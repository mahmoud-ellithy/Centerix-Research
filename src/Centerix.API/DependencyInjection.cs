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
                    partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new System.Threading.RateLimiting.SlidingWindowRateLimiterOptions
                    {
                        PermitLimit = 5,
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
        // TenantGuardMiddleware must run BEFORE UseAuthorization so that tenant context
        // is established and permissions are loaded before the PermissionAuthorizationHandler runs.
        app.UseMiddleware<TenantGuardMiddleware>();
        app.UseAuthorization();

        return app;
    }
}
