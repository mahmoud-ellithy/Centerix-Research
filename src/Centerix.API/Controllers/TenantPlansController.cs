using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform;
using Centerix.Infrastructure.Auth;
using Microsoft.AspNetCore.Mvc;

namespace Centerix.API.Controllers;

[Route("api/[controller]")]
public class TenantPlansController(ILocalizer localizer, IPlatformService platformService) : ApiController(localizer)
{
    private readonly IPlatformService _platformService = platformService;

    [HttpGet]
    [HasPermission(Permissions.TenantPlans.Read)]
    public async Task<IActionResult> GetTenantPlans(CancellationToken cancellationToken)
    {
        var result = await _platformService.GetTenantPlansAsync(cancellationToken);
        return result.Match(
            plans => Ok(plans),
            Problem);
    }

    [HttpPost]
    [HasPermission(Permissions.TenantPlans.Create)]
    public async Task<IActionResult> CreateTenantPlan(TenantPlanDto tenantPlan, CancellationToken cancellationToken)
    {
        var result = await _platformService.CreateTenantPlanAsync(tenantPlan, cancellationToken);

        return result.Match(
            _ => StatusCode(StatusCodes.Status201Created),
            Problem);
    }

    [HttpPut("{id}")]
    [HasPermission(Permissions.TenantPlans.Update)]
    public async Task<IActionResult> UpdateTenantPlan(Guid id, TenantPlanDto tenantPlan, CancellationToken cancellationToken)
    {
        var result = await _platformService.UpdateTenantPlanAsync(id, tenantPlan, cancellationToken);

        return result.Match(
            _ => NoContent(),
            Problem);
    }
}
