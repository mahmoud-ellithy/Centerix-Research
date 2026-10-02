namespace Centerix.Infrastructure.Platform.Services;

using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Contracts.Services;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Contracts;
using Centerix.Domain.Platform.Contracts.EligibilityRules;
using Centerix.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Default implementation of <see cref="IFreezeEligibilityService"/>. The service uses
/// <c>EligibilityContextBuilder</c> to fetch tenant-scoped facts, runs the closed rule
/// evaluator, and synchronises the benefit's <c>EligibilityStatus</c> with the result.
/// </summary>
/// <remarks>
/// <para>
/// The service is registered as scoped because it depends on <c>IAppDbContext</c>. Per-request
/// lifetime guarantees that the entity-tracker stays within a single unit of work.
/// </para>
/// <para>
/// Cross-tenant access is blocked at two layers: the <c>IOwnerOnlyFactQuery</c> fact-query
/// seam (which throws <see cref="TenantScopeException"/> on cross-tenant rows), and the
/// post-load tenant guard on the benefit itself.
/// </para>
/// <para>
/// <b>Reversibility invariant:</b> the eligibility axis is <c>NotEligible ⇄ Eligible</c>.
/// Every freeze MUST converge <c>EligibilityStatus</c> with the evaluation outcome. The
/// evaluator's <c>IsEligible = true</c> result triggers <c>MarkEligible</c>; the <c>false</c>
/// result triggers <c>MarkNotEligible</c>. Neither path may mutate <c>FulfillmentStatus</c>,
/// <c>GrantedAtUtc</c>, <c>GrantedBy</c>, <c>DeliveredAtUtc</c>, <c>DeliveredBy</c>, or any
/// applied-to-subscription state.
/// </para>
/// </remarks>
public sealed class FreezeEligibilityService : IFreezeEligibilityService
{
    private readonly IAppDbContext _db;
    private readonly EligibilityContextBuilder _builder;
    private readonly EligibilityRuleEvaluator _evaluator;

    public FreezeEligibilityService(
        IAppDbContext db,
        EligibilityContextBuilder builder,
        EligibilityRuleEvaluator evaluator)
    {
        _db = db;
        _builder = builder;
        _evaluator = evaluator;
    }

    public async Task<Result<FreezeEligibilityResponse>> FreezeBenefitAsync(
        Guid benefitId,
        CancellationToken cancellationToken = default)
    {
        // IgnoreQueryFilters: a benefit belonging to another tenant is hidden by the global
        // filter; we check tenant ownership explicitly below to return CrossTenant.
        var benefit = await _db.ContractBenefits
            .IgnoreQueryFilters()
            .Include(b => b.Contract)
            .FirstOrDefaultAsync(b => b.Id == benefitId, cancellationToken);

        if (benefit is null)
            return ContractErrors.Benefit.NotFound(benefitId);

        var tenantId = _db.TenantId;
        if (string.IsNullOrWhiteSpace(tenantId) || benefit.Contract.TenantId != tenantId)
            return ContractErrors.Benefit.CrossTenantBenefit;

        var now = DateTime.UtcNow;

        EligibilityContext context;
        try
        {
            context = await _builder.BuildAsync(
                tenantId,
                benefit.ContractId,
                benefit.Id,
                now,
                cancellationToken);
        }
        catch (TenantScopeException)
        {
            return ContractErrors.Benefit.CrossTenantBenefit;
        }

        var rule = benefit.EligibilityRule;

        // No rule snapshot: there is nothing to evaluate. The benefit stays in its existing
        // EligibilityStatus (we do not mutate). No FulfillmentStatus / GrantedAtUtc / GrantedBy
        // mutation either.
        if (rule is null)
        {
            return new FreezeEligibilityResponse(
                Success: true,
                BenefitId: benefit.Id,
                IsEligible: false,
                ReasonCode: "ContractFreezing.NoRule",
                ReasonPath: null,
                EvaluatedAt: now,
                StatusChanged: false);
        }

        var evaluation = _evaluator.Evaluate(rule, context);

        // Reversibility: converge EligibilityStatus with the evaluation outcome. The two
        // branches below touch ONLY that one field via the existing domain methods. They MUST
        // NOT touch FulfillmentStatus / GrantedAtUtc / GrantedBy / DeliveredAtUtc / DeliveredBy.
        if (evaluation.IsEligible)
        {
            var statusChanged = benefit.EligibilityStatus
                != Domain.Platform.Contracts.Enums.BenefitEligibilityStatus.Eligible;

            if (statusChanged)
            {
                var markResult = benefit.MarkEligible(now, tenantId);
                if (!markResult.IsSuccess)
                    return markResult.Errors!;

                _db.StampAddedTenantIds(tenantId);
                await _db.SaveChangesAsync(cancellationToken);
            }

            return new FreezeEligibilityResponse(
                Success: true,
                BenefitId: benefit.Id,
                IsEligible: true,
                ReasonCode: null,
                ReasonPath: null,
                EvaluatedAt: now,
                StatusChanged: statusChanged);
        }
        else
        {
            // Ineligible: synchronise the status back to NotEligible. Fulfillment fields stay
            // untouched — a Granted/Delivered benefit remains granted/delivered.
            var statusChanged = benefit.EligibilityStatus
                != Domain.Platform.Contracts.Enums.BenefitEligibilityStatus.NotEligible;

            if (statusChanged)
            {
                var markResult = benefit.MarkNotEligible();
                if (!markResult.IsSuccess)
                    return markResult.Errors!;

                _db.StampAddedTenantIds(tenantId);
                await _db.SaveChangesAsync(cancellationToken);
            }

            return new FreezeEligibilityResponse(
                Success: true,
                BenefitId: benefit.Id,
                IsEligible: false,
                ReasonCode: evaluation.Reason.ToString(),
                ReasonPath: evaluation.ReasonPath,
                EvaluatedAt: now,
                StatusChanged: statusChanged);
        }
    }
}