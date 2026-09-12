using Centerix.Application.Common.Behaviours;
using Centerix.Domain.Platform.Billing.Refunds;
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

        return services;
    }
}