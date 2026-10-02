using Centerix.Domain.Common.Results;

namespace Centerix.Application.Platform.Contracts.Services;

/// <summary>
/// Application service that freezes the eligibility snapshot of a <c>ContractBenefit</c> by
/// evaluating the closed rule algebra against current tenant facts. This is the only service
/// permitted to transition a benefit from <c>NotEligible</c> to <c>Eligible</c>.
/// </summary>
/// <remarks>
/// <para>
/// The freeze pipeline:
/// <list type="number">
///   <item><description>Build an <c>EligibilityContext</c> via the owner-only fact query.</description></item>
///   <item><description>Evaluate the benefit's frozen rule snapshot.</description></item>
///   <item><description>If <c>IsEligible == true</c>, call <c>MarkEligible</c> and persist.</description></item>
///   <item><description>If <c>IsEligible == false</c>, return the reason — do not mutate
///   <c>FulfillmentStatus</c>, <c>GrantedAtUtc</c>, or <c>GrantedBy</c>.</description></item>
/// </list>
/// </para>
/// </remarks>
public interface IFreezeEligibilityService
{
    /// <summary>
    /// Freezes the eligibility snapshot for the benefit identified by <paramref name="benefitId"/>.
    /// </summary>
    Task<Result<FreezeEligibilityResponse>> FreezeBenefitAsync(
        Guid benefitId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Typed result returned by <see cref="IFreezeEligibilityService"/>.
/// </summary>
/// <param name="Success">Whether the freeze pipeline completed without errors.</param>
/// <param name="BenefitId">The benefit whose eligibility was evaluated.</param>
/// <param name="IsEligible">The outcome of the rule evaluation.</param>
/// <param name="ReasonCode">Stable reason code (from <c>WhyIneligible</c>) when not eligible.</param>
/// <param name="ReasonPath">Stable rule-branch identifier; useful for audit logs.</param>
/// <param name="EvaluatedAt">UTC timestamp of the evaluation.</param>
/// <param name="StatusChanged">Whether <c>MarkEligible</c> was called and the benefit transitioned.</param>
public sealed record FreezeEligibilityResponse(
    bool Success,
    Guid BenefitId,
    bool IsEligible,
    string? ReasonCode,
    string? ReasonPath,
    DateTime EvaluatedAt,
    bool StatusChanged);