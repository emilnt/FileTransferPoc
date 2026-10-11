using System.Net;

namespace FileTransferPoc.Client.Download;

/// <summary>A download failed in a way retrying will not fix, or the retries ran out.</summary>
public sealed class DownloadException(string message, HttpStatusCode? statusCode = null, Exception? inner = null)
    : Exception(message, inner)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;

    /// <summary>Explains a status code the way the ESAS spec's error table does.</summary>
    internal static string Describe(HttpStatusCode status) => status switch
    {
        HttpStatusCode.BadRequest => "Bad request: the URI or headers are malformed (e.g. invalid GUID). Fix the request before trying again.",
        HttpStatusCode.Unauthorized => "Unauthorized: no valid credentials. Check the Maskinporten token setup.",
        HttpStatusCode.Forbidden => "Forbidden: authenticated, but not allowed to fetch this file. Check that the right Maskinporten scope is used.",
        HttpStatusCode.NotFound => "Not found: the file was deleted (e.g. after 15 days) or the GUID is wrong. If the file is under 15 days old, contact the sender.",
        HttpStatusCode.MethodNotAllowed => "Method not allowed: only GET is supported.",
        HttpStatusCode.RequestedRangeNotSatisfiable => "Range not satisfiable: the requested range is outside the file.",
        HttpStatusCode.InternalServerError => "Internal server error at the provider.",
        HttpStatusCode.ServiceUnavailable => "Service unavailable: the API is temporarily down.",
        _ => $"Unexpected status {(int)status} {status}.",
    };
}
