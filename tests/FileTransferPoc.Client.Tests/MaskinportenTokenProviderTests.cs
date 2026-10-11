using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FileTransferPoc.Client.Auth;
using Microsoft.Extensions.Time.Testing;

namespace FileTransferPoc.Client.Tests;

public sealed class MaskinportenTokenProviderTests
{
    private static readonly Uri TokenEndpoint = new("https://maskinporten.stub/token");

    private readonly RSA _key = RSA.Create(2048);
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-11T12:00:00Z"));

    private MaskinportenTokenProvider Create(TokenEndpointStub stub) =>
        new(new HttpClient(stub), new MaskinportenOptions
        {
            TokenEndpoint = TokenEndpoint,
            ClientId = "client-123",
            Scope = "esas:dokument.les",
            SigningKey = _key,
            KeyId = "kid-1",
        }, _time);

    [Fact]
    public async Task Posts_signed_jwt_bearer_assertion()
    {
        var stub = new TokenEndpointStub();

        var token = await Create(stub).GetAccessTokenAsync();

        Assert.Equal("access-1", token);
        var form = Assert.Single(stub.Forms);
        Assert.Equal("urn:ietf:params:oauth:grant-type:jwt-bearer", form["grant_type"]);

        var parts = form["assertion"].Split('.');
        Assert.Equal(3, parts.Length);
        Assert.True(_key.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), Base64UrlDecode(parts[2]),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));

        using var header = JsonDocument.Parse(Base64UrlDecode(parts[0]));
        Assert.Equal("RS256", header.RootElement.GetProperty("alg").GetString());
        Assert.Equal("kid-1", header.RootElement.GetProperty("kid").GetString());

        using var claims = JsonDocument.Parse(Base64UrlDecode(parts[1]));
        var root = claims.RootElement;
        Assert.Equal("client-123", root.GetProperty("iss").GetString());
        Assert.Equal("https://maskinporten.stub/", root.GetProperty("aud").GetString());
        Assert.Equal("esas:dokument.les", root.GetProperty("scope").GetString());
        Assert.Equal(120, root.GetProperty("exp").GetInt64() - root.GetProperty("iat").GetInt64());
        Assert.True(Guid.TryParse(root.GetProperty("jti").GetString(), out _));
    }

    [Fact]
    public async Task Caches_token_until_shortly_before_expiry()
    {
        var stub = new TokenEndpointStub { ExpiresIn = 120 };
        var provider = Create(stub);

        Assert.Equal("access-1", await provider.GetAccessTokenAsync());
        _time.Advance(TimeSpan.FromSeconds(89));
        Assert.Equal("access-1", await provider.GetAccessTokenAsync());
        _time.Advance(TimeSpan.FromSeconds(2)); // inside the 30s skew
        Assert.Equal("access-2", await provider.GetAccessTokenAsync());
    }

    [Fact]
    public async Task Invalidate_forces_a_new_token()
    {
        var provider = Create(new TokenEndpointStub());

        await provider.GetAccessTokenAsync();
        provider.Invalidate();

        Assert.Equal("access-2", await provider.GetAccessTokenAsync());
    }

    [Fact]
    public async Task Reports_token_endpoint_errors()
    {
        var stub = new TokenEndpointStub { Status = HttpStatusCode.BadRequest };

        var ex = await Assert.ThrowsAsync<TokenRequestException>(() => Create(stub).GetAccessTokenAsync());

        Assert.Contains("400", ex.Message);
        Assert.Contains("invalid_grant", ex.Message);
    }

    private static byte[] Base64UrlDecode(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }

    private sealed class TokenEndpointStub : HttpMessageHandler
    {
        private int _issued;

        public List<Dictionary<string, string>> Forms { get; } = [];
        public int ExpiresIn { get; init; } = 120;
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(TokenEndpoint, request.RequestUri);
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Forms.Add(body.Split('&').Select(p => p.Split('=', 2))
                .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1])));

            if (Status != HttpStatusCode.OK)
            {
                return new HttpResponseMessage(Status) { Content = new StringContent("""{"error":"invalid_grant"}""") };
            }

            _issued++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { access_token = $"access-{_issued}", token_type = "Bearer", expires_in = ExpiresIn, scope = "esas:dokument.les" }),
            };
        }
    }
}
