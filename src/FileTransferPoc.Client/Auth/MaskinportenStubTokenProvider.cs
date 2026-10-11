namespace FileTransferPoc.Client.Auth;

/// <summary>
/// Gets tokens from the Web API's Maskinporten stand-in (<c>POST /maskinporten-stub/token</c> with
/// <c>consumer_org</c> and <c>scope</c>), which the API exposes in Development only.
/// </summary>
public sealed class MaskinportenStubTokenProvider(
    HttpClient http, Uri tokenEndpoint, string consumerOrganization, string scope, TimeProvider? time = null)
    : CachingTokenProvider(http, tokenEndpoint, TimeSpan.FromSeconds(30), time)
{
    protected override IEnumerable<KeyValuePair<string, string>> CreateTokenRequest() =>
    [
        new("consumer_org", consumerOrganization),
        new("scope", scope),
    ];
}
