using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Tenants;
using Centerix.Application.Platform.Tenants.Commands;
using Centerix.Application.Platform.Tenants.Queries;
using Centerix.Infrastructure.Auth;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Centerix.API.Controllers;

[Route("api/[controller]")]
public class TenantsController(ILocalizer localizer, IMediator mediator) : ApiController(localizer)
{
    [HttpGet]
    [HasPermission(Permissions.Tenants.Read)]
    public async Task<IActionResult> GetTenants(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetTenantsQuery(), cancellationToken);

        return result.Match(
            tenants => Ok(tenants),
            Problem);
    }

    [HttpGet("{id:guid}")]
    [HasPermission(Permissions.Tenants.Read)]
    public async Task<IActionResult> GetTenant(Guid id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetTenantByIdQuery(id), cancellationToken);

        return result.Match(
            tenant => Ok(tenant),
            Problem);
    }

    [HttpPost]
    [HasPermission(Permissions.Tenants.Create)]
    public async Task<IActionResult> CreateTenant(CreateTenantCommand command, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(command, cancellationToken);

        return result.Match(
            _ => StatusCode(StatusCodes.Status201Created),
            Problem);
    }

    /// <summary>PLATFORM-ONLY: approves a pending tenant and assigns its first subscription.</summary>
    [HttpPost("{id:guid}/approve")]
    [HasPermission(Permissions.Subscriptions.Manage)]
    public async Task<IActionResult> ApproveTenant(Guid id, ApproveTenantCommand command, CancellationToken cancellationToken)
    {
        if (id != command.TenantId)
        {
            return BadRequest(new { detail = "Route id does not match command tenant id." });
        }

        var result = await mediator.Send(command, cancellationToken);

        return result.Match(
            _ => StatusCode(StatusCodes.Status201Created),
            Problem);
    }

    /// <summary>PLATFORM-ONLY: rejects a pending tenant application.</summary>
    [HttpPost("{id:guid}/reject")]
    [HasPermission(Permissions.Tenants.Update)]
    public async Task<IActionResult> RejectTenant(Guid id, RejectTenantCommand command, CancellationToken cancellationToken)
    {
        if (id != command.TenantId)
        {
            return BadRequest(new { detail = "Route id does not match command tenant id." });
        }

        var result = await mediator.Send(command, cancellationToken);

        return result.Match(
            _ => NoContent(),
            Problem);
    }

    /// <summary>PLATFORM-ONLY: completes provisioning (Provisioning → Active).</summary>
    [HttpPost("{id:guid}/activate")]
    [HasPermission(Permissions.Tenants.Update)]
    public async Task<IActionResult> ActivateTenant(Guid id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new ActivateTenantCommand(id), cancellationToken);

        return result.Match(
            _ => NoContent(),
            Problem);
    }

    [HttpPut("{id:guid}")]
    [HasPermission(Permissions.Tenants.Update)]
    public async Task<IActionResult> UpdateTenant(Guid id, UpdateTenantCommand command, CancellationToken cancellationToken)
    {
        if (id != command.Id)
        {
            return BadRequest(new { detail = "Route id does not match command id." });
        }

        var result = await mediator.Send(command, cancellationToken);

        return result.Match(
            _ => NoContent(),
            Problem);
    }

    [HttpPost("{id:guid}/suspend")]
    [HasPermission(Permissions.Tenants.Update)]
    public async Task<IActionResult> SuspendTenant(Guid id, SuspendTenantCommand command, CancellationToken cancellationToken)
    {
        if (id != command.Id)
        {
            return BadRequest(new { detail = "Route id does not match command id." });
        }

        var result = await mediator.Send(command, cancellationToken);

        return result.Match(
            _ => NoContent(),
            Problem);
    }

    [HttpPost("{id:guid}/reactivate")]
    [HasPermission(Permissions.Tenants.Update)]
    public async Task<IActionResult> ReactivateTenant(Guid id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new ReactivateTenantCommand(id), cancellationToken);

        return result.Match(
            _ => NoContent(),
            Problem);
    }

    [HttpDelete("{id:guid}")]
    [HasPermission(Permissions.Tenants.Delete)]
    public async Task<IActionResult> CancelTenant(Guid id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new CancelTenantCommand(id), cancellationToken);

        return result.Match(
            _ => NoContent(),
            Problem);
    }
}
