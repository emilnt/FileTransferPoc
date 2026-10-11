using System.Collections.Concurrent;
using System.Net.Http.Headers;
using FileTransferPoc.Api.Auth;
using FileTransferPoc.Api.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FileTransferPoc.Api.Tests;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public const string AllowedOrg = "123456789";
    public const string OtherOrg = "987654321";
    public const string Scope = "esas:filer";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ftpoc-tests-" + Guid.NewGuid().ToString("N"));

    public ConcurrentQueue<string> Logs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Storage:RootPath", _root);
        builder.UseSetting("Storage:RetentionDays", "15");
        builder.UseSetting("Maskinporten:SigningKey", "test-signing-key-that-is-long-enough-0123456789");
        builder.UseSetting("Maskinporten:RequiredScope", Scope);
        builder.ConfigureLogging(logging => logging.AddProvider(new CapturingLoggerProvider(Logs)));
    }

    public string Token(string org = AllowedOrg, string scope = Scope, TimeSpan? lifetime = null) =>
        Services.GetRequiredService<MaskinportenStubIssuer>().CreateToken(org, scope, lifetime);

    public HttpClient AuthorizedClient(string org = AllowedOrg, string scope = Scope)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(org, scope));
        return client;
    }

    public async Task<Guid> PublishAsync(byte[] content, DateTimeOffset? publishedAt = null, params string[] allowed)
    {
        var id = Guid.NewGuid();
        using var stream = new MemoryStream(content);
        await Services.GetRequiredService<IFileStore>().PublishAsync(
            id,
            stream,
            allowed.Length > 0 ? allowed : [AllowedOrg],
            publishedAt ?? DateTimeOffset.UtcNow.AddMinutes(-1),
            CancellationToken.None);
        return id;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class CapturingLoggerProvider(ConcurrentQueue<string> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, sink);

        public void Dispose()
        {
        }
    }

    private sealed class CapturingLogger(string category, ConcurrentQueue<string> sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            sink.Enqueue($"{category}: {formatter(state, exception)}");
    }
}
