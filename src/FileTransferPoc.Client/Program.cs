using System.Security.Cryptography;
using FileTransferPoc.Client;
using FileTransferPoc.Client.Auth;
using FileTransferPoc.Client.Download;

CliOptions options;
try
{
    options = CliOptions.Parse(args);
}
catch (FormatException ex)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine();
    Console.Error.WriteLine(CliOptions.Usage);
    return 2;
}

if (options.ShowHelp)
{
    Console.WriteLine(CliOptions.Usage);
    return 0;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

using var signingKey = RSA.Create();
if (options.KeyPath is not null)
{
    signingKey.ImportFromPem(await File.ReadAllTextAsync(options.KeyPath));
}
else
{
    Console.Error.WriteLine("warning: no --key given; signing the Maskinporten assertion with a throwaway key (only works against the stub issuer).");
}

using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
var tokens = new MaskinportenTokenProvider(http, new MaskinportenOptions
{
    TokenEndpoint = options.TokenEndpoint,
    Audience = options.Audience,
    ClientId = options.ClientId,
    Scope = options.Scope,
    SigningKey = signingKey,
    KeyId = options.KeyId,
});

var downloader = new DocumentDownloader(http, tokens, new DownloadOptions
{
    MaxRetries = options.MaxRetries,
    ChunkSize = options.ChunkSize,
});
downloader.Retrying += (attempt, delay, reason) =>
    Console.Error.WriteLine($"retry {attempt}/{options.MaxRetries} in {delay.TotalSeconds:0.0}s: {reason}");

var lastReport = DateTime.MinValue;
downloader.Progress += (written, total) =>
{
    if (DateTime.UtcNow - lastReport < TimeSpan.FromMilliseconds(500) && written != total)
    {
        return;
    }

    lastReport = DateTime.UtcNow;
    Console.Error.Write(total is > 0
        ? $"\r{written:N0} / {total:N0} bytes ({100.0 * written / total.Value:0.0}%)   "
        : $"\r{written:N0} bytes   ");
};

try
{
    Console.Error.WriteLine($"downloading {options.FileUri} -> {options.OutputPath}");
    var result = await downloader.DownloadAsync(options.FileUri, options.OutputPath, options.Checksum, cts.Token);
    Console.Error.WriteLine();
    Console.WriteLine(result.ChecksumVerified
        ? $"saved {result.Length:N0} bytes to {result.Path} (checksum {options.Checksum!.Algorithm.Name} verified)"
        : $"saved {result.Length:N0} bytes to {result.Path} (no checksum supplied, integrity not verified)");
    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine($"cancelled; run the same command again to resume from {options.OutputPath}.part");
    return 130;
}
catch (Exception ex) when (ex is DownloadException or TokenRequestException)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}
