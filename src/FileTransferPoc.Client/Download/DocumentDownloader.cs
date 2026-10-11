using System.Net;
using System.Net.Http.Headers;
using FileTransferPoc.Client.Auth;

namespace FileTransferPoc.Client.Download;

public sealed record DownloadResult(string Path, long Length, bool ChecksumVerified);

/// <summary>
/// Fetches a document from an ESAS "HenteDokumentViaHttps" endpoint.
/// Bytes go to <c>destination.part</c> first, so an interrupted download resumes with a Range request
/// from where it stopped (also across process restarts, since the bytes for a GUID never change).
/// The file is moved into place only after the optional checksum matches.
/// </summary>
public sealed class DocumentDownloader
{
    private const int BufferSize = 81920;

    private readonly HttpClient _http;
    private readonly ITokenProvider _tokens;
    private readonly DownloadOptions _options;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public DocumentDownloader(
        HttpClient http,
        ITokenProvider tokens,
        DownloadOptions? options = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _http = http;
        _tokens = tokens;
        _options = options ?? new DownloadOptions();
        _delay = delay ?? Task.Delay;
    }

    /// <summary>Raised before each retry with the attempt number, the delay and the reason.</summary>
    public event Action<int, TimeSpan, string>? Retrying;

    /// <summary>Raised as bytes are written, with the total written so far and the full length when known.</summary>
    public event Action<long, long?>? Progress;

    public async Task<DownloadResult> DownloadAsync(
        Uri fileUri, string destination, Checksum? checksum = null, CancellationToken cancellationToken = default)
    {
        var partPath = destination + ".part";
        long? totalLength = null;
        var failures = 0;
        var refreshedToken = false;

        while (true)
        {
            var offset = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
            if (totalLength is { } known)
            {
                if (offset == known)
                {
                    break;
                }

                if (offset > known)
                {
                    File.Delete(partPath);
                    offset = 0;
                }
            }

            string? transientReason;
            TimeSpan? retryAfter = null;
            var progressed = false;

            try
            {
                using var request = await CreateRequestAsync(fileUri, offset, cancellationToken).ConfigureAwait(false);
                using var response = await _http
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                switch (response.StatusCode)
                {
                    case HttpStatusCode.OK:
                    {
                        // Whole file. Either we asked without Range, or the range covered more than the file.
                        totalLength = response.Content.Headers.ContentLength;
                        var written = await WriteBodyAsync(response, partPath, 0, totalLength, cancellationToken)
                            .ConfigureAwait(false);
                        progressed = written > 0;
                        if (totalLength is null)
                        {
                            totalLength = written; // No Content-Length: a cleanly ended body is the whole file.
                        }

                        transientReason = written < totalLength ? $"connection closed after {written} of {totalLength} bytes" : null;
                        break;
                    }

                    case HttpStatusCode.PartialContent:
                    {
                        var range = response.Content.Headers.ContentRange
                            ?? throw new DownloadException("206 response without a Content-Range header.", response.StatusCode);
                        if (range.From is not { } from || range.To is not { } to || range.Length is not { } length)
                        {
                            throw new DownloadException($"Unusable Content-Range '{range}'.", response.StatusCode);
                        }

                        if (from > offset)
                        {
                            throw new DownloadException(
                                $"Server returned bytes {from}-{to} but {offset} was requested.", response.StatusCode);
                        }

                        totalLength = length;
                        var expected = to - from + 1;
                        var written = await WriteBodyAsync(response, partPath, from, length, cancellationToken)
                            .ConfigureAwait(false);
                        progressed = written > 0;
                        transientReason = written < expected ? $"connection closed after {written} of {expected} bytes" : null;
                        break;
                    }

                    case HttpStatusCode.RequestedRangeNotSatisfiable:
                    {
                        // "bytes */N": if we already hold N bytes the previous run finished but was not finalised.
                        var length = response.Content.Headers.ContentRange?.Length;
                        if (length is null || offset < length)
                        {
                            throw new DownloadException(DownloadException.Describe(response.StatusCode), response.StatusCode);
                        }

                        totalLength = length;
                        transientReason = null;
                        break;
                    }

                    case HttpStatusCode.Unauthorized when !refreshedToken:
                        // Token may have expired or been revoked; fetch a new one once.
                        _tokens.Invalidate();
                        refreshedToken = true;
                        continue;

                    case HttpStatusCode.InternalServerError:
                    case HttpStatusCode.BadGateway:
                    case HttpStatusCode.ServiceUnavailable:
                    case HttpStatusCode.GatewayTimeout:
                    case HttpStatusCode.TooManyRequests:
                        transientReason = $"{(int)response.StatusCode} {DownloadException.Describe(response.StatusCode)}";
                        retryAfter = RetryAfter(response);
                        break;

                    default:
                        throw new DownloadException(DownloadException.Describe(response.StatusCode), response.StatusCode);
                }
            }
            catch (Exception ex) when (IsTransient(ex, cancellationToken))
            {
                transientReason = ex.Message;
                progressed = File.Exists(partPath) && new FileInfo(partPath).Length > offset;
            }

            if (transientReason is null)
            {
                continue; // Re-evaluate the .part length; a finished chunk leads either to the next range or out.
            }

            // A long download may be interrupted many times; only consecutive failures count against the limit.
            failures = progressed ? 1 : failures + 1;
            if (failures > _options.MaxRetries)
            {
                throw new DownloadException($"Giving up after {_options.MaxRetries} retries: {transientReason}");
            }

            var delay = retryAfter is { } ra ? Min(ra, _options.MaxBackoff) : Backoff(failures);
            Retrying?.Invoke(failures, delay, transientReason);
            await _delay(delay, cancellationToken).ConfigureAwait(false);
        }

        var verified = false;
        if (checksum is not null)
        {
            if (!await checksum.MatchesAsync(partPath, cancellationToken).ConfigureAwait(false))
            {
                File.Delete(partPath);
                throw new DownloadException($"Checksum mismatch: expected {checksum}. The partial file was discarded.");
            }

            verified = true;
        }

        File.Move(partPath, destination, overwrite: true);
        return new DownloadResult(destination, totalLength ?? 0, verified);
    }

    /// <summary>Exponential backoff with ±20% jitter: initial, 2×initial, 4×initial, … capped at MaxBackoff.</summary>
    internal TimeSpan Backoff(int failure)
    {
        var exponential = _options.InitialBackoff * Math.Pow(2, Math.Min(failure - 1, 30));
        var capped = Min(exponential, _options.MaxBackoff);
        return capped * (0.8 + Random.Shared.NextDouble() * 0.4);
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(Uri fileUri, long offset, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, fileUri);
        var token = await _tokens.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        if (_options.ChunkSize is { } chunk)
        {
            request.Headers.Range = new RangeHeaderValue(offset, offset + chunk - 1);
        }
        else if (offset > 0)
        {
            request.Headers.Range = new RangeHeaderValue(offset, null);
        }

        return request;
    }

    private async Task<long> WriteBodyAsync(
        HttpResponseMessage response, string partPath, long position, long? totalLength, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(partPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
        file.SetLength(position); // 200 restarts from zero; 206 may overlap bytes we already have.
        file.Position = position;

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[BufferSize];
        long written = 0;
        try
        {
            int read;
            while ((read = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                written += read;
                Progress?.Invoke(position + written, totalLength);
            }
        }
        finally
        {
            // Keep whatever arrived before a failure so the retry can resume from it.
            await file.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }

        return written;
    }

    private static bool IsTransient(Exception ex, CancellationToken cancellationToken) => ex switch
    {
        DownloadException => false,
        OperationCanceledException => !cancellationToken.IsCancellationRequested, // HttpClient timeout
        HttpRequestException or IOException => true,
        _ => false,
    };

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
        {
            return delta;
        }

        if (header?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return null;
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}
