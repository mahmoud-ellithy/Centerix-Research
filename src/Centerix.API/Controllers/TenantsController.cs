using Centerix.Application.Common.Interfaces;
using Centerix.Application.Tenants;
using Centerix.Infrastructure.Auth;
using Microsoft.AspNetCore.Mvc;

namespace Centerix.API.Controllers;

[Route("api/[controller]")]
public class TenantsController(ILocalizer localizer, ITenantService tenantService) : ApiController(localizer)
{
    private readonly ITenantService _tenantService = tenantService;

    [HttpGet]
    [HasPermission(Permissions.Tenants.Read)]
    public async Task<IActionResult> GetTenants(CancellationToken cancellationToken)
    {
        var tenants = await _tenantService.GetTenantsAsync(cancellationToken);
        return Ok(tenants);
    }

    [HttpGet("{id}")]
    [HasPermission(Permissions.Tenants.Read)]
    public async Task<IActionResult> GetTenant(string id, CancellationToken cancellationToken)
    {
        var tenant = await _tenantService.GetTenantByIdAsync(id, cancellationToken);
        if (tenant is null)
        {
            return NotFound();
        }
        return Ok(tenant);
    }

    [HttpPost]
    [HasPermission(Permissions.Tenants.Create)]
    public async Task<IActionResult> CreateTenant(CreateTenantRequest request, CancellationToken cancellationToken)
    {
        var tenant = await _tenantService.CreateTenantAsync(request, cancellationToken);
        return CreatedAtAction(nameof(GetTenant), new { id = tenant.Id }, tenant);
    }

    [HttpPut("{id}/deactivate")]
    [HasPermission(Permissions.Tenants.Update)]
    public async Task<IActionResult> DeactivateTenant(string id, CancellationToken cancellationToken)
    {
        await _tenantService.DeactivateTenantAsync(id, cancellationToken);
        return NoContent();
    }

    [HttpPut("{id}/activate")]
    [HasPermission(Permissions.Tenants.Update)]
    public async Task<IActionResult> ActivateTenant(string id, CancellationToken cancellationToken)
    {
        await _tenantService.ActivateTenantAsync(id, cancellationToken);
        return NoContent();
    }

    [HttpPut("{id}/subscription")]
    [HasPermission(Permissions.Tenants.Update)]
    public async Task<IActionResult> UpdateSubscription(string id, [FromBody] UpdateSubscriptionRequest request, CancellationToken cancellationToken)
    {
        await _tenantService.UpdateTenantSubscriptionAsync(id, request.NewExpiryDate, cancellationToken);
        return NoContent();
    }
}