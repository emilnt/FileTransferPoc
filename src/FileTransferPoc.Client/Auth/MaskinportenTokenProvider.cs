using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FileTransferPoc.Client.Auth;

/// <summary>
/// Obtains access tokens from Maskinporten with the OAuth2 JWT-bearer grant (RFC 7523):
/// the client signs a short-lived assertion and exchanges it at the token endpoint.
/// </summary>
public sealed class MaskinportenTokenProvider(HttpClient http, MaskinportenClientOptions options, TimeProvider? time = null)
    : CachingTokenProvider(http, options.TokenEndpoint, options.ExpirySkew, time)
{
    private const string JwtBearerGrant = "urn:ietf:params:oauth:grant-type:jwt-bearer";

    protected override IEnumerable<KeyValuePair<string, string>> CreateTokenRequest() =>
    [
        new("grant_type", JwtBearerGrant),
        new("assertion", CreateAssertion()),
    ];

    internal string CreateAssertion()
    {
        var now = Time.GetUtcNow();
        var header = new Dictionary<string, object> { ["alg"] = "RS256", ["typ"] = "JWT" };
        if (options.KeyId is not null)
        {
            header["kid"] = options.KeyId;
        }

        var payload = new Dictionary<string, object>
        {
            ["iss"] = options.ClientId,
            ["aud"] = options.Audience ?? options.TokenEndpoint.GetLeftPart(UriPartial.Authority) + "/",
            ["scope"] = options.Scope,
            ["iat"] = now.ToUnixTimeSeconds(),
            ["exp"] = (now + options.AssertionLifetime).ToUnixTimeSeconds(),
            ["jti"] = Guid.NewGuid().ToString(),
        };

        var signingInput = Base64Url(JsonSerializer.SerializeToUtf8Bytes(header)) + "." +
                           Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload));
        var signature = options.SigningKey.SignData(
            Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return signingInput + "." + Base64Url(signature);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
