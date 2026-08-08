using Centerix.Domain.Common.Results;

namespace Centerix.Application.Platform;

public interface IPlatformService
{
    Task<Result<IEnumerable<PlanDto>>> GetPlansAsync(CancellationToken cancellationToken);
    Task<Result<PlanDto>> GetPlanByIdAsync(int id, CancellationToken cancellationToken);
    Task<Result<Created>> CreatePlanAsync(PlanDto plan, CancellationToken cancellationToken);
    Task<Result<Updated>> UpdatePlanAsync(int id, PlanDto plan, CancellationToken cancellationToken);
    Task<Result<Deleted>> DeletePlanAsync(int id, CancellationToken cancellationToken);

    Task<Result<IEnumerable<FeatureDto>>> GetFeaturesAsync(CancellationToken cancellationToken);
    Task<Result<FeatureDto>> GetFeatureByIdAsync(int id, CancellationToken cancellationToken);
    Task<Result<Created>> CreateFeatureAsync(FeatureDto feature, CancellationToken cancellationToken);
    Task<Result<Updated>> UpdateFeatureAsync(int id, FeatureDto feature, CancellationToken cancellationToken);
    Task<Result<Deleted>> DeleteFeatureAsync(int id, CancellationToken cancellationToken);

    Task<Result<IEnumerable<TenantPlanDto>>> GetTenantPlansAsync(CancellationToken cancellationToken);
    Task<Result<Created>> CreateTenantPlanAsync(TenantPlanDto tenantPlanDto, CancellationToken cancellationToken);
    Task<Result<Updated>> UpdateTenantPlanAsync(Guid id, TenantPlanDto tenantPlanDto, CancellationToken cancellationToken);

    Task<Result<IEnumerable<TenantCRMLeadDto>>> GetTenantCRMLeadsAsync(CancellationToken cancellationToken);
    Task<Result<Created>> CreateTenantCRMLeadAsync(TenantCRMLeadDto leadDto, CancellationToken cancellationToken);
    Task<Result<Updated>> UpdateTenantCRMLeadAsync(Guid id, TenantCRMLeadDto leadDto, CancellationToken cancellationToken);
}
