using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Commands;
using Centerix.Application.Platform.Queries;
using Centerix.Application.Platform.Subscriptions.Queries;
using Centerix.Infrastructure.Auth;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Centerix.API.Controllers;

/// <summary>
/// Commercial subscription workflows. ALL mutating operations are PLATFORM-ONLY
/// (Subscriptions.Manage + in-handler platform guard). Tenants may read their OWN current
/// subscription via GET me (TenantPlans.Read, tenant-scoped).
/// </summary>
[Route("api/[controller]")]
public class TenantPlansController(ILocalizer localizer, IMediator mediator) : ApiController(localizer)
{
    /// <summary>PLATFORM: cross-tenant subscription listing.</summary>
    [HttpGet]
    [HasPermission(Permissions.Subscriptions.Read)]
    public async Task<IActionResult> GetTenantPlans(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetSubscriptionsQuery(), cancellationToken);

        return result.Match(
            plans => Ok(plans),
            Problem);
    }

    /// <summary>TENANT: the caller's own current subscription state (tenant-scoped read).</summary>
    [HttpGet("me")]
    [HasPermission(Permissions.TenantPlans.Read)]
    public async Task<IActionResult> GetMySubscription(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetMySubscriptionQuery(), cancellationToken);

        return result.Match(
            subscription => Ok(subscription),
            Problem);
    }

    /// <summary>PLATFORM: assigns (or re-assigns) a plan; supersedes any non-terminal subscription.</summary>
    [HttpPost]
    [HasPermission(Permissions.Subscriptions.Manage)]
    public async Task<IActionResult> AssignPlan(AssignPlanCommand command, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(command, cancellationToken);

        return result.Match(
            _ => StatusCode(StatusCodes.Status201Created),
            Problem);
    }

    /// <summary>PLATFORM: renews (extends) the tenant's current subscription.</summary>
    [HttpPost("renew")]
    [HasPermission(Permissions.Subscriptions.Manage)]
    public async Task<IActionResult> RenewSubscription(RenewSubscriptionCommand command, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(command, cancellationToken);

        return result.Match(
            _ => NoContent(),
            Problem);
    }

    /// <summary>PLATFORM: activates a Pending/Suspended subscription.</summary>
    [HttpPost("activate")]
    [HasPermission(Permissions.Subscriptions.Manage)]
    public async Task<IActionResult> ActivateSubscription(ActivateSubscriptionCommand command, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(command, cancellationToken);

        return result.Match(
            _ => NoContent(),
            Problem);
    }

    /// <summary>PLATFORM: suspends the active subscription (e.g. non-payment).</summary>
    [HttpPost("suspend")]
    [HasPermission(Permissions.Subscriptions.Manage)]
    public async Task<IActionResult> SuspendSubscription(SuspendSubscriptionCommand command, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(command, cancellationToken);

        return result.Match(
            _ => NoContent(),
            Problem);
    }

    /// <summary>PLATFORM: cancels the current subscription (history preserved).</summary>
    [HttpPost("cancel")]
    [HasPermission(Permissions.Subscriptions.Manage)]
    public async Task<IActionResult> CancelSubscription(CancelSubscriptionCommand command, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(command, cancellationToken);

        return result.Match(
            _ => NoContent(),
            Problem);
    }
}
