namespace FileTransferPoc.Api.Storage;

/// <summary>Metadata stored per published GUID.</summary>
/// <param name="Id">The GUID that ends the download URI.</param>
/// <param name="FilePath">Path to the bytes, relative to the store's <c>files/</c> folder.</param>
/// <param name="AllowedOrganizations">Norwegian organization numbers (9 digits) that may fetch the file.</param>
/// <param name="PublishedAt">When the URI was shared with the recipient. Retention counts from here.</param>
public sealed record FileRecord(
    Guid Id,
    string FilePath,
    IReadOnlyList<string> AllowedOrganizations,
    DateTimeOffset PublishedAt);

/// <summary>A record whose bytes were found on disk.</summary>
public sealed record StoredFile(FileRecord Record, string FullPath, long Length);
