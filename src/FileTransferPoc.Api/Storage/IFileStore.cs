namespace FileTransferPoc.Api.Storage;

public interface IFileStore
{
    /// <summary>Returns the file for <paramref name="id"/>, or null when there is no record or its bytes are gone.</summary>
    Task<StoredFile?> FindAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Copies <paramref name="content"/> into the store and writes its metadata record.</summary>
    Task<FileRecord> PublishAsync(
        Guid id,
        Stream content,
        IReadOnlyList<string> allowedOrganizations,
        DateTimeOffset publishedAt,
        CancellationToken cancellationToken);
}
