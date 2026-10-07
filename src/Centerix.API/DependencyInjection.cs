using System.Globalization;
using System.Text.Json.Serialization;

using Asp.Versioning;
using Finbuckle.MultiTenant;
using Microsoft.AspNetCore.Authorization;

using Scalar.AspNetCore;

using Serilog;
using Centerix.Application.Common.Interfaces;
using Centerix.API.Infrastructure;
using Centerix.API.Localization;

namespace Microsoft.Extensions.DependencyInjection;

public static class DependencyInjection
{
    public static IServiceCollection AddPresentation(this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddCustomProblemDetails()
            .AddCustomApiVersioning()
            .AddApiDocumentation()
            .AddExceptionHandling()
            .AddControllerWithJsonConfiguration();

        // F1: forwarded-header trust is configuration-bound with fail-fast invariants. Registering
        // it here guarantees the options are validated at startup (ValidateOnStart), never lazily.
        services.AddForwardedHeadersProtection(configuration);

        services.AddSingleton<ILocalizer, JsonLocalizer>();

        return services;
    }

    private static IServiceCollection AddCustomProblemDetails(this IServiceCollection services)
    {
        services.AddProblemDetails(options =>
        {
            options.CustomizeProblemDetails = context =>
            {
                context.ProblemDetails.Instance =
                    $"{context.HttpContext.Request.Method} {context.HttpContext.Request.Path}";
                context.ProblemDetails.Extensions.Add("requestId", context.HttpContext.TraceIdentifier);
            };
        });

        return services;
    }

    private static IServiceCollection AddCustomApiVersioning(this IServiceCollection services)
    {
        services.AddApiVersioning(options =>
        {
            options.DefaultApiVersion = new ApiVersion(1);
            options.AssumeDefaultVersionWhenUnspecified = true;
            options.ReportApiVersions = true;
        })
        .AddApiExplorer(options =>
        {
            options.GroupNameFormat = "'v'VVV";
            options.SubstituteApiVersionInUrl = true;
        });

        return services;
    }

    private static IServiceCollection AddApiDocumentation(this IServiceCollection services)
    {
        services.AddOpenApi();
        return services;
    }

    private static IServiceCollection AddExceptionHandling(this IServiceCollection services)
    {
        services.AddExceptionHandler<Centerix.API.Infrastructure.GlobalExceptionHandler>();
        return services;
    }

    private static IServiceCollection AddControllerWithJsonConfiguration(this IServiceCollection services)
    {
        services.AddControllers().AddJsonOptions(options =>
        {
            options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        });

        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build());

        // Rate limiting for brute-force protection on login endpoint
        services.AddRateLimiter(options =>
        {
            options.AddPolicy("LoginPolicy", httpContext =>
                System.Threading.RateLimiting.RateLimitPartition.GetSlidingWindowLimiter(
                    partitionKey: ClientIp.ForRateLimit(httpContext),
                    factory: _ => new System.Threading.RateLimiting.SlidingWindowRateLimiterOptions
                    {
                        PermitLimit = 5,
                        Window = TimeSpan.FromMinutes(1),
                        SegmentsPerWindow = 4,
                        QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0
                    }));

            // Refresh is anonymous and CPU/DB expensive (hashing, a serializable transaction and a
            // token mint). It is also the endpoint an attacker who has stolen a refresh token would
            // hammer. A legitimate client refreshes a handful of times a minute; this ceiling is far
            // above that while still bounding abuse from a single source.
            options.AddPolicy("RefreshPolicy", httpContext =>
                System.Threading.RateLimiting.RateLimitPartition.GetSlidingWindowLimiter(
                    partitionKey: ClientIp.ForRateLimit(httpContext),
                    factory: _ => new System.Threading.RateLimiting.SlidingWindowRateLimiterOptions
                    {
                        PermitLimit = 30,
                        Window = TimeSpan.FromMinutes(1),
                        SegmentsPerWindow = 4,
                        QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0
                    }));

            // Invitation registration is anonymous: the token in the body is the only capability,
            // so an unauthenticated caller can be pointed at it forever. A generous per-source
            // ceiling still caps token guessing and account-bombing without affecting real users
            // (registration is a one-shot flow).
            options.AddPolicy("RegisterPolicy", httpContext =>
                System.Threading.RateLimiting.RateLimitPartition.GetSlidingWindowLimiter(
                    partitionKey: ClientIp.ForRateLimit(httpContext),
                    factory: _ => new System.Threading.RateLimiting.SlidingWindowRateLimiterOptions
                    {
                        PermitLimit = 60,
                        Window = TimeSpan.FromMinutes(1),
                        SegmentsPerWindow = 4,
                        QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0
                    }));

            options.OnRejected = async (context, cancellationToken) =>
            {
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.HttpContext.Response.ContentType = "application/json";

                var response = new
                {
                    error = "Too many requests. Please try again later.",
                    retryAfterSeconds = 60
                };

                await context.HttpContext.Response.WriteAsJsonAsync(response, cancellationToken);
            };
        });

        return services;
    }

    public static IApplicationBuilder UseCoreMiddlewares(this IApplicationBuilder app)
    {
        app.UseExceptionHandler();

        // F1: trust guard first — strips X-Forwarded-For when there is no socket peer to
        // attribute the chain to, so ForwardedHeadersMiddleware can only ever start its
        // validation walk from a proven address. Runs before UseForwardedHeaders and before
        // UseRateLimiter so the limiter partitions on the resolved (server-chosen) address.
        app.UseMiddleware<ForwardedHeaderTrustGuardMiddleware>();
        app.UseForwardedHeaders();

        app.UseStatusCodePages();
        app.UseRateLimiter();
        app.UseHttpsRedirection();
        app.UseSerilogRequestLogging();
        app.UseMiddleware<Centerix.API.Infrastructure.RequestLogContextMiddleware>();

        var supportedCultures = new[] { "en", "ar" };
        var localizationOptions = new RequestLocalizationOptions()
            .SetDefaultCulture("en")
            .AddSupportedCultures(supportedCultures)
            .AddSupportedUICultures(supportedCultures);

        app.UseRequestLocalization(localizationOptions);

        app.UseMultiTenant();
        app.UseAuthentication();
        // NEW-1: password.change_required enforcement runs on the authenticated principal
        // BEFORE tenant authorization so the bootstrap/root user (no membership) is still
        // gated on rotation yet can always reach the rotation endpoint itself.
        app.UseMiddleware<PasswordChangeEnforcementMiddleware>();
        // TenantGuardMiddleware must run BEFORE UseAuthorization so that tenant context
        // is established and permissions are loaded before the PermissionAuthorizationHandler runs.
        app.UseMiddleware<TenantGuardMiddleware>();
        app.UseAuthorization();

        return app;
    }
}
