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

        // Validate that the Contract has a complete entitlement snapshot before creating a Subscription.
        // A Contract with zero/default entitlements would silently create a Subscription with no features,
        // limits, or pricing — which is almost always a bug from an incomplete creation path.
        var snapshotValidation = contract.ValidateSnapshotCompleteness();
        if (!snapshotValidation.IsSuccess)
            return snapshotValidation.Errors!;

        var now = timeProvider.GetUtcNow().UtcDateTime;

        // Create subscription from the Contract's authoritative commercial snapshot.
        // Start alignment: the subscription MUST start at the contract's effective date —
        // never at "now". The wall clock must not influence commercial period alignment.
        // Activation follows renewal semantics: a future-dated contract creates a Pending
        // subscription (it must not grant access or occupy the non-terminal unique index
        // before its effective date); an immediate/historical contract activates.
        var snapshot = contract.GetSubscriptionSnapshot();

        var subscriptionResult = await subscriptionFactory.CreateFromSnapshotAsync(
            contract.TenantId,
            contract.PlanId,
            snapshot,
            startsAtUtc: contract.EffectiveAtUtc,
            autoRenew: false,
            activate: contract.EffectiveAtUtc <= now,
            cancellationToken);

        if (!subscriptionResult.IsSuccess)
            return subscriptionResult.Errors!;

        var subscription = subscriptionResult.Value;

        // Explicit commercial alignment invariant — never trust derivation alone:
        //   Contract.EffectiveAtUtc == Subscription.StartsAtUtc
        //   Contract.EndsAtUtc == Subscription.EffectiveEndsAtUtc
        //   Contract.BonusMonths == Subscription.BonusMonths
        if (subscription.StartsAtUtc != contract.EffectiveAtUtc)
            return Error.Conflict("Contract.SubscriptionStartMismatch",
                $"Subscription starts at {subscription.StartsAtUtc:O} but Contract is effective at {contract.EffectiveAtUtc:O}.");

        if (subscription.EffectiveEndsAtUtc != contract.EndsAtUtc)
            return Error.Conflict("Contract.SubscriptionEndMismatch",
                $"Subscription ends at {subscription.EffectiveEndsAtUtc:O} but Contract ends at {contract.EndsAtUtc:O}.");

        if (subscription.BonusMonths != contract.BonusMonths)
            return Error.Conflict("Contract.SubscriptionBonusMismatch",
                $"Subscription has {subscription.BonusMonths} bonus months but Contract has {contract.BonusMonths}.");

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
