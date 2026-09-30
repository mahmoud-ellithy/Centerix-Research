namespace Centerix.Application.Platform.Contracts.Commands;

using Centerix.Domain.Common.Results;
using MediatR;

/// <summary>
/// Applies a Granted FreeMonthsBenefit to the tenant's active subscription,
/// extending the subscription's entitlement end date by the benefit's EntitlementMonths.
/// Idempotent: if the benefit has already been applied, returns success without re-extending.
/// </summary>
/// <param name="BenefitId">The ID of the FreeMonthsBenefit to apply.</param>
public record ApplyFreeMonthsBenefitToSubscriptionCommand(Guid BenefitId)
    : IRequest<Result<ApplyFreeMonthsBenefitResult>>;

/// <summary>Result of a successful apply operation.</summary>
public sealed record ApplyFreeMonthsBenefitResult
{
    public Guid BenefitId { get; init; }
    public DateTime? AppliedAtUtc { get; init; }
    public bool IsAlreadyApplied { get; init; }
    public Guid SubscriptionId { get; init; }
    public DateTime EffectiveEndsAtUtc { get; init; }
}
