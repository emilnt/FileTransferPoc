using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;

namespace FileTransferPoc.Api.Infrastructure;

public sealed class ServiceOptions
{
    public const string SectionName = "Service";

    /// <summary>Answers every download with 503 while true. Reloads with configuration.</summary>
    public bool MaintenanceMode { get; set; }

    /// <summary>Downloads served at once before new ones get 503.</summary>
    public int MaxConcurrentDownloads { get; set; } = 100;

    /// <summary>Seconds put in Retry-After on 503 answers.</summary>
    public int RetryAfterSeconds { get; set; } = 30;
}

/// <summary>503 Service Unavailable: maintenance mode and a concurrency cap for overload.</summary>
public static class Overload
{
    public const string DownloadPolicy = "downloads";

    public static IServiceCollection AddOverloadProtection(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ServiceOptions>().Bind(configuration.GetSection(ServiceOptions.SectionName));

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status503ServiceUnavailable;
            limiter.OnRejected = (context, _) =>
            {
                SetRetryAfter(context.HttpContext);
                return ValueTask.CompletedTask;
            };
            limiter.AddPolicy(DownloadPolicy, context =>
            {
                var options = context.RequestServices.GetRequiredService<IOptions<ServiceOptions>>().Value;
                return RateLimitPartition.GetConcurrencyLimiter("downloads", _ => new ConcurrencyLimiterOptions
                {
                    PermitLimit = options.MaxConcurrentDownloads,
                    QueueLimit = 0,
                });
            });
        });

        return services;
    }

    public static IApplicationBuilder UseMaintenanceMode(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var options = context.RequestServices.GetRequiredService<IOptionsMonitor<ServiceOptions>>().CurrentValue;
            if (options.MaintenanceMode && context.Request.Path.StartsWithSegments("/filer"))
            {
                SetRetryAfter(context);
                await Results.Problem(
                        statusCode: StatusCodes.Status503ServiceUnavailable,
                        title: "Tjenesten er utilgjengelig",
                        detail: "The service is down for maintenance. Retry later with exponential backoff.")
                    .ExecuteAsync(context);
                return;
            }

            await next(context);
        });

    private static void SetRetryAfter(HttpContext context)
    {
        var options = context.RequestServices.GetRequiredService<IOptionsMonitor<ServiceOptions>>().CurrentValue;
        context.Response.Headers.RetryAfter = options.RetryAfterSeconds.ToString();
    }
}
