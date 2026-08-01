using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Billing.Commands;
using Centerix.Application.Platform.Billing.Queries;
using Centerix.Infrastructure.Auth;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Centerix.API.Controllers;

[Route("api/[controller]")]
public class InvoicesController(ILocalizer localizer, IMediator mediator) : ApiController(localizer)
{
    [HttpGet]
    [HasPermission(Permissions.Invoices.Read)]
    public async Task<IActionResult> GetInvoices(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetInvoicesQuery(), cancellationToken);

        return result.Match(
            invoices => Ok(invoices),
            Problem);
    }

    [HttpGet("{id:guid}")]
    [HasPermission(Permissions.Invoices.Read)]
    public async Task<IActionResult> GetInvoice(Guid id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetInvoiceByIdQuery(id), cancellationToken);

        return result.Match(
            invoice => Ok(invoice),
            Problem);
    }

    [HttpPost]
    [HasPermission(Permissions.Invoices.Create)]
    public async Task<IActionResult> CreateInvoice(CreateInvoiceCommand command, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(command, cancellationToken);

        return result.Match(
            _ => StatusCode(StatusCodes.Status201Created),
            Problem);
    }

    [HttpPost("{id:guid}/issue")]
    [HasPermission(Permissions.Invoices.Update)]
    public async Task<IActionResult> IssueInvoice(Guid id, IssueInvoiceCommand command, CancellationToken cancellationToken)
    {
        if (id != command.Id)
        {
            return BadRequest(new { detail = "Route id does not match command id." });
        }

        var result = await mediator.Send(command, cancellationToken);

        return result.Match(
            _ => NoContent(),
            Problem);
    }

    [HttpPost("{id:guid}/lines")]
    [HasPermission(Permissions.Invoices.Update)]
    public async Task<IActionResult> AddInvoiceLine(Guid id, AddInvoiceLineCommand command, CancellationToken cancellationToken)
    {
        if (id != command.InvoiceId)
        {
            return BadRequest(new { detail = "Route id does not match command invoice id." });
        }

        var result = await mediator.Send(command, cancellationToken);

        return result.Match(
            _ => StatusCode(StatusCodes.Status201Created),
            Problem);
    }

    [HttpGet("{id:guid}/lines")]
    [HasPermission(Permissions.Invoices.Read)]
    public async Task<IActionResult> GetInvoiceLines(Guid id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetInvoiceLinesQuery(id), cancellationToken);

        return result.Match(
            lines => Ok(lines),
            Problem);
    }

    [HttpDelete("{id:guid}/lines/{lineId:guid}")]
    [HasPermission(Permissions.Invoices.Update)]
    public async Task<IActionResult> RemoveInvoiceLine(Guid id, Guid lineId, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new RemoveInvoiceLineCommand(id, lineId), cancellationToken);

        return result.Match(
            _ => NoContent(),
            Problem);
    }

    [HttpPost("{id:guid}/pay")]
    [HasPermission(Permissions.Invoices.Update)]
    public async Task<IActionResult> MarkInvoicePaid(Guid id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new MarkInvoicePaidCommand(id), cancellationToken);

        return result.Match(
            _ => NoContent(),
            Problem);
    }

    [HttpPost("{id:guid}/cancel")]
    [HasPermission(Permissions.Invoices.Update)]
    public async Task<IActionResult> CancelInvoice(Guid id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new CancelInvoiceCommand(id), cancellationToken);

        return result.Match(
            _ => NoContent(),
            Problem);
    }

    [HttpDelete("{id:guid}")]
    [HasPermission(Permissions.Invoices.Delete)]
    public async Task<IActionResult> DeleteInvoice(Guid id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new CancelInvoiceCommand(id), cancellationToken);

        return result.Match(
            _ => NoContent(),
            Problem);
    }
}
