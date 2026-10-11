using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileTransferPoc.Client.Auth;

/// <summary>
/// Obtains access tokens with the OAuth2 JWT-bearer grant (RFC 7523) as Maskinporten does:
/// the client signs a short-lived assertion and exchanges it at the token endpoint.
/// </summary>
public sealed class MaskinportenTokenProvider : ITokenProvider
{
    private const string JwtBearerGrant = "urn:ietf:params:oauth:grant-type:jwt-bearer";

    private readonly HttpClient _http;
    private readonly MaskinportenOptions _options;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _token;
    private DateTimeOffset _expiresAt;

    public MaskinportenTokenProvider(HttpClient http, MaskinportenOptions options, TimeProvider? time = null)
    {
        _http = http;
        _options = options;
        _time = time ?? TimeProvider.System;
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_token is not null && _time.GetUtcNow() < _expiresAt)
            {
                return _token;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, _options.TokenEndpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = JwtBearerGrant,
                    ["assertion"] = CreateAssertion(),
                }),
            };

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new TokenRequestException(
                    $"Token endpoint returned {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
            }

            var token = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(token?.AccessToken))
            {
                throw new TokenRequestException("Token endpoint response did not contain an access_token.");
            }

            _token = token.AccessToken;
            var lifetime = TimeSpan.FromSeconds(token.ExpiresIn > 0 ? token.ExpiresIn : 60);
            _expiresAt = _time.GetUtcNow() + lifetime - _options.ExpirySkew;
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

    internal string CreateAssertion()
    {
        var now = _time.GetUtcNow();
        var header = new Dictionary<string, object> { ["alg"] = "RS256", ["typ"] = "JWT" };
        if (_options.KeyId is not null)
        {
            header["kid"] = _options.KeyId;
        }

        var payload = new Dictionary<string, object>
        {
            ["iss"] = _options.ClientId,
            ["aud"] = _options.Audience ?? _options.TokenEndpoint.GetLeftPart(UriPartial.Authority) + "/",
            ["scope"] = _options.Scope,
            ["iat"] = now.ToUnixTimeSeconds(),
            ["exp"] = (now + _options.AssertionLifetime).ToUnixTimeSeconds(),
            ["jti"] = Guid.NewGuid().ToString(),
        };

        var signingInput = Base64Url(JsonSerializer.SerializeToUtf8Bytes(header)) + "." +
                           Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload));
        var signature = _options.SigningKey.SignData(
            Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return signingInput + "." + Base64Url(signature);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("token_type")] string? TokenType,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("scope")] string? Scope);
}

public sealed class TokenRequestException(string message) : Exception(message);
