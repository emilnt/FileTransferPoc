using System.Net;
using System.Security.Cryptography;
using FileTransferPoc.Api.Auth;
using FileTransferPoc.Api.Storage;
using FileTransferPoc.Client.Auth;
using FileTransferPoc.Client.Download;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FileTransferPoc.Client.Tests;

/// <summary>Runs the client against the real Web API in memory, with its Maskinporten stub and seeded sample files.</summary>
public sealed class EndToEndTests : IClassFixture<EndToEndTests.SeededApi>, IDisposable
{
    private static readonly Uri BaseAddress = new("http://localhost/");

    private readonly SeededApi _api;
    private readonly string _dir = Directory.CreateTempSubdirectory("esas-e2e").FullName;

    public EndToEndTests(SeededApi api) => _api = api;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static byte[] LargeSample()
    {
        // Same bytes SampleDataSeeder publishes.
        var large = new byte[10 * 1024 * 1024];
        new Random(42).NextBytes(large);
        return large;
    }

    private static Uri FileUri(Guid id) => new(BaseAddress, $"filer/v1/nedlasting/{id}");

    private DocumentDownloader CreateDownloader(HttpMessageHandler handler, string org = SampleDataSeeder.SampleOrganization, DownloadOptions? options = null)
    {
        var http = new HttpClient(handler) { BaseAddress = BaseAddress };
        var tokens = new MaskinportenStubTokenProvider(http, new Uri(BaseAddress, "maskinporten-stub/token"), org, "esas:filer");
        return new DocumentDownloader(http, tokens, options, (_, _) => Task.CompletedTask);
    }

    [Fact]
    public async Task Downloads_seeded_file_in_chunks_and_verifies_checksum()
    {
        var expected = LargeSample();
        var destination = Path.Combine(_dir, "large.bin");
        var checksum = Checksum.Parse("sha256:" + Convert.ToHexStringLower(SHA256.HashData(expected)));

        var result = await CreateDownloader(_api.Server.CreateHandler(), options: new DownloadOptions { ChunkSize = 3 * 1024 * 1024 })
            .DownloadAsync(FileUri(SampleDataSeeder.LargeFileId), destination, checksum);

        Assert.True(result.ChecksumVerified);
        Assert.Equal(expected, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task Resumes_after_the_connection_drops_mid_file()
    {
        var expected = LargeSample();
        var destination = Path.Combine(_dir, "resumed.bin");
        var cutting = new CutFirstResponse(4 * 1024 * 1024) { InnerHandler = _api.Server.CreateHandler() };
        var downloader = CreateDownloader(cutting);
        var retries = 0;
        downloader.Retrying += (_, _, _) => retries++;

        await downloader.DownloadAsync(FileUri(SampleDataSeeder.LargeFileId), destination,
            Checksum.Parse("sha256:" + Convert.ToHexStringLower(SHA256.HashData(expected))));

        Assert.Equal(1, retries);
        Assert.Equal(expected, await File.ReadAllBytesAsync(destination));
        Assert.Equal(4 * 1024 * 1024, cutting.ResumedFrom);
    }

    [Fact]
    public async Task Other_organization_gets_403()
    {
        var ex = await Assert.ThrowsAsync<DownloadException>(() =>
            CreateDownloader(_api.Server.CreateHandler(), org: "987654321")
                .DownloadAsync(FileUri(SampleDataSeeder.SmallFileId), Path.Combine(_dir, "x")));

        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
    }

    [Fact]
    public async Task Unknown_guid_gets_404()
    {
        var ex = await Assert.ThrowsAsync<DownloadException>(() =>
            CreateDownloader(_api.Server.CreateHandler()).DownloadAsync(FileUri(Guid.NewGuid()), Path.Combine(_dir, "x")));

        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
    }

    public sealed class SeededApi : WebApplicationFactory<MaskinportenStubIssuer>
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "ftpoc-client-e2e-" + Guid.NewGuid().ToString("N"));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Storage:RootPath", _root);
            builder.UseSetting("Storage:SeedSampleData", "true");
            builder.UseSetting("Maskinporten:SigningKey", "test-signing-key-that-is-long-enough-0123456789");
            builder.UseSetting("Maskinporten:EnableStubIssuer", "true");
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }

    /// <summary>Lets the first download response deliver only part of its body, then fails like a dropped connection.</summary>
    private sealed class CutFirstResponse(int bytes) : DelegatingHandler
    {
        private bool _cut;

        public long? ResumedFrom { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (!request.RequestUri!.AbsolutePath.StartsWith("/filer/", StringComparison.Ordinal))
            {
                return response;
            }

            if (_cut)
            {
                ResumedFrom ??= request.Headers.Range?.Ranges.Single().From;
                return response;
            }

            _cut = true;
            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            var headers = response.Content.Headers.ToList();
            response.Content = new StreamContent(new TruncatedStream(body, bytes));
            foreach (var header in headers)
            {
                response.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            return response;
        }
    }

    private sealed class TruncatedStream(byte[] data, int cutAfter) : MemoryStream(data, 0, cutAfter)
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = base.Read(buffer, offset, count);
            return read > 0 ? read : throw new IOException("The response ended prematurely.");
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await base.ReadAsync(buffer, cancellationToken);
            return read > 0 ? read : throw new IOException("The response ended prematurely.");
        }
    }
}
