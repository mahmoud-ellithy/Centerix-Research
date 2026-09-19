using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Billing.Commands;
using Centerix.Infrastructure.Auth;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Centerix.API.Controllers;

[Route("api/[controller]")]
public class RefundsController(ILocalizer localizer, IMediator mediator) : ApiController(localizer)
{
    [HttpPost]
    [HasPermission(Permissions.Refunds.Create)]
    public async Task<IActionResult> CreateRefund(
        [FromBody] CreateRefundRequest request,
        CancellationToken cancellationToken)
    {
        var command = new CreateRefundCommand(
            request.RefundNumber,
            request.ContractId,
            request.SubscriptionId,
            request.InvoiceId,
            request.Reason);

        var result = await mediator.Send(command, cancellationToken);

        return result.Match(
            id => StatusCode(StatusCodes.Status201Created, new { id }),
            Problem);
    }

    [HttpPost("{id:guid}/approve")]
    [HasPermission(Permissions.Refunds.Approve)]
    public async Task<IActionResult> ApproveRefund(Guid id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new ApproveRefundCommand(id), cancellationToken);

        return result.Match(
            _ => Ok(new { message = "Refund approved" }),
            Problem);
    }

    [HttpPost("{id:guid}/execute")]
    [HasPermission(Permissions.Refunds.Execute)]
    public async Task<IActionResult> ExecuteRefund(
        Guid id,
        [FromBody] ExecuteRefundRequest? request,
        CancellationToken cancellationToken)
    {
        var idempotencyKey = request?.IdempotencyKey;
        var command = new ExecuteRefundCommand(id, idempotencyKey);
        var result = await mediator.Send(command, cancellationToken);

        return result.Match(
            _ => Ok(new { message = "Refund executed successfully" }),
            Problem);
    }

    [HttpPost("calculate")]
    [HasPermission(Permissions.Refunds.Create)]
    public async Task<IActionResult> CalculateRefund(
        [FromBody] CalculateRefundRequest request,
        CancellationToken cancellationToken)
    {
        var query = new CalculateRefundQuery(request.ContractId, DateTime.UtcNow);
        var result = await mediator.Send(query, cancellationToken);

        return result.Match(
            calculation => Ok(calculation),
            Problem);
    }
}

public record CreateRefundRequest(
    string RefundNumber,
    Guid ContractId,
    Guid? SubscriptionId = null,
    Guid? InvoiceId = null,
    string Reason = "");

public record ExecuteRefundRequest(string? IdempotencyKey = null);

public record CalculateRefundRequest(Guid ContractId);
