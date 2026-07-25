namespace Centerix.Infrastructure.Platform;

using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform;
using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing;
using Centerix.Domain.Platform.Billing.Enums;
using Centerix.Domain.Platform.Features;
using Centerix.Domain.Platform.Leads;
using Centerix.Domain.Platform.Leads.Enums;
using Centerix.Domain.Platform.Plans;
using Centerix.Domain.Platform.Subscriptions;
using Centerix.Domain.Platform.Subscriptions.Enums;
using Mapster;
using Microsoft.EntityFrameworkCore;

public class PlatformService(
    IAppDbContext dbContext,
    TimeProvider timeProvider,
    ILocalizer localizer,
    IAuditWriter auditWriter) : IPlatformService
{
    private readonly IAppDbContext _dbContext = dbContext;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILocalizer _localizer = localizer;
    private readonly IAuditWriter _auditWriter = auditWriter;

    public async Task<Result<IEnumerable<PlanDto>>> GetPlansAsync(CancellationToken cancellationToken)
    {
        var plans = await _dbContext.Plans
            .Where(p => p.IsActive)
            .ProjectToType<PlanDto>()
            .ToListAsync(cancellationToken);
        return plans;
    }

    public async Task<Result<PlanDto>> GetPlanByIdAsync(int id, CancellationToken cancellationToken)
    {
        var plan = await _dbContext.Plans
            .Where(p => p.Id == id)
            .ProjectToType<PlanDto>()
            .FirstOrDefaultAsync(cancellationToken);

        if (plan is null)
        {
            return Error.NotFound("Plan.NotFound", $"Plan with id '{id}' was not found.");
        }

        return plan;
    }

    public async Task<Result<Created>> CreatePlanAsync(PlanDto planDto, CancellationToken cancellationToken)
    {
        var planResult = Plan.Create(
            0,
            planDto.Code,
            planDto.DisplayName,
            planDto.MonthlyPrice,
            planDto.MaxStudents,
            planDto.MaxUsers,
            planDto.MaxBranches,
            planDto.MaxTeachers,
            planDto.StorageGB,
            planDto.SMSQuota,
            planDto.IsActive);

        if (!planResult.IsSuccess)
            return planResult.Errors!;

        _dbContext.Plans.Add(planResult.Value);
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditWriter.WriteAsync(
            action: "Plan.Create",
            entityType: nameof(Plan),
            entityId: planResult.Value.Id.ToString(),
            newValue: AuditPayload.Serialize(new { planResult.Value.Code, planResult.Value.DisplayName, planResult.Value.MonthlyPrice, planResult.Value.IsActive }),
            cancellationToken: cancellationToken);

        return Result.Created;
    }

    public async Task<Result<Updated>> UpdatePlanAsync(int id, PlanDto planDto, CancellationToken cancellationToken)
    {
        var plan = await _dbContext.Plans.FindAsync([id], cancellationToken: cancellationToken);
        if (plan is null)
        {
            return Error.NotFound("Plan.NotFound", $"Plan with id '{id}' was not found.");
        }

        var oldValue = AuditPayload.Serialize(new { plan.Code, plan.DisplayName, plan.MonthlyPrice, plan.IsActive });

        plan.Update(
            planDto.Code,
            planDto.DisplayName,
            planDto.MonthlyPrice,
            planDto.MaxStudents,
            planDto.MaxUsers,
            planDto.MaxBranches,
            planDto.MaxTeachers,
            planDto.StorageGB,
            planDto.SMSQuota,
            planDto.IsActive);

        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditWriter.WriteAsync(
            action: "Plan.Update",
            entityType: nameof(Plan),
            entityId: id.ToString(),
            oldValue: oldValue,
            newValue: AuditPayload.Serialize(new { plan.Code, plan.DisplayName, plan.MonthlyPrice, plan.IsActive }),
            cancellationToken: cancellationToken);

        return Result.Updated;
    }

    public async Task<Result<Deleted>> DeletePlanAsync(int id, CancellationToken cancellationToken)
    {
        var plan = await _dbContext.Plans.FindAsync([id], cancellationToken: cancellationToken);
        if (plan is null)
        {
            return Error.NotFound("Plan.NotFound", $"Plan with id '{id}' was not found.");
        }

        var oldValue = AuditPayload.Serialize(new { plan.Code, plan.DisplayName, plan.MonthlyPrice, plan.IsActive });

        _dbContext.Plans.Remove(plan);
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditWriter.WriteAsync(
            action: "Plan.Delete",
            entityType: nameof(Plan),
            entityId: id.ToString(),
            oldValue: oldValue,
            cancellationToken: cancellationToken);

        return Result.Deleted;
    }

    public async Task<Result<IEnumerable<FeatureDto>>> GetFeaturesAsync(CancellationToken cancellationToken)
    {
        var features = await _dbContext.Features
            .ProjectToType<FeatureDto>()
            .ToListAsync(cancellationToken);
        return features;
    }

    public async Task<Result<FeatureDto>> GetFeatureByIdAsync(int id, CancellationToken cancellationToken)
    {
        var feature = await _dbContext.Features
            .Where(f => f.Id == id)
            .ProjectToType<FeatureDto>()
            .FirstOrDefaultAsync(cancellationToken);

        if (feature is null)
        {
            return Error.NotFound("Feature.NotFound", $"Feature with id '{id}' was not found.");
        }

        return feature;
    }

    public async Task<Result<Created>> CreateFeatureAsync(FeatureDto featureDto, CancellationToken cancellationToken)
    {
        var featureResult = Feature.Create(
            0,
            featureDto.Code,
            featureDto.Description,
            featureDto.Module);

        if (!featureResult.IsSuccess)
            return featureResult.Errors!;

        _dbContext.Features.Add(featureResult.Value);
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditWriter.WriteAsync(
            action: "Feature.Create",
            entityType: nameof(Feature),
            entityId: featureResult.Value.Id.ToString(),
            newValue: AuditPayload.Serialize(new { featureResult.Value.Code, featureResult.Value.Description, featureResult.Value.Module }),
            cancellationToken: cancellationToken);

        return Result.Created;
    }

    public async Task<Result<Updated>> UpdateFeatureAsync(int id, FeatureDto featureDto, CancellationToken cancellationToken)
    {
        var feature = await _dbContext.Features.FindAsync([id], cancellationToken: cancellationToken);
        if (feature is null)
        {
            return Error.NotFound("Feature.NotFound", $"Feature with id '{id}' was not found.");
        }

        var oldValue = AuditPayload.Serialize(new { feature.Code, feature.Description, feature.Module });

        var updateResult = feature.Update(
            featureDto.Code,
            featureDto.Description,
            featureDto.Module);

        if (!updateResult.IsSuccess)
            return updateResult.Errors!;

        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditWriter.WriteAsync(
            action: "Feature.Update",
            entityType: nameof(Feature),
            entityId: id.ToString(),
            oldValue: oldValue,
            newValue: AuditPayload.Serialize(new { feature.Code, feature.Description, feature.Module }),
            cancellationToken: cancellationToken);

        return Result.Updated;
    }

    public async Task<Result<Deleted>> DeleteFeatureAsync(int id, CancellationToken cancellationToken)
    {
        var feature = await _dbContext.Features.FindAsync([id], cancellationToken: cancellationToken);
        if (feature is null)
        {
            return Error.NotFound("Feature.NotFound", $"Feature with id '{id}' was not found.");
        }

        var oldValue = AuditPayload.Serialize(new { feature.Code, feature.Description, feature.Module });

        _dbContext.Features.Remove(feature);
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditWriter.WriteAsync(
            action: "Feature.Delete",
            entityType: nameof(Feature),
            entityId: id.ToString(),
            oldValue: oldValue,
            cancellationToken: cancellationToken);

        return Result.Deleted;
    }

    public async Task<Result<IEnumerable<TenantPlanDto>>> GetTenantPlansAsync(CancellationToken cancellationToken)
    {
        var tenantPlans = await _dbContext.TenantPlans
            .Include(tp => tp.Plan)
            .ProjectToType<TenantPlanDto>()
            .ToListAsync(cancellationToken);

        foreach (var tp in tenantPlans)
        {
            var status = (SubscriptionStatus)tp.Status;
            tp.StatusLabel = _localizer.Translate($"Enum:SubscriptionStatus.{status}");
        }

        return tenantPlans;
    }

    public async Task<Result<Created>> CreateTenantPlanAsync(TenantPlanDto tenantPlanDto, CancellationToken cancellationToken)
    {
        var planResult = TenantPlan.Create(
            Guid.NewGuid(),
            tenantPlanDto.PlanId,
            tenantPlanDto.StartsAt,
            tenantPlanDto.AutoRenew,
            (SubscriptionStatus)tenantPlanDto.Status);

        if (!planResult.IsSuccess)
            return planResult.Errors!;

        _dbContext.TenantPlans.Add(planResult.Value);
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditWriter.WriteAsync(
            action: "TenantPlan.Create",
            entityType: nameof(TenantPlan),
            entityId: planResult.Value.Id.ToString(),
            newValue: AuditPayload.Serialize(new { planResult.Value.PlanId, planResult.Value.StartsAt, planResult.Value.AutoRenew }),
            cancellationToken: cancellationToken);

        return Result.Created;
    }

    public async Task<Result<Updated>> UpdateTenantPlanAsync(Guid id, TenantPlanDto tenantPlanDto, CancellationToken cancellationToken)
    {
        var tenantPlan = await _dbContext.TenantPlans.FindAsync([id], cancellationToken: cancellationToken);
        if (tenantPlan is null)
        {
            return Error.NotFound("TenantPlan.NotFound", $"TenantPlan with id '{id}' was not found.");
        }

        var oldValue = AuditPayload.Serialize(new { tenantPlan.EndsAt, tenantPlan.AutoRenew });

        var updateResult = tenantPlan.Update(
            tenantPlanDto.EndsAt,
            tenantPlanDto.AutoRenew);

        if (!updateResult.IsSuccess)
            return updateResult.Errors!;

        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditWriter.WriteAsync(
            action: "TenantPlan.Update",
            entityType: nameof(TenantPlan),
            entityId: id.ToString(),
            oldValue: oldValue,
            newValue: AuditPayload.Serialize(new { tenantPlan.EndsAt, tenantPlan.AutoRenew }),
            cancellationToken: cancellationToken);

        return Result.Updated;
    }

    public async Task<Result<IEnumerable<TenantBillingDto>>> GetTenantBillingsAsync(CancellationToken cancellationToken)
    {
        var billings = await _dbContext.TenantBillings
            .Include(tb => tb.Plan)
            .OrderByDescending(tb => tb.CreatedAt)
            .ProjectToType<TenantBillingDto>()
            .ToListAsync(cancellationToken);

        foreach (var billing in billings)
        {
            var status = (BillingStatus)billing.Status;
            billing.StatusLabel = _localizer.Translate($"Enum:BillingStatus.{status}");
        }

        return billings;
    }

    public async Task<Result<Created>> CreateTenantBillingAsync(TenantBillingDto billingDto, CancellationToken cancellationToken)
    {
        var billingResult = TenantBilling.Create(
            Guid.NewGuid(),
            billingDto.PlanId,
            billingDto.AmountEGP,
            billingDto.Method,
            (BillingStatus)billingDto.Status,
            _timeProvider.GetUtcNow().DateTime);

        if (!billingResult.IsSuccess)
            return billingResult.Errors!;

        _dbContext.TenantBillings.Add(billingResult.Value);
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditWriter.WriteAsync(
            action: "TenantBilling.Create",
            entityType: nameof(TenantBilling),
            entityId: billingResult.Value.Id.ToString(),
            newValue: AuditPayload.Serialize(new { billingResult.Value.PlanId, billingResult.Value.AmountEGP, billingResult.Value.Method }),
            cancellationToken: cancellationToken);

        return Result.Created;
    }

    public async Task<Result<IEnumerable<TenantCRMLeadDto>>> GetTenantCRMLeadsAsync(CancellationToken cancellationToken)
    {
        var leads = await _dbContext.TenantCRMLeads
            .OrderByDescending(tc => tc.CreatedAt)
            .ProjectToType<TenantCRMLeadDto>()
            .ToListAsync(cancellationToken);

        foreach (var lead in leads)
        {
            if (Enum.TryParse<LeadStage>(lead.Stage, out var stage))
            {
                lead.StageLabel = _localizer.Translate($"Enum:LeadStage.{stage}");
            }
        }

        return leads;
    }

    public async Task<Result<Created>> CreateTenantCRMLeadAsync(TenantCRMLeadDto leadDto, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<LeadStage>(leadDto.Stage, out var stage))
        {
            return Error.Validation("Lead.Stage_Invalid", $"Invalid lead stage '{leadDto.Stage}'.");
        }

        var leadResult = TenantCRMLead.Create(
            Guid.NewGuid(),
            leadDto.CenterName,
            leadDto.ContactName,
            leadDto.Phone,
            leadDto.Source,
            stage,
            leadDto.AssignedTo,
            _timeProvider.GetUtcNow().DateTime);

        if (!leadResult.IsSuccess)
            return leadResult.Errors!;

        _dbContext.TenantCRMLeads.Add(leadResult.Value);
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditWriter.WriteAsync(
            action: "TenantCRMLead.Create",
            entityType: nameof(TenantCRMLead),
            entityId: leadResult.Value.Id.ToString(),
            newValue: AuditPayload.Serialize(new { leadResult.Value.CenterName, leadResult.Value.ContactName, leadResult.Value.Phone, leadResult.Value.Stage }),
            cancellationToken: cancellationToken);

        return Result.Created;
    }

    public async Task<Result<Updated>> UpdateTenantCRMLeadAsync(Guid id, TenantCRMLeadDto leadDto, CancellationToken cancellationToken)
    {
        var lead = await _dbContext.TenantCRMLeads.FindAsync([id], cancellationToken: cancellationToken);
        if (lead is null)
        {
            return Error.NotFound("TenantCRMLead.NotFound", $"TenantCRMLead with id '{id}' was not found.");
        }

        var oldValue = AuditPayload.Serialize(new { lead.CenterName, lead.ContactName, lead.Phone, lead.Stage });

        lead.Update(
            leadDto.CenterName,
            leadDto.ContactName,
            leadDto.Phone,
            leadDto.Source,
            leadDto.Stage,
            leadDto.AssignedTo);

        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditWriter.WriteAsync(
            action: "TenantCRMLead.Update",
            entityType: nameof(TenantCRMLead),
            entityId: id.ToString(),
            oldValue: oldValue,
            newValue: AuditPayload.Serialize(new { lead.CenterName, lead.ContactName, lead.Phone, lead.Stage }),
            cancellationToken: cancellationToken);

        return Result.Updated;
    }
}
