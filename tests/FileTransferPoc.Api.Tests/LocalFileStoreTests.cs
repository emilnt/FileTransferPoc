using FileTransferPoc.Api.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace FileTransferPoc.Api.Tests;

public class LocalFileStoreTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Published_record_round_trips_with_its_service_name()
    {
        var publishedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var id = await factory.PublishAsync([1, 2, 3], publishedAt, ApiFactory.AllowedOrg, ApiFactory.OtherOrg);

        var file = await factory.Services.GetRequiredService<IFileStore>().FindAsync(id, CancellationToken.None);

        Assert.NotNull(file);
        Assert.Equal(ApiFactory.ServiceName, file.Record.ServiceName);
        Assert.Equal([ApiFactory.AllowedOrg, ApiFactory.OtherOrg], file.Record.AllowedOrganizations);
        Assert.Equal(publishedAt, file.Record.PublishedAt);
        Assert.Equal(3, file.Length);
    }
}
