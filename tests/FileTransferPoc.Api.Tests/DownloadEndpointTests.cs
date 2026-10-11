using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;
using FileTransferPoc.Api.Storage;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace FileTransferPoc.Api.Tests;

public class DownloadEndpointTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly byte[] Content = Enumerable.Range(0, 1000).Select(i => (byte)(i % 251)).ToArray();

    private static string Url(Guid id) => $"/filer/v1/nedlasting/{id:D}";

    [Fact]
    public async Task Get_without_range_returns_the_whole_file()
    {
        var id = await factory.PublishAsync(Content);

        var response = await factory.AuthorizedClient().GetAsync(Url(id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("bytes", Assert.Single(response.Headers.AcceptRanges));
        Assert.Equal($"\"{id:D}\"", response.Headers.ETag?.Tag);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Content.Length, response.Content.Headers.ContentLength);
        Assert.Equal(Content, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Single_range_returns_206_with_content_range()
    {
        var id = await factory.PublishAsync(Content);

        var response = await SendWithRange(id, 100, 199);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(new ContentRangeHeaderValue(100, 199, Content.Length), response.Content.Headers.ContentRange);
        Assert.Equal(100, response.Content.Headers.ContentLength);
        Assert.Equal(Content[100..200], await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Resuming_from_an_offset_returns_the_rest_of_the_file()
    {
        var id = await factory.PublishAsync(Content);

        var response = await SendWithRange(id, 900, 4999);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(new ContentRangeHeaderValue(900, 999, Content.Length), response.Content.Headers.ContentRange);
        Assert.Equal(Content[900..], await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Range_larger_than_the_file_returns_200_with_the_whole_file()
    {
        var id = await factory.PublishAsync(Content);

        var response = await SendWithRange(id, 0, 1024 * 1024);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(response.Content.Headers.ContentRange);
        Assert.Equal(Content, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Range_outside_the_file_returns_416_with_the_file_length()
    {
        var id = await factory.PublishAsync(Content);

        var response = await SendWithRange(id, 5000, 6000);

        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, response.StatusCode);
        Assert.Equal("bytes */1000", response.Content.Headers.GetValues("Content-Range").Single());
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("550e8400e29b41d4a716446655440000")]
    [InlineData("{550e8400-e29b-41d4-a716-446655440000}")]
    public async Task Malformed_guid_returns_400(string id)
    {
        var response = await factory.AuthorizedClient().GetAsync($"/filer/v1/nedlasting/{id}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Missing_token_returns_401()
    {
        var id = await factory.PublishAsync(Content);

        var response = await factory.CreateClient().GetAsync(Url(id));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.Single().Scheme);
    }

    [Fact]
    public async Task Token_with_wrong_signature_returns_401()
    {
        var id = await factory.PublishAsync(Content);
        var token = factory.Token();
        var tampered = token[..^4] + (token.EndsWith("AAAA") ? "BBBB" : "AAAA");
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tampered);

        var response = await client.GetAsync(Url(id));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Expired_token_returns_401()
    {
        var id = await factory.PublishAsync(Content);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.Token(lifetime: TimeSpan.FromMinutes(-5)));

        var response = await client.GetAsync(Url(id));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Token_without_the_required_scope_returns_403()
    {
        var id = await factory.PublishAsync(Content);

        var response = await factory.AuthorizedClient(scope: "some:other").GetAsync(Url(id));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Organization_not_allowed_for_the_file_returns_403()
    {
        var id = await factory.PublishAsync(Content);

        var response = await factory.AuthorizedClient(ApiFactory.OtherOrg).GetAsync(Url(id));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Any_listed_organization_may_fetch_the_file()
    {
        var id = await factory.PublishAsync(Content, null, ApiFactory.AllowedOrg, ApiFactory.OtherOrg);

        var response = await factory.AuthorizedClient(ApiFactory.OtherOrg).GetAsync(Url(id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_guid_returns_404()
    {
        var response = await factory.AuthorizedClient().GetAsync(Url(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task File_past_retention_returns_404()
    {
        var id = await factory.PublishAsync(Content, DateTimeOffset.UtcNow.AddDays(-16));

        var response = await factory.AuthorizedClient().GetAsync(Url(id));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task File_within_retention_is_still_available()
    {
        var id = await factory.PublishAsync(Content, DateTimeOffset.UtcNow.AddDays(-14));

        var response = await factory.AuthorizedClient().GetAsync(Url(id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task File_can_be_fetched_several_times()
    {
        var id = await factory.PublishAsync(Content);
        var client = factory.AuthorizedClient();

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(Content, await client.GetByteArrayAsync(Url(id)));
        }
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task Methods_other_than_get_return_405(string method)
    {
        var id = await factory.PublishAsync(Content);

        var response = await factory.AuthorizedClient().SendAsync(new HttpRequestMessage(new HttpMethod(method), Url(id)));

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Contains("GET", response.Content.Headers.Allow);
    }

    [Fact]
    public async Task Unexpected_failure_returns_500_problem()
    {
        using var failing = factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<IFileStore, ThrowingFileStore>()));
        var client = failing.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.Token());

        var response = await client.GetAsync(Url(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Maintenance_mode_returns_503_with_retry_after()
    {
        using var maintenance = factory.WithWebHostBuilder(builder => builder.UseSetting("Service:MaintenanceMode", "true"));
        var client = maintenance.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.Token());

        var response = await client.GetAsync(Url(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.NotNull(response.Headers.RetryAfter);
    }

    [Fact]
    public async Task Requests_are_logged_with_organization_and_source_ip()
    {
        var id = await factory.PublishAsync(Content);
        var token = factory.Token();

        var context = await factory.Server.SendAsync(http =>
        {
            http.Request.Method = "GET";
            http.Request.Path = Url(id);
            http.Request.Headers.Authorization = $"Bearer {token}";
            http.Request.Headers.Range = "bytes=0-9";
            http.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7");
        });

        Assert.Equal(StatusCodes.Status206PartialContent, context.Response.StatusCode);
        Assert.Contains(factory.Logs, line =>
            line.Contains(id.ToString("D"))
            && line.Contains("203.0.113.7")
            && line.Contains($"organization {ApiFactory.AllowedOrg}")
            && line.Contains("answered 206"));
    }

    [Fact]
    public async Task Rejected_requests_are_logged_too()
    {
        var id = await factory.PublishAsync(Content);

        await factory.CreateClient().GetAsync(Url(id));

        Assert.Contains(factory.Logs, line => line.Contains(id.ToString("D")) && line.Contains("unauthenticated") && line.Contains("answered 401"));
    }

    private async Task<HttpResponseMessage> SendWithRange(Guid id, long from, long to)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, Url(id));
        request.Headers.Range = new RangeHeaderValue(from, to);
        return await factory.AuthorizedClient().SendAsync(request);
    }

    private sealed class ThrowingFileStore : IFileStore
    {
        public Task<StoredFile?> FindAsync(Guid id, CancellationToken cancellationToken) =>
            throw new IOException("Disk on fire");

        public Task<FileRecord> PublishAsync(Guid id, string serviceName, Stream content, IReadOnlyList<string> allowedOrganizations, DateTimeOffset publishedAt, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
