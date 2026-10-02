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
/// evaluator, and transitions the benefit to <c>Eligible</c> only when the rule passes.
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

        // Build the immutable fact aggregate through the owner-only fact query seam.
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
            // Surface as the existing typed error so callers see consistent error codes.
            return ContractErrors.Benefit.CrossTenantBenefit;
        }

        var rule = benefit.EligibilityRule;

        // No rule snapshot: nothing to evaluate. The benefit stays NotEligible. Do NOT
        // mutate FulfillmentStatus / GrantedAtUtc / GrantedBy.
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

        if (!evaluation.IsEligible)
        {
            // Ineligible: no MarkEligible, no fulfillment mutations.
            return new FreezeEligibilityResponse(
                Success: true,
                BenefitId: benefit.Id,
                IsEligible: false,
                ReasonCode: evaluation.Reason.ToString(),
                ReasonPath: evaluation.ReasonPath,
                EvaluatedAt: now,
                StatusChanged: false);
        }

        // Eligible: only transition if not already eligible.
        var statusChanged = false;
        if (benefit.EligibilityStatus == Domain.Platform.Contracts.Enums.BenefitEligibilityStatus.NotEligible)
        {
            var markResult = benefit.MarkEligible(now, tenantId);
            if (!markResult.IsSuccess)
                return markResult.Errors!;

            _db.StampAddedTenantIds(tenantId);
            await _db.SaveChangesAsync(cancellationToken);
            statusChanged = true;
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
}