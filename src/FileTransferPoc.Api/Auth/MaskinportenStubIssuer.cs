using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace FileTransferPoc.Api.Auth;

/// <summary>
/// Issues tokens shaped like Maskinporten access tokens (<c>consumer</c>, <c>scope</c>, <c>client_id</c>),
/// signed with the configured symmetric key. Stands in for Maskinporten in the POC and in tests.
/// </summary>
public sealed class MaskinportenStubIssuer(IOptions<MaskinportenOptions> options, TimeProvider timeProvider)
{
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(2);

    public string CreateToken(string organizationNumber, string scope, TimeSpan? lifetime = null)
    {
        var settings = options.Value;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var claims = new Dictionary<string, object>
        {
            [MaskinportenClaims.Scope] = scope,
            ["client_id"] = $"stub-client-{organizationNumber}",
            ["client_amr"] = "private_key_jwt",
            ["token_type"] = "Bearer",
            ["jti"] = Guid.NewGuid().ToString("N"),
            [MaskinportenClaims.Consumer] = new Dictionary<string, string>
            {
                ["authority"] = "iso6523-actorid-upis",
                ["ID"] = MaskinportenClaims.OrganizationNumberPrefix + organizationNumber,
            },
        };

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = settings.Issuer,
            Audience = settings.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = now + (lifetime ?? DefaultLifetime),
            Claims = claims,
            SigningCredentials = new SigningCredentials(CreateSigningKey(settings), SecurityAlgorithms.HmacSha256),
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    public static SymmetricSecurityKey CreateSigningKey(MaskinportenOptions settings)
    {
        if (string.IsNullOrEmpty(settings.SigningKey) || settings.SigningKey.Length < 32)
        {
            throw new InvalidOperationException("Maskinporten:SigningKey must be at least 32 characters when Maskinporten:Authority is not set.");
        }

        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.SigningKey));
    }
}
