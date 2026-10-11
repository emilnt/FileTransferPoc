namespace FileTransferPoc.Api.Auth;

public sealed class MaskinportenOptions
{
    public const string SectionName = "Maskinporten";

    /// <summary>
    /// Real Maskinporten base URL (for example https://test.maskinporten.no/). When set, signing keys come
    /// from its OpenID metadata and <see cref="SigningKey"/> is ignored.
    /// </summary>
    public string? Authority { get; set; }

    /// <summary>Expected <c>iss</c> of access tokens.</summary>
    public string Issuer { get; set; } = "https://maskinporten-stub.local/";

    /// <summary>Optional expected <c>aud</c>. Maskinporten only sets one when the client asks for a resource.</summary>
    public string? Audience { get; set; }

    /// <summary>Symmetric key (at least 32 characters) the stub issuer signs with and the API validates against.</summary>
    public string? SigningKey { get; set; }

    /// <summary>Scope a token must carry to call the download endpoint.</summary>
    public string RequiredScope { get; set; } = "esas:filer";

    /// <summary>Exposes <c>POST /maskinporten-stub/token</c>, a local stand-in for Maskinporten. Never enable in production.</summary>
    public bool EnableStubIssuer { get; set; }
}
