using System.Text;
using Microsoft.Extensions.Options;

namespace FileTransferPoc.Api.Storage;

/// <summary>Publishes a couple of fixed sample files on startup when <see cref="StorageOptions.SeedSampleData"/> is on.</summary>
public sealed class SampleDataSeeder(
    IFileStore store,
    IOptions<StorageOptions> options,
    TimeProvider timeProvider,
    ILogger<SampleDataSeeder> logger) : IHostedService
{
    public const string SampleOrganization = "123456789";
    public const string SampleServiceName = "TestDomain";
    public static readonly Guid SmallFileId = Guid.Parse("550e8400-e29b-41d4-a716-446655440000");
    public static readonly Guid LargeFileId = Guid.Parse("6f1c2a7e-3b4d-4e5f-8a9b-0c1d2e3f4a5b");

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.SeedSampleData)
        {
            return;
        }

        var text = Encoding.UTF8.GetBytes("Hei fra FileTransferPoc. Dette er en liten testfil.\n");
        await SeedAsync(SmallFileId, text, cancellationToken);

        // 10 MiB of deterministic bytes, enough to exercise ranges and resumed downloads.
        var large = new byte[10 * 1024 * 1024];
        new Random(42).NextBytes(large);
        await SeedAsync(LargeFileId, large, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task SeedAsync(Guid id, byte[] content, CancellationToken cancellationToken)
    {
        if (await store.FindAsync(id, cancellationToken) is not null)
        {
            return;
        }

        using var stream = new MemoryStream(content);
        await store.PublishAsync(id, SampleServiceName, stream, [SampleOrganization], timeProvider.GetUtcNow(), cancellationToken);
        logger.LogInformation("Seeded sample file {FileId} ({Length} bytes) in service {ServiceName} for organization {OrganizationNumber}",
            id, content.Length, SampleServiceName, SampleOrganization);
    }
}
