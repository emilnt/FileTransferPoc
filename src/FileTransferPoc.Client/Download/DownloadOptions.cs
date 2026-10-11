namespace FileTransferPoc.Client.Download;

public sealed class DownloadOptions
{
    /// <summary>How many times a 500/503 or a broken connection is retried before giving up.</summary>
    public int MaxRetries { get; init; } = 6;

    /// <summary>Delay before the first retry; doubled for each subsequent one.</summary>
    public TimeSpan InitialBackoff { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Upper bound for a single backoff delay (also caps a server supplied Retry-After).</summary>
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Size of the range asked for on a fresh download. The spec lets the first call use a default range:
    /// the answer is either 200 with the whole file or 206 whose Content-Range gives the full length.
    /// Null asks for everything from the current offset (<c>bytes=N-</c>).
    /// </summary>
    public long? ChunkSize { get; init; }
}
