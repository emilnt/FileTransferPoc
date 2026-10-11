using System.Net;
using FileTransferPoc.Api.Auth;
using FileTransferPoc.Api.Download;
using FileTransferPoc.Api.Infrastructure;
using FileTransferPoc.Api.Storage;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddProblemDetails();
builder.Services.AddOptions<StorageOptions>().Bind(builder.Configuration.GetSection(StorageOptions.SectionName));
builder.Services.AddSingleton<IFileStore, LocalFileStore>();
builder.Services.AddHostedService<SampleDataSeeder>();
builder.Services.AddMaskinportenAuthentication(builder.Configuration);
builder.Services.AddOverloadProtection(builder.Configuration);

// Behind a reverse proxy, list it under ForwardedHeaders:KnownProxies so the logged source IP is the client's.
var knownProxies = builder.Configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [];
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    foreach (var proxy in knownProxies)
    {
        options.KnownProxies.Add(IPAddress.Parse(proxy));
    }
});

var app = builder.Build();

if (knownProxies.Length > 0)
{
    app.UseForwardedHeaders();
}

app.UseMiddleware<RequestLogging>();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseMaintenanceMode();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapDownloadEndpoint();
app.MapMaskinportenStub();

app.Run();

public partial class Program;
