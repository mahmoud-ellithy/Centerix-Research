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
    // FIN-001 two-key authorization, enforced by the framework itself (not only by the handler):
    //   1. TenantCredits.Create  (Tenant scope)   — WHICH TENANT this request may act in. Satisfied
    //      only by an active TenantMembership in the resolved tenant holding the permission.
    //   2. PlatformCredits.Mint  (Platform scope) — whether the caller may mint balance at all.
    //      Satisfied only by the DB-verified IPlatformAdminVerifier; a tenant-derived grant can
    //      NEVER satisfy it.
    // ASP.NET Core combines every IAuthorizeData on the endpoint into ONE policy whose requirements
    // must ALL succeed, so a plain TenantAdmin is rejected at the authorization layer even if the
    // handler's IPlatformAdminGuard were removed.
    [HasPermission(Permissions.TenantCredits.Create)]
    [HasPermission(Permissions.PlatformCredits.Mint)]
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
