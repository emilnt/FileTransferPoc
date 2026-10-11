using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;

namespace FileTransferPoc.Api.Auth;

public static class MaskinportenAuthentication
{
    public const string DownloadPolicy = "EsasDownload";

    public static IServiceCollection AddMaskinportenAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<MaskinportenOptions>().Bind(configuration.GetSection(MaskinportenOptions.SectionName));
        services.AddSingleton<MaskinportenStubIssuer>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<MaskinportenOptions>>((jwt, maskinporten) =>
            {
                var settings = maskinporten.Value;

                // Keep claim names as Maskinporten sends them (scope, consumer, client_id).
                jwt.MapInboundClaims = false;
                jwt.TokenValidationParameters.ValidIssuer = settings.Issuer;
                jwt.TokenValidationParameters.ValidateAudience = !string.IsNullOrEmpty(settings.Audience);
                jwt.TokenValidationParameters.ValidAudience = settings.Audience;
                jwt.TokenValidationParameters.ClockSkew = TimeSpan.FromSeconds(30);

                if (!string.IsNullOrEmpty(settings.Authority))
                {
                    jwt.Authority = settings.Authority;
                }
                else
                {
                    jwt.TokenValidationParameters.IssuerSigningKey = MaskinportenStubIssuer.CreateSigningKey(settings);
                }
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(DownloadPolicy, policy => policy
                .RequireAuthenticatedUser()
                .RequireAssertion(context =>
                {
                    var settings = context.Resource is HttpContext http
                        ? http.RequestServices.GetRequiredService<IOptions<MaskinportenOptions>>().Value
                        : null;
                    return settings is not null && context.User.HasScope(settings.RequiredScope);
                }));

        return services;
    }

    /// <summary>
    /// <c>POST /maskinporten-stub/token</c> with form fields <c>consumer_org</c> (9 digits) and <c>scope</c>.
    /// Answers like Maskinporten's token endpoint. Only mapped when <see cref="MaskinportenOptions.EnableStubIssuer"/> is on.
    /// </summary>
    public static IEndpointRouteBuilder MapMaskinportenStub(this IEndpointRouteBuilder endpoints)
    {
        var settings = endpoints.ServiceProvider.GetRequiredService<IOptions<MaskinportenOptions>>().Value;
        if (!settings.EnableStubIssuer)
        {
            return endpoints;
        }

        endpoints.MapPost("/maskinporten-stub/token", (HttpRequest request, MaskinportenStubIssuer issuer) =>
            {
                var form = request.Form;
                var organization = form["consumer_org"].ToString();
                var scope = form["scope"].ToString();
                if (organization.Length != 9 || !organization.All(char.IsAsciiDigit) || string.IsNullOrWhiteSpace(scope))
                {
                    return Results.BadRequest(new { error = "invalid_request", error_description = "consumer_org (9 digits) and scope are required." });
                }

                return Results.Ok(new
                {
                    access_token = issuer.CreateToken(organization, scope),
                    token_type = "Bearer",
                    expires_in = (int)MaskinportenStubIssuer.DefaultLifetime.TotalSeconds,
                    scope,
                });
            })
            .DisableAntiforgery()
            .AllowAnonymous();

        return endpoints;
    }
}
