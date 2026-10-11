using FileTransferPoc.Api.Auth;
using FileTransferPoc.Api.Infrastructure;
using FileTransferPoc.Api.Storage;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace FileTransferPoc.Api.Download;

public static class DownloadEndpoint
{
    public const string Route = "/filer/v1/nedlasting/{id}";

    public static IEndpointRouteBuilder MapDownloadEndpoint(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(Route, HandleAsync)
            .RequireAuthorization(MaskinportenAuthentication.DownloadPolicy)
            .RequireRateLimiting(Overload.DownloadPolicy);
        return endpoints;
    }

    private static async Task<IResult> HandleAsync(
        string id,
        HttpContext context,
        IFileStore store,
        IOptions<StorageOptions> storageOptions,
        TimeProvider timeProvider)
    {
        if (!Guid.TryParseExact(id, "D", out var fileId))
        {
            return Problem(StatusCodes.Status400BadRequest, "Ugyldig GUID", "The identifier must be a GUID in the form 00000000-0000-0000-0000-000000000000.");
        }

        var file = await store.FindAsync(fileId, context.RequestAborted);
        if (file is null || !IsAvailable(file.Record, storageOptions.Value, timeProvider.GetUtcNow()))
        {
            return Problem(StatusCodes.Status404NotFound, "Filen finnes ikke", "No file is available for this identifier.");
        }

        var organization = context.User.GetOrganizationNumber();
        if (organization is null || !file.Record.AllowedOrganizations.Contains(organization, StringComparer.Ordinal))
        {
            return Problem(StatusCodes.Status403Forbidden, "Ingen tilgang", "The calling organization may not fetch this file.");
        }

        var response = context.Response;
        response.Headers.AcceptRanges = "bytes";
        // The bytes behind a GUID never change, so the GUID is a valid strong validator.
        response.Headers.ETag = new EntityTagHeaderValue($"\"{fileId:D}\"").ToString();

        var range = ByteRange.Resolve(context.Request.Headers.Range.Count == 1 ? context.Request.Headers.Range[0] : null, file.Length);
        context.Items[RequestLogging.RangeItemKey] = range;

        if (range.Outcome == RangeOutcome.NotSatisfiable)
        {
            response.Headers.ContentRange = $"bytes */{file.Length}";
            return Problem(StatusCodes.Status416RangeNotSatisfiable, "Ugyldig Range", $"The file is {file.Length} bytes long.");
        }

        response.ContentType = "application/octet-stream";
        response.ContentLength = range.Length;
        if (range.Outcome == RangeOutcome.Partial)
        {
            response.StatusCode = StatusCodes.Status206PartialContent;
            response.Headers.ContentRange = $"bytes {range.Start}-{range.End}/{file.Length}";
        }
        else
        {
            response.StatusCode = StatusCodes.Status200OK;
        }

        try
        {
            // Streams straight from disk (sendfile where the server supports it); nothing is buffered in memory.
            await response.SendFileAsync(file.FullPath, range.Start, range.Length, context.RequestAborted);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The client went away mid-download; it can resume with a Range request.
        }

        return Results.Empty;
    }

    private static bool IsAvailable(FileRecord record, StorageOptions options, DateTimeOffset now)
    {
        if (record.PublishedAt > now)
        {
            return false;
        }

        return options.RetentionDays <= 0 || now < record.PublishedAt.AddDays(options.RetentionDays);
    }

    private static IResult Problem(int statusCode, string title, string detail) =>
        Results.Problem(statusCode: statusCode, title: title, detail: detail);
}
