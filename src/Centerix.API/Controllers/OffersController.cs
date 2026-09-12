using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Promotions;
using Centerix.Application.Platform.Promotions.Commands;
using Centerix.Infrastructure.Auth;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Centerix.API.Controllers;

[Route("api/[controller]")]
public class OffersController(ILocalizer localizer, IMediator mediator) : ApiController(localizer)
{
    /// <summary>
    /// Calculates and persists a commercial offer for a plan and duration.
    /// Returns the persisted Offer snapshot with all commercial terms.
    /// </summary>
    [HttpPost("calculate")]
    [HasPermission(Permissions.Offers.Calculate)]
    public async Task<IActionResult> CalculateOffer([FromBody] CalculateAndPersistOfferRequest request, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(
            new CalculateAndPersistOfferCommand(request.PlanId, request.DurationMonths, request.EvaluationTimeUtc),
            cancellationToken);

        return result.Match(
            offer => StatusCode(StatusCodes.Status201Created, offer),
            Problem);
    }

    /// <summary>
    /// Accepts a calculated offer. The customer confirms the commercial terms.
    /// The offer must not be expired or already accepted.
    /// </summary>
    [HttpPost("{id}/accept")]
    [HasPermission(Permissions.Offers.Accept)]
    public async Task<IActionResult> AcceptOffer(Guid id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new AcceptOfferCommand(id), cancellationToken);

        return result.Match(
            offer => Ok(offer),
            Problem);
    }

    /// <summary>
    /// Gets an offer by ID.
    /// </summary>
    [HttpGet("{id}")]
    [HasPermission(Permissions.Offers.Read)]
    public async Task<IActionResult> GetOffer(Guid id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetOfferByIdQuery(id), cancellationToken);

        return result.Match(
            offer => Ok(offer),
            Problem);
    }

    /// <summary>
    /// Lists all offers for the current tenant.
    /// </summary>
    [HttpGet]
    [HasPermission(Permissions.Offers.Read)]
    public async Task<IActionResult> GetOffers(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new ListOffersQuery(), cancellationToken);

        return result.Match(
            offers => Ok(offers),
            Problem);
    }
}

public class CalculateAndPersistOfferRequest
{
    public int PlanId { get; set; }
    public int DurationMonths { get; set; }
    public DateTime? EvaluationTimeUtc { get; set; }
}
