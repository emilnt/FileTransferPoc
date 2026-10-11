using System.Security.Claims;
using System.Text.Json;

namespace FileTransferPoc.Api.Auth;

/// <summary>Reads the Maskinporten claims the API cares about.</summary>
public static class MaskinportenClaims
{
    public const string Consumer = "consumer";
    public const string Scope = "scope";

    /// <summary>ISO 6523 prefix Maskinporten uses for Norwegian organization numbers.</summary>
    public const string OrganizationNumberPrefix = "0192:";

    /// <summary>
    /// The calling organization from <c>consumer</c>, which Maskinporten issues as
    /// <c>{"authority":"iso6523-actorid-upis","ID":"0192:123456789"}</c>. Returns the 9 digits, or null.
    /// </summary>
    public static string? GetOrganizationNumber(this ClaimsPrincipal user)
    {
        var consumer = user.FindFirst(Consumer)?.Value;
        if (string.IsNullOrEmpty(consumer))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(consumer);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("ID", out var id)
                && id.GetString() is { } value
                && value.StartsWith(OrganizationNumberPrefix, StringComparison.Ordinal))
            {
                return value[OrganizationNumberPrefix.Length..];
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    public static bool HasScope(this ClaimsPrincipal user, string scope) =>
        user.FindAll(Scope)
            .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Contains(scope, StringComparer.Ordinal);
}
