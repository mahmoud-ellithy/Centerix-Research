namespace Centerix.Application.Platform.Contracts.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Subscriptions;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Contracts;
using Centerix.Domain.Platform.Contracts.Enums;
using Centerix.Domain.Platform.Subscriptions;
using Centerix.Domain.Platform.Subscriptions.Enums;

using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// PLATFORM-ONLY workflow: creates a Subscription (TenantPlan) from an Active Contract.
/// The subscription snapshots the contract's commercial terms.
/// </summary>
public record CreateSubscriptionFromContractCommand(Guid ContractId) : IRequest<Result<Created>>;

public class CreateSubscriptionFromContractValidator : AbstractValidator<CreateSubscriptionFromContractCommand>
{
    public CreateSubscriptionFromContractValidator()
    {
        RuleFor(x => x.ContractId).NotEmpty();
    }
}

public class CreateSubscriptionFromContractHandler(
    IAppDbContext dbContext,
    IPlatformAdminGuard platformAdminGuard,
    ISubscriptionFactory subscriptionFactory,
    IAuditWriter auditWriter,
    TimeProvider timeProvider) : IRequestHandler<CreateSubscriptionFromContractCommand, Result<Created>>
{
    public async Task<Result<Created>> Handle(
        CreateSubscriptionFromContractCommand request,
        CancellationToken cancellationToken)
    {
        var guardResult = platformAdminGuard.EnsurePlatformAdmin();
        if (!guardResult.IsSuccess)
            return guardResult.Errors!;

        var contract = await dbContext.Contracts
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == request.ContractId, cancellationToken);

        if (contract is null)
            return Error.NotFound("Contract.NotFound", $"Contract '{request.ContractId}' was not found.");

        // Contract must be Active to create a subscription
        if (contract.Status != ContractStatus.Active)
            return Error.Conflict("Contract.NotActive", $"Contract '{request.ContractId}' is not Active and cannot be used to create a subscription.");

        var now = timeProvider.GetUtcNow().UtcDateTime;

        // Create subscription from contract snapshot using the factory
        // The factory creates from Plan, so we need to pass the contract's PlanId
        var subscriptionResult = await subscriptionFactory.CreateActivatedAsync(
            contract.TenantId,
            contract.PlanId,
            now,
            autoRenew: false,
            cancellationToken);

        if (!subscriptionResult.IsSuccess)
            return subscriptionResult.Errors!;

        var subscription = subscriptionResult.Value;

        // Link subscription to contract
        subscription.LinkToContract(contract.Id);

        dbContext.TenantPlans.Add(subscription);

        // Stamp tenant ID before save (InMemory provider doesn't run interceptors)
        dbContext.StampAddedTenantIds(contract.TenantId);

        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "Subscription.CreateFromContract",
            entityType: nameof(TenantPlan),
            entityId: subscription.Id.ToString(),
            newValue: AuditPayload.Serialize(new
            {
                contract.Id,
                contract.TenantId,
                PlanId = contract.PlanId,
                subscription.SnapshotPrice,
                subscription.SnapshotCurrency,
                subscription.DurationMonths,
                subscription.BonusMonths,
                subscription.EffectiveEndsAtUtc
            }),
            cancellationToken: cancellationToken);

        return Result.Created;
    }
}
