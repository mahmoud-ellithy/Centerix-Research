using Centerix.Application.Common.Behaviours;
using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Contracts.Services;
using Centerix.Domain.Platform.Billing.Refunds;
using Centerix.Domain.Platform.Contracts.EligibilityRules;
using Centerix.Domain.Platform.Promotions;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.Extensions.DependencyInjection;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        var assembly = typeof(DependencyInjection).Assembly;

        services.AddMediatR(config =>
        {
            config.RegisterServicesFromAssembly(assembly);
            config.AddBehavior(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
            config.AddBehavior(typeof(IPipelineBehavior<,>), typeof(UnhandledExceptionBehaviour<,>));
            config.AddBehavior(typeof(IPipelineBehavior<,>), typeof(LoggingBehaviour<,>));
            config.AddBehavior(typeof(IPipelineBehavior<,>), typeof(PerformanceBehaviour<,>));
            config.AddBehavior(typeof(IPipelineBehavior<,>), typeof(CachingBehaviour<,>));
        });

        services.AddValidatorsFromAssembly(assembly);

        // Register refund calculation service
        services.AddScoped<IRefundCalculationService, RefundCalculationService>();

        // Register promotion calculation service
        services.AddScoped<IPromotionCalculationService, PromotionCalculationService>();

        // Task F: eligibility rule algebra — stateless, thread-safe; singleton lifetime.
        services.AddSingleton<EligibilityRuleEvaluator>();

        // The EligibilityContextBuilder is also stateless and only depends on the
        // owner-only fact query interface. Its DI lifetime is decided by the lifetime
        // of IOwnerOnlyFactQuery (scoped) — Microsoft.Extensions.DependencyInjection
        // resolves it per scope automatically.
        services.AddScoped<EligibilityContextBuilder>();

        return services;
    }
}