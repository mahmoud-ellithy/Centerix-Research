using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Billing.Installments;
using Centerix.Application.Platform.Billing.Installments.Commands;
using Centerix.Application.Platform.Billing.Installments.Queries;
using Centerix.Infrastructure.Auth;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Centerix.API.Controllers;

[Route("api/[controller]")]
public class InstallmentsController(ILocalizer localizer, IMediator mediator) : ApiController(localizer)
{
    [HttpGet("contract/{contractId:guid}")]
    [HasPermission(Permissions.Installments.Read)]
    public async Task<IActionResult> GetInstallmentSchedule(Guid contractId, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetInstallmentScheduleQuery(contractId), cancellationToken);
        return result.Match(
            installments => Ok(installments),
            Problem);
    }

    [HttpGet("{id:guid}")]
    [HasPermission(Permissions.Installments.Read)]
    public async Task<IActionResult> GetInstallment(Guid id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetInstallmentQuery(id), cancellationToken);
        return result.Match(
            installment => Ok(installment),
            Problem);
    }

    [HttpPost("schedule")]
    [HasPermission(Permissions.Installments.Create)]
    public async Task<IActionResult> CreateInstallmentSchedule(
        CreateInstallmentScheduleCommand command,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(command, cancellationToken);
        return result.Match(
            ids => StatusCode(StatusCodes.Status201Created, ids),
            Problem);
    }

    [HttpPost]
    [HasPermission(Permissions.Installments.Create)]
    public async Task<IActionResult> AddInstallment(
        AddInstallmentCommand command,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(command, cancellationToken);
        return result.Match(
            id => StatusCode(StatusCodes.Status201Created, new { id }),
            Problem);
    }

    [HttpPut("{id:guid}")]
    [HasPermission(Permissions.Installments.Update)]
    public async Task<IActionResult> UpdateInstallment(
        Guid id,
        UpdateInstallmentCommand command,
        CancellationToken cancellationToken)
    {
        if (id != command.Id)
            return BadRequest(new { detail = "Route id does not match command id." });

        var result = await mediator.Send(command, cancellationToken);
        return result.Match(
            _ => NoContent(),
            Problem);
    }

    [HttpPost("{id:guid}/cancel")]
    [HasPermission(Permissions.Installments.Cancel)]
    public async Task<IActionResult> CancelInstallment(Guid id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new CancelInstallmentCommand(id), cancellationToken);
        return result.Match(
            _ => NoContent(),
            Problem);
    }
}
