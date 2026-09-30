using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Contracts.Commands;
using Centerix.Application.Platform.Contracts.Queries;
using Centerix.Application.Platform.Promotions.Commands;
using Centerix.Infrastructure.Auth;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Centerix.API.Controllers;

[Route("api/[controller]")]
public class ContractsController(ILocalizer localizer, IMediator mediator) : ApiController(localizer)
{
    [HttpGet]
    [HasPermission(Permissions.Contracts.Read)]
    public async Task<IActionResult> GetContracts(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new ListContractsQuery(), cancellationToken);

        return result.Match(
            contracts => Ok(contracts),
            Problem);
    }

    [HttpGet("{id}")]
    [HasPermission(Permissions.Contracts.Read)]
    public async Task<IActionResult> GetContract(Guid id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetContractByIdQuery(id), cancellationToken);

        return result.Match(
            contract => Ok(contract),
            Problem);
    }

    /// <summary>
    /// Creates a Contract from an accepted Offer. ALL commercial values are derived
    /// from the server-side Offer snapshot. The client CANNOT override any authoritative
    /// commercial values (ContractedAmount, DiscountAmount, PromotionId, ChargedMonths, etc.).
    /// Benefits are copied from the Offer snapshot — the client cannot inject or override them.
    /// </summary>
    [HttpPost("from-offer")]
    [HasPermission(Permissions.Contracts.Create)]
    public async Task<IActionResult> CreateContractFromOffer(
        [FromBody] CreateContractFromOfferRequest request,
        CancellationToken cancellationToken)
    {
        var command = new CreateContractFromOfferCommand(
            request.OfferId,
            request.ContractNumber,
            request.EffectiveAtUtc);

        var result = await mediator.Send(command, cancellationToken);

        return result.Match(
            contractId => CreatedAtAction(nameof(GetContract), new { id = contractId }, contractId),
            Problem);
    }

    /// <summary>
    /// Grants a FreeMonthsBenefit (Pending → Granted). Idempotent: returns success
    /// without mutation if the benefit is already Granted.
    /// Requires: benefit exists, belongs to the caller's tenant, is Eligible and Pending.
    /// </summary>
    [HttpPost("free-months/{benefitId}/grant")]
    [HasPermission(Permissions.Benefits.Manage)]
    public async Task<IActionResult> GrantFreeMonthsBenefit(
        Guid benefitId,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GrantFreeMonthsBenefitCommand(benefitId), cancellationToken);

        return result.Match(
            r => Ok(r),
            Problem);
    }

    /// <summary>
    /// Applies a Granted FreeMonthsBenefit to the tenant's active subscription,
    /// extending the subscription entitlement by the benefit's EntitlementMonths.
    /// Idempotent: returns success without re-extending if the benefit is already Applied.
    /// Requires: benefit exists, belongs to the caller's tenant, is Granted.
    /// The subscription is extended atomically with the benefit state transition.
    /// </summary>
    [HttpPost("free-months/{benefitId}/apply")]
    [HasPermission(Permissions.Benefits.Manage)]
    public async Task<IActionResult> ApplyFreeMonthsBenefit(
        Guid benefitId,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new ApplyFreeMonthsBenefitToSubscriptionCommand(benefitId), cancellationToken);

        return result.Match(
            r => Ok(r),
            Problem);
    }
}

public class CreateContractFromOfferRequest
{
    public Guid OfferId { get; set; }
    public string ContractNumber { get; set; } = default!;
    public DateTime? EffectiveAtUtc { get; set; }
}
