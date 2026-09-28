using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Promotions;
using Centerix.Application.Platform.Promotions.Commands;
using Centerix.Domain.Platform.Promotions.Enums;
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
    /// Benefits are sourced from trusted commercial configuration, not from the client.
    ///
    /// <see cref="PaymentTerms"/> is supplied explicitly by the platform operator
    /// at calculation time and is NEVER inferred from <c>PromotionType</c>,
    /// <c>Plan.BonusMonths</c>, installment rows, or any other indirect field.
    /// </summary>
    [HttpPost("calculate")]
    [HasPermission(Permissions.Offers.Calculate)]
    public async Task<IActionResult> CalculateOffer([FromBody] CalculateAndPersistOfferRequest request, CancellationToken cancellationToken)
    {
        // PaymentTerms is an explicit commercial decision. The request must supply it.
        // We reject missing/undefined values rather than silently defaulting to either arm.
        if (request.PaymentTerms is null || !Enum.IsDefined(typeof(PaymentTerms), request.PaymentTerms.Value))
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "PaymentTerms required",
                detail: "PaymentTerms is an explicit commercial decision and must be supplied " +
                        "as either FullUpfront (0) or Installments (1).");
        }

        var command = new CalculateAndPersistOfferCommand(
            request.PlanId,
            request.DurationMonths,
            request.PaymentTerms.Value,
            request.EvaluationTimeUtc);

        var result = await mediator.Send(command, cancellationToken);

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

    /// <summary>
    /// Explicit commercial payment mode. Required. The platform operator must
    /// supply either <see cref="PaymentTerms.FullUpfront"/> (numeric 0) or
    /// <see cref="PaymentTerms.Installments"/> (numeric 1) in the request body.
    /// The handler validates that the supplied value is a defined enum member;
    /// the absence of the field is rejected (treated as "explicit decision missing").
    /// </summary>
    public PaymentTerms? PaymentTerms { get; set; }

    public DateTime? EvaluationTimeUtc { get; set; }
}
