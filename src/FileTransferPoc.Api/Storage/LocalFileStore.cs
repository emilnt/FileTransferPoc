using System.Text.Json;
using Microsoft.Extensions.Options;

namespace FileTransferPoc.Api.Storage;

/// <summary>
/// Keeps bytes under <c>{root}/files/</c> and a JSON metadata record per GUID under <c>{root}/metadata/{guid}.json</c>.
/// </summary>
public sealed class LocalFileStore : IFileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _filesRoot;
    private readonly string _metadataRoot;

    public LocalFileStore(IOptions<StorageOptions> options, IHostEnvironment environment)
    {
        var root = Path.GetFullPath(options.Value.RootPath, environment.ContentRootPath);
        _filesRoot = Path.Combine(root, "files");
        _metadataRoot = Path.Combine(root, "metadata");
        Directory.CreateDirectory(_filesRoot);
        Directory.CreateDirectory(_metadataRoot);
    }

    public async Task<StoredFile?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        var metadataPath = MetadataPath(id);
        if (!File.Exists(metadataPath))
        {
            return null;
        }

        FileRecord? record;
        await using (var stream = File.OpenRead(metadataPath))
        {
            record = await JsonSerializer.DeserializeAsync<FileRecord>(stream, JsonOptions, cancellationToken);
        }

        if (record is null || record.Id != id)
        {
            throw new InvalidDataException($"Metadata record {metadataPath} is invalid.");
        }

        var fullPath = ResolveFilePath(record.FilePath);
        var info = new FileInfo(fullPath);
        return info.Exists ? new StoredFile(record, fullPath, info.Length) : null;
    }

    public async Task<FileRecord> PublishAsync(
        Guid id,
        Stream content,
        IReadOnlyList<string> allowedOrganizations,
        DateTimeOffset publishedAt,
        CancellationToken cancellationToken)
    {
        // Bytes for a published GUID must never change, so refuse to overwrite.
        var relativePath = id.ToString("D");
        var fullPath = ResolveFilePath(relativePath);
        await using (var target = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            await content.CopyToAsync(target, cancellationToken);
        }

        var record = new FileRecord(id, relativePath, allowedOrganizations, publishedAt);
        await using (var metadata = new FileStream(MetadataPath(id), FileMode.CreateNew, FileAccess.Write))
        {
            await JsonSerializer.SerializeAsync(metadata, record, JsonOptions, cancellationToken);
        }

        return record;
    }

    private string MetadataPath(Guid id) => Path.Combine(_metadataRoot, id.ToString("D") + ".json");

    private string ResolveFilePath(string relativePath)
    {
        var fullPath = Path.GetFullPath(relativePath, _filesRoot);
        if (!fullPath.StartsWith(_filesRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"File path '{relativePath}' points outside the store.");
        }

        return fullPath;
    }
}
