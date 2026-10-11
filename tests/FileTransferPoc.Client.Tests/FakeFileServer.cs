using System.Net;
using System.Net.Http.Headers;

using FileTransferPoc.Client.Auth;

namespace FileTransferPoc.Client.Tests;

/// <summary>
/// In-memory stand-in for the document API: serves one file with single-part Range support as the spec
/// describes, and can be scripted to fail the next requests in specific ways.
/// </summary>
internal sealed class FakeFileServer(byte[] content) : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage?>> _script = new();

    public List<HttpRequestMessage> Requests { get; } = [];
    public string ExpectedToken { get; set; } = "token-1";

    /// <summary>Respond to the next request with this status (null = serve normally).</summary>
    public FakeFileServer ThenStatus(HttpStatusCode status, TimeSpan? retryAfter = null)
    {
        _script.Enqueue(_ =>
        {
            var response = new HttpResponseMessage(status);
            if (retryAfter is { } ra)
            {
                response.Headers.RetryAfter = new RetryConditionHeaderValue(ra);
            }

            return response;
        });
        return this;
    }

    /// <summary>Serve the next request normally, but drop the connection after <paramref name="bytes"/> body bytes.</summary>
    public FakeFileServer ThenCutAfter(int bytes)
    {
        _script.Enqueue(request =>
        {
            var response = Serve(request);
            var full = response.Content.ReadAsByteArrayAsync().Result;
            var headers = response.Content.Headers;
            response.Content = new StreamContent(new BrokenStream(full, bytes));
            foreach (var header in headers)
            {
                response.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            return response;
        });
        return this;
    }

    /// <summary>Answer the next request with 200 and the whole file, ignoring any Range header.</summary>
    public FakeFileServer ThenFullFile()
    {
        _script.Enqueue(_ => Full());
        return this;
    }

    /// <summary>Serve the next request normally.</summary>
    public FakeFileServer ThenServe()
    {
        _script.Enqueue(_ => null);
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (request.Headers.Authorization is not { Scheme: "Bearer" } auth || auth.Parameter != ExpectedToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        }

        var scripted = _script.TryDequeue(out var step) ? step(request) : null;
        return Task.FromResult(scripted ?? Serve(request));
    }

    private HttpResponseMessage Serve(HttpRequestMessage request)
    {
        var range = request.Headers.Range?.Ranges.Single();
        if (range is null)
        {
            return Full();
        }

        var from = range.From ?? 0;
        if (from >= content.Length)
        {
            var notSatisfiable = new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                Content = new ByteArrayContent([]),
            };
            notSatisfiable.Content.Headers.ContentRange = new ContentRangeHeaderValue(content.Length);
            return notSatisfiable;
        }

        // Spec: a range larger than the file gets 200 with the whole file.
        if (from == 0 && range.To >= content.Length)
        {
            return Full();
        }

        var to = Math.Min(range.To ?? content.Length - 1, content.Length - 1);
        var slice = content[(int)from..(int)(to + 1)];
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(slice) };
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, content.Length);
        response.Content.Headers.ContentLength = slice.Length;
        return response;
    }

    private HttpResponseMessage Full()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };
        response.Content.Headers.ContentLength = content.Length;
        return response;
    }

    /// <summary>Yields the first <c>cutAfter</c> bytes, then fails like a dropped TCP connection.</summary>
    private sealed class BrokenStream(byte[] data, int cutAfter) : Stream
    {
        private int _position;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= cutAfter)
            {
                throw new IOException("The response ended prematurely.");
            }

            var n = Math.Min(count, Math.Min(cutAfter, data.Length) - _position);
            Array.Copy(data, _position, buffer, offset, n);
            _position += n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>Hands out tokens in sequence so tests can see a refresh happen.</summary>
internal sealed class FakeTokenProvider : ITokenProvider
{
    private int _issued = 1;

    public int Invalidations { get; private set; }

    public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult($"token-{_issued}");

    public void Invalidate()
    {
        Invalidations++;
        _issued++;
    }
}
