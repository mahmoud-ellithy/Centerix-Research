using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Billing;
using Centerix.Application.Platform.Billing.Commands;
using Centerix.Application.Platform.Billing.Queries;
using Centerix.Infrastructure.Auth;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Centerix.API.Controllers;

[Route("api/[controller]")]
public class TenantCreditsController(ILocalizer localizer, IMediator mediator) : ApiController(localizer)
{
    [HttpGet]
    [HasPermission(Permissions.TenantCredits.Read)]
    public async Task<IActionResult> GetTenantCredits(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetTenantCreditsQuery(), cancellationToken);

        return result.Match(
            credits => Ok(credits),
            Problem);
    }

    [HttpPost]
    [HasPermission(Permissions.TenantCredits.Create)]
    public async Task<IActionResult> CreateTenantCredit(CreateTenantCreditCommand command, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(command, cancellationToken);

        return result.Match(
            _ => StatusCode(StatusCodes.Status201Created),
            Problem);
    }

    [HttpPost("{id:guid}/apply")]
    [HasPermission(Permissions.TenantCredits.Apply)]
    public async Task<IActionResult> ApplyCreditToInvoice(
        Guid id,
        [FromBody] ApplyCreditToInvoiceRequest request,
        CancellationToken cancellationToken)
    {
        var idempotencyKey = request.IdempotencyKey ?? Guid.NewGuid().ToString("N");
        var command = new ApplyCreditToInvoiceCommand(id, request.InvoiceId, request.Amount, idempotencyKey);
        var result = await mediator.Send(command, cancellationToken);

        return result.Match(
            _ => Ok(new { message = "Credit applied successfully" }),
            Problem);
    }

    [HttpGet("{id:guid}/balance")]
    [HasPermission(Permissions.TenantCredits.Read)]
    public async Task<IActionResult> GetCreditBalance(Guid id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetCreditBalanceQuery(id), cancellationToken);

        return result.Match(
            balance => Ok(balance),
            Problem);
    }
}

public record ApplyCreditToInvoiceRequest(Guid InvoiceId, decimal Amount, string? IdempotencyKey = null);
