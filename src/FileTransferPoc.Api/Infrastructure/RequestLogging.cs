using System.Diagnostics;
using FileTransferPoc.Api.Auth;
using FileTransferPoc.Api.Download;

namespace FileTransferPoc.Api.Infrastructure;

/// <summary>
/// Logs every request with the calling organization and source IP, as the ESAS spec requires.
/// Runs outermost so failed, rejected and unauthenticated requests are logged too.
/// </summary>
public sealed class RequestLogging(RequestDelegate next, ILogger<RequestLogging> logger)
{
    public const string RangeItemKey = "esas.range";

    public async Task InvokeAsync(HttpContext context)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            await next(context);
        }
        finally
        {
            var range = context.Items.TryGetValue(RangeItemKey, out var value) && value is RangeDecision decision
                ? decision
                : (RangeDecision?)null;

            logger.LogInformation(
                "{Method} {Path} from {SourceIp} organization {OrganizationNumber} client {ClientId} range {RequestedRange} answered {StatusCode} sending {SentRange} in {ElapsedMs:0} ms",
                context.Request.Method,
                context.Request.Path.Value,
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                context.User.GetOrganizationNumber() ?? "unauthenticated",
                context.User.FindFirst("client_id")?.Value ?? "-",
                context.Request.Headers.Range.ToString() is { Length: > 0 } requested ? requested : "-",
                context.RequestAborted.IsCancellationRequested ? 499 : context.Response.StatusCode,
                range is { Outcome: not RangeOutcome.NotSatisfiable } r ? $"{r.Start}-{r.End}" : "-",
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }
}
