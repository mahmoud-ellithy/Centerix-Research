using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Promotions;
using Centerix.Application.Platform.Promotions.Commands;
using Centerix.Application.Platform.Promotions.Queries;
using Centerix.Infrastructure.Auth;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Centerix.API.Controllers;

[Route("api/[controller]")]
public class PromotionsController(ILocalizer localizer, IMediator mediator) : ApiController(localizer)
{
    [HttpGet]
    [HasPermission(Permissions.Promotions.View)]
    public async Task<IActionResult> GetPromotions(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new ListPromotionsQuery(), cancellationToken);

        return result.Match(
            promotions => Ok(promotions),
            Problem);
    }

    [HttpGet("{id}")]
    [HasPermission(Permissions.Promotions.View)]
    public async Task<IActionResult> GetPromotion(int id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetPromotionByIdQuery(id), cancellationToken);

        return result.Match(
            promotion => Ok(promotion),
            Problem);
    }

    [HttpPost]
    [HasPermission(Permissions.Promotions.Create)]
    public async Task<IActionResult> CreatePromotion(CreatePromotionCommand command, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(command, cancellationToken);

        return result.Match(
            id => StatusCode(StatusCodes.Status201Created, id),
            Problem);
    }

    [HttpPut("{id}")]
    [HasPermission(Permissions.Promotions.Update)]
    public async Task<IActionResult> UpdatePromotion(int id, UpdatePromotionCommand command, CancellationToken cancellationToken)
    {
        if (id != command.Id)
            return BadRequest(new { detail = "Route id does not match command id." });

        var result = await mediator.Send(command, cancellationToken);

        return result.Match(
            _ => NoContent(),
            Problem);
    }

    [HttpPost("{id}/activate")]
    [HasPermission(Permissions.Promotions.Activate)]
    public async Task<IActionResult> ActivatePromotion(int id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new ActivatePromotionCommand(id), cancellationToken);

        return result.Match(
            _ => NoContent(),
            Problem);
    }

    [HttpPost("{id}/disable")]
    [HasPermission(Permissions.Promotions.Activate)]
    public async Task<IActionResult> DisablePromotion(int id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new DisablePromotionCommand(id), cancellationToken);

        return result.Match(
            _ => NoContent(),
            Problem);
    }

    [HttpPost("calculate")]
    [HasPermission(Permissions.Promotions.Calculate)]
    public async Task<IActionResult> CalculateOffer([FromBody] CalculateOfferRequest request, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(
            new CalculateOfferQuery(request.PlanId, request.DurationMonths, request.EvaluationTimeUtc),
            cancellationToken);

        return result.Match(
            offer => Ok(offer),
            Problem);
    }
}

public class CalculateOfferRequest
{
    public int PlanId { get; set; }
    public int DurationMonths { get; set; }
    public DateTime? EvaluationTimeUtc { get; set; }
}
