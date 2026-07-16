using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform;
using Centerix.Infrastructure.Auth;
using Microsoft.AspNetCore.Mvc;

namespace Centerix.API.Controllers;

[Route("api/[controller]")]
public class TenantCRMLeadsController(ILocalizer localizer, IPlatformService platformService) : ApiController(localizer)
{
    private readonly IPlatformService _platformService = platformService;

    [HttpGet]
    [HasPermission(Permissions.TenantCRMLeads.Read)]
    public async Task<IActionResult> GetTenantCRMLeads(CancellationToken cancellationToken)
    {
        var result = await _platformService.GetTenantCRMLeadsAsync(cancellationToken);
        return result.Match(
            leads => Ok(leads),
            Problem);
    }

    [HttpPost]
    [HasPermission(Permissions.TenantCRMLeads.Create)]
    public async Task<IActionResult> CreateTenantCRMLead(TenantCRMLeadDto lead, CancellationToken cancellationToken)
    {
        var result = await _platformService.CreateTenantCRMLeadAsync(lead, cancellationToken);

        return result.Match(
            _ => StatusCode(StatusCodes.Status201Created),
            Problem);
    }

    [HttpPut("{id}")]
    [HasPermission(Permissions.TenantCRMLeads.Update)]
    public async Task<IActionResult> UpdateTenantCRMLead(Guid id, TenantCRMLeadDto lead, CancellationToken cancellationToken)
    {
        var result = await _platformService.UpdateTenantCRMLeadAsync(id, lead, cancellationToken);

        return result.Match(
            _ => NoContent(),
            Problem);
    }
}