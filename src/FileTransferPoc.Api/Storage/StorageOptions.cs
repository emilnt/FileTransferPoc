namespace FileTransferPoc.Api.Storage;

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>Root folder. File bytes live in <c>files/</c> and one metadata record per GUID in <c>metadata/</c>.</summary>
    public string RootPath { get; set; } = "data";

    /// <summary>
    /// Days a file stays downloadable after it was published. The spec requires at least 15.
    /// Zero or less keeps files available indefinitely.
    /// </summary>
    public int RetentionDays { get; set; } = 15;

    /// <summary>Creates a few sample files on startup so a client has something to download.</summary>
    public bool SeedSampleData { get; set; }
}
