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
            request.EffectiveAtUtc,
            request.Benefits);

        var result = await mediator.Send(command, cancellationToken);

        return result.Match(
            contractId => CreatedAtAction(nameof(GetContract), new { id = contractId }, contractId),
            Problem);
    }
}

public class CreateContractFromOfferRequest
{
    public Guid OfferId { get; set; }
    public string ContractNumber { get; set; } = default!;
    public DateTime? EffectiveAtUtc { get; set; }
    public List<CreateContractFromOfferBenefitRequest>? Benefits { get; set; }
}
