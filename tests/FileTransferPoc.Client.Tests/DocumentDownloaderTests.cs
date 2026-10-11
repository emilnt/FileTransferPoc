using System.Net;
using System.Security.Cryptography;
using FileTransferPoc.Client.Download;

namespace FileTransferPoc.Client.Tests;

public sealed class DocumentDownloaderTests : IDisposable
{
    private static readonly Uri FileUri = new("https://api.example.no/filer/v1/nedlasting/550e8400-e29b-41d4-a716-446655440000");

    private readonly string _dir = Directory.CreateTempSubdirectory("esas-client-tests").FullName;
    private readonly byte[] _content = RandomNumberGenerator.GetBytes(200_000);
    private readonly List<TimeSpan> _delays = [];
    private readonly FakeTokenProvider _tokens = new();

    private string Destination => Path.Combine(_dir, "document.bin");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private DocumentDownloader CreateDownloader(FakeFileServer server, DownloadOptions? options = null) =>
        new(new HttpClient(server), _tokens, options ?? new DownloadOptions { InitialBackoff = TimeSpan.FromSeconds(1) },
            (delay, _) =>
            {
                _delays.Add(delay);
                return Task.CompletedTask;
            });

    private Checksum Sha256Of(byte[] bytes) => Checksum.Parse("sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes)));

    [Fact]
    public async Task Downloads_whole_file_with_bearer_token_and_verifies_checksum()
    {
        var server = new FakeFileServer(_content);

        var result = await CreateDownloader(server).DownloadAsync(FileUri, Destination, Sha256Of(_content));

        Assert.True(result.ChecksumVerified);
        Assert.Equal(_content.Length, result.Length);
        Assert.Equal(_content, await File.ReadAllBytesAsync(Destination));
        Assert.False(File.Exists(Destination + ".part"));
        var request = Assert.Single(server.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("token-1", request.Headers.Authorization?.Parameter);
        Assert.Null(request.Headers.Range);
    }

    [Fact]
    public async Task Resumes_with_range_after_connection_drops()
    {
        var server = new FakeFileServer(_content).ThenCutAfter(70_000);

        await CreateDownloader(server).DownloadAsync(FileUri, Destination, Sha256Of(_content));

        Assert.Equal(_content, await File.ReadAllBytesAsync(Destination));
        Assert.Equal(2, server.Requests.Count);
        var resume = server.Requests[1].Headers.Range!.Ranges.Single();
        Assert.Equal(70_000, resume.From);
        Assert.Null(resume.To);
        Assert.Single(_delays);
    }

    [Fact]
    public async Task Resumes_from_part_file_left_by_an_earlier_run()
    {
        await File.WriteAllBytesAsync(Destination + ".part", _content[..123_456]);
        var server = new FakeFileServer(_content);

        await CreateDownloader(server).DownloadAsync(FileUri, Destination, Sha256Of(_content));

        Assert.Equal(_content, await File.ReadAllBytesAsync(Destination));
        Assert.Equal(123_456, Assert.Single(server.Requests).Headers.Range!.Ranges.Single().From);
    }

    [Fact]
    public async Task Finishes_when_part_file_is_already_complete_and_server_answers_416()
    {
        await File.WriteAllBytesAsync(Destination + ".part", _content);
        var server = new FakeFileServer(_content);

        var result = await CreateDownloader(server).DownloadAsync(FileUri, Destination, Sha256Of(_content));

        Assert.True(result.ChecksumVerified);
        Assert.Equal(_content, await File.ReadAllBytesAsync(Destination));
    }

    [Fact]
    public async Task Starts_over_when_server_ignores_range_and_returns_200()
    {
        await File.WriteAllBytesAsync(Destination + ".part", RandomNumberGenerator.GetBytes(50_000));
        var server = new FakeFileServer(_content).ThenFullFile();

        await CreateDownloader(server).DownloadAsync(FileUri, Destination, Sha256Of(_content));

        Assert.Equal(_content, await File.ReadAllBytesAsync(Destination));
    }

    [Fact]
    public async Task Downloads_in_chunks_when_chunk_size_is_set()
    {
        var server = new FakeFileServer(_content);

        await CreateDownloader(server, new DownloadOptions { ChunkSize = 64_000 })
            .DownloadAsync(FileUri, Destination, Sha256Of(_content));

        Assert.Equal(_content, await File.ReadAllBytesAsync(Destination));
        Assert.Equal(new long?[] { 0, 64_000, 128_000, 192_000 },
            server.Requests.Select(r => r.Headers.Range!.Ranges.Single().From));
    }

    [Fact]
    public async Task Small_file_inside_default_range_comes_back_as_200()
    {
        var small = RandomNumberGenerator.GetBytes(1_000);
        var server = new FakeFileServer(small);

        var result = await CreateDownloader(server, new DownloadOptions { ChunkSize = 64_000 })
            .DownloadAsync(FileUri, Destination, Sha256Of(small));

        Assert.Equal(1_000, result.Length);
        Assert.Single(server.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Retries_500_and_503_with_exponential_backoff(HttpStatusCode status)
    {
        var server = new FakeFileServer(_content).ThenStatus(status).ThenStatus(status).ThenStatus(status);

        await CreateDownloader(server).DownloadAsync(FileUri, Destination);

        Assert.Equal(_content, await File.ReadAllBytesAsync(Destination));
        Assert.Equal(4, server.Requests.Count);
        Assert.Equal(3, _delays.Count);
        // 1s, 2s, 4s with ±20% jitter.
        Assert.InRange(_delays[0].TotalSeconds, 0.8, 1.2);
        Assert.InRange(_delays[1].TotalSeconds, 1.6, 2.4);
        Assert.InRange(_delays[2].TotalSeconds, 3.2, 4.8);
    }

    [Fact]
    public async Task Honours_retry_after_on_503()
    {
        var server = new FakeFileServer(_content).ThenStatus(HttpStatusCode.ServiceUnavailable, TimeSpan.FromSeconds(7));

        await CreateDownloader(server).DownloadAsync(FileUri, Destination);

        Assert.Equal(TimeSpan.FromSeconds(7), Assert.Single(_delays));
    }

    [Fact]
    public async Task Gives_up_after_max_retries()
    {
        var server = new FakeFileServer(_content);
        for (var i = 0; i < 10; i++)
        {
            server.ThenStatus(HttpStatusCode.ServiceUnavailable);
        }

        var ex = await Assert.ThrowsAsync<DownloadException>(() =>
            CreateDownloader(server, new DownloadOptions { MaxRetries = 3 }).DownloadAsync(FileUri, Destination));

        Assert.Contains("Giving up after 3 retries", ex.Message);
        Assert.Equal(4, server.Requests.Count);
        Assert.False(File.Exists(Destination));
    }

    [Fact]
    public async Task Backoff_is_capped()
    {
        var downloader = CreateDownloader(new FakeFileServer(_content), new DownloadOptions
        {
            InitialBackoff = TimeSpan.FromSeconds(1),
            MaxBackoff = TimeSpan.FromSeconds(10),
        });

        Assert.InRange(downloader.Backoff(20).TotalSeconds, 8, 12);
    }

    [Fact]
    public async Task Refreshes_token_once_on_401()
    {
        var server = new FakeFileServer(_content) { ExpectedToken = "token-2" };

        await CreateDownloader(server).DownloadAsync(FileUri, Destination);

        Assert.Equal(1, _tokens.Invalidations);
        Assert.Equal(["token-1", "token-2"], server.Requests.Select(r => r.Headers.Authorization!.Parameter!));
    }

    [Fact]
    public async Task Fails_on_repeated_401()
    {
        var server = new FakeFileServer(_content) { ExpectedToken = "never" };

        var ex = await Assert.ThrowsAsync<DownloadException>(() => CreateDownloader(server).DownloadAsync(FileUri, Destination));

        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.Equal(2, server.Requests.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.MethodNotAllowed)]
    public async Task Does_not_retry_client_errors(HttpStatusCode status)
    {
        var server = new FakeFileServer(_content).ThenStatus(status);

        var ex = await Assert.ThrowsAsync<DownloadException>(() => CreateDownloader(server).DownloadAsync(FileUri, Destination));

        Assert.Equal(status, ex.StatusCode);
        Assert.Single(server.Requests);
        Assert.Empty(_delays);
    }

    [Fact]
    public async Task Checksum_mismatch_discards_the_file()
    {
        var server = new FakeFileServer(_content);
        var wrong = Checksum.Parse("sha256:" + new string('0', 64));

        var ex = await Assert.ThrowsAsync<DownloadException>(() => CreateDownloader(server).DownloadAsync(FileUri, Destination, wrong));

        Assert.Contains("Checksum mismatch", ex.Message);
        Assert.False(File.Exists(Destination));
        Assert.False(File.Exists(Destination + ".part"));
    }

    [Fact]
    public async Task Repeated_interruptions_that_make_progress_do_not_exhaust_retries()
    {
        var server = new FakeFileServer(_content);
        for (var i = 0; i < 5; i++)
        {
            server.ThenCutAfter(30_000);
        }

        await CreateDownloader(server, new DownloadOptions { MaxRetries = 2 }).DownloadAsync(FileUri, Destination, Sha256Of(_content));

        Assert.Equal(_content, await File.ReadAllBytesAsync(Destination));
        Assert.Equal(6, server.Requests.Count);
    }
}
