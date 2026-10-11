using System.Security.Cryptography;

namespace FileTransferPoc.Client.Auth;

/// <summary>Settings for the Maskinporten JWT-bearer grant.</summary>
public sealed class MaskinportenClientOptions
{
    /// <summary>Token endpoint, e.g. https://test.maskinporten.no/token.</summary>
    public required Uri TokenEndpoint { get; init; }

    /// <summary>Audience for the client assertion. Maskinporten expects the issuer URL; defaults to the token endpoint's origin.</summary>
    public string? Audience { get; init; }

    /// <summary>The integration's client_id, used as <c>iss</c> in the assertion.</summary>
    public required string ClientId { get; init; }

    /// <summary>Space separated scopes to request.</summary>
    public required string Scope { get; init; }

    /// <summary>Key used to sign the assertion (RS256).</summary>
    public required RSA SigningKey { get; init; }

    /// <summary>Optional key id placed in the JWT header.</summary>
    public string? KeyId { get; init; }

    /// <summary>Lifetime of the client assertion. Maskinporten allows at most 120 seconds.</summary>
    public TimeSpan AssertionLifetime { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>Refresh this long before the access token actually expires.</summary>
    public TimeSpan ExpirySkew { get; init; } = TimeSpan.FromSeconds(30);
}
