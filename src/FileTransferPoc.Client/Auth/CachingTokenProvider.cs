using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace FileTransferPoc.Client.Auth;

/// <summary>
/// Posts a form to an OAuth2 token endpoint and caches the access token until shortly before it expires.
/// Subclasses decide what the form contains.
/// </summary>
public abstract class CachingTokenProvider : ITokenProvider
{
    private readonly HttpClient _http;
    private readonly Uri _tokenEndpoint;
    private readonly TimeSpan _expirySkew;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _token;
    private DateTimeOffset _expiresAt;

    protected CachingTokenProvider(HttpClient http, Uri tokenEndpoint, TimeSpan expirySkew, TimeProvider? time)
    {
        _http = http;
        _tokenEndpoint = tokenEndpoint;
        _expirySkew = expirySkew;
        Time = time ?? TimeProvider.System;
    }

    protected TimeProvider Time { get; }

    /// <summary>Form fields for one token request.</summary>
    protected abstract IEnumerable<KeyValuePair<string, string>> CreateTokenRequest();

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_token is not null && Time.GetUtcNow() < _expiresAt)
            {
                return _token;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, _tokenEndpoint)
            {
                Content = new FormUrlEncodedContent(CreateTokenRequest()),
            };

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new TokenRequestException(
                    $"Token endpoint {_tokenEndpoint} returned {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
            }

            var token = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(token?.AccessToken))
            {
                throw new TokenRequestException("Token endpoint response did not contain an access_token.");
            }

            _token = token.AccessToken;
            var lifetime = TimeSpan.FromSeconds(token.ExpiresIn > 0 ? token.ExpiresIn : 60);
            _expiresAt = Time.GetUtcNow() + lifetime - _expirySkew;
            return _token;
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Invalidate()
    {
        _lock.Wait();
        try
        {
            _token = null;
        }
        finally
        {
            _lock.Release();
        }
    }

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}

public sealed class TokenRequestException(string message) : Exception(message);
