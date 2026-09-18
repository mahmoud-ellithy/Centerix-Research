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

    /// <summary>PLATFORM: renews (extends) the tenant's current subscription by appending months.</summary>
    [HttpPost("renew")]
    [HasPermission(Permissions.Subscriptions.Manage)]
    public async Task<IActionResult> RenewSubscription(RenewSubscriptionCommand command, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(command, cancellationToken);

        return result.Match(
            _ => NoContent(),
            Problem);
    }

    /// <summary>
    /// PLATFORM: Renews a subscription as a NEW commercial transaction.
    /// Creates a new Offer → Contract → Subscription chain using current commercial terms.
    /// Old promotions/discounts/benefits are NOT inherited. The old subscription remains immutable.
    /// </summary>
    [HttpPost("{id}/renew-commercial")]
    [HasPermission(Permissions.Subscriptions.Manage)]
    public async Task<IActionResult> RenewSubscriptionCommercial(
        Guid id,
        [FromBody] RenewSubscriptionCommercialRequest request,
        CancellationToken cancellationToken)
    {
        var command = new RenewSubscriptionOfferCommand(
            id,
            request?.PlanId,
            request?.DurationMonths);

        var result = await mediator.Send(command, cancellationToken);

        return result.Match(
            contractId => Ok(new
            {
                ContractId = contractId,
                Message = "Renewal completed as a new commercial transaction. New Contract and Subscription created."
            }),
            Problem);
    }

    /// <summary>
    /// PLATFORM: Changes a subscription to a different plan (upgrade or downgrade).
    /// Creates a new Offer → Contract → Subscription chain using the target plan's current commercial terms.
    /// The old subscription is ended (cancelled) at the effective date.
    /// Old promotions/discounts/benefits are NOT inherited. The old subscription's snapshot is never modified.
    /// No automatic early-cancellation refund is triggered.
    /// </summary>
    [HttpPost("{id}/change-plan")]
    [HasPermission(Permissions.Subscriptions.Manage)]
    public async Task<IActionResult> ChangePlan(
        Guid id,
        [FromBody] ChangePlanRequest request,
        CancellationToken cancellationToken)
    {
        var command = new ChangeSubscriptionPlanCommand(
            id,
            request.NewPlanId);

        var result = await mediator.Send(command, cancellationToken);

        return result.Match(
            contractId => Ok(new
            {
                ContractId = contractId,
                Message = "Plan changed successfully. New Contract and Subscription created. Old subscription ended."
            }),
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

    /// <summary>PLATFORM: cancels the current subscription (history preserved, no contract).</summary>
    [HttpPost("cancel")]
    [HasPermission(Permissions.Subscriptions.Manage)]
    public async Task<IActionResult> CancelSubscription(CancelSubscriptionCommand command, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(command, cancellationToken);

        return result.Match(
            _ => NoContent(),
            Problem);
    }

    /// <summary>
    /// PLATFORM: cancels a subscription with full financial calculation.
    /// For subscriptions linked to a Contract, this performs refund/outstanding calculation
    /// using historical Contract pricing. This is the AUTHORITATIVE cancellation workflow.
    /// </summary>
    [HttpPost("{id}/cancel")]
    [HasPermission(Permissions.Subscriptions.Manage)]
    public async Task<IActionResult> CancelSubscriptionWithFinancials(
        Guid id,
        [FromBody] CancelSubscriptionRequest request,
        CancellationToken cancellationToken)
    {
        var command = new Centerix.Application.Platform.Billing.Commands.CancelSubscriptionCommand(
            id,
            request?.CancellationDateUtc ?? DateTime.UtcNow,
            request?.Reason ?? "Platform cancellation");

        var result = await mediator.Send(command, cancellationToken);

        return result.Match(
            cancellation => Ok(cancellation),
            Problem);
    }
}

/// <summary>
/// Request body for the commercial renewal endpoint.
/// Only PlanId and DurationMonths are allowed as optional overrides.
/// All commercial values (price, discount, benefits) are server-derived from the Offer engine.
/// </summary>
public class RenewSubscriptionCommercialRequest
{
    /// <summary>Optional: override the plan for renewal. Null = use current plan.</summary>
    public int? PlanId { get; set; }

    /// <summary>Optional: override the duration. Null = use plan's default duration.</summary>
    public int? DurationMonths { get; set; }
}

/// <summary>
/// Request body for the financial cancellation endpoint.
/// CancellationDateUtc is validated against the subscription lifecycle.
/// </summary>
public class CancelSubscriptionRequest
{
    /// <summary>The effective cancellation date (UTC). Must be validated against subscription lifecycle.</summary>
    public DateTime CancellationDateUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Business reason for the cancellation.</summary>
    public string Reason { get; set; } = "Platform cancellation";
}

/// <summary>
/// Request body for the change-plan endpoint.
/// Only NewPlanId is accepted. All commercial values are server-derived from the Offer engine.
/// </summary>
public class ChangePlanRequest
{
    /// <summary>Required: the target plan ID to change to.</summary>
    public int NewPlanId { get; set; }
}
