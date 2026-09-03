using Centerix.Application.Common.Interfaces;
using Centerix.Application.Teachers.SalaryPayments.Commands;
using Centerix.Application.Teachers.SalaryPayments.Queries;
using Centerix.Infrastructure.Auth;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Centerix.API.Controllers;

[Route("api/[controller]")]
public class SalaryPaymentsController(ILocalizer localizer, IMediator mediator) : ApiController(localizer)
{
    [HttpGet]
    [HasPermission(Permissions.SalaryPayments.Read)]
    public async Task<IActionResult> GetPayments([FromQuery] Guid? teacherId, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetSalaryPaymentsQuery(teacherId), cancellationToken);

        return result.Match(
            items => Ok(items),
            Problem);
    }

    [HttpGet("{id}")]
    [HasPermission(Permissions.SalaryPayments.Read)]
    public async Task<IActionResult> GetPayment(Guid id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetSalaryPaymentByIdQuery(id), cancellationToken);

        return result.Match(
            item => Ok(item),
            Problem);
    }

    [HttpPost]
    [HasPermission(Permissions.SalaryPayments.Create)]
    [RequireFeature(FeatureCodes.TeacherManagement)]
    public async Task<IActionResult> CreatePayment(CreateSalaryPaymentCommand command, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(command, cancellationToken);

        return result.Match(
            _ => StatusCode(StatusCodes.Status201Created),
            Problem);
    }

    [HttpPost("{id}/mark-paid")]
    [HasPermission(Permissions.SalaryPayments.Update)]
    [RequireFeature(FeatureCodes.TeacherManagement)]
    public async Task<IActionResult> MarkPaid(Guid id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new MarkSalaryPaymentPaidCommand(id), cancellationToken);

        return result.Match(
            _ => NoContent(),
            Problem);
    }

    [HttpPost("{id}/cancel")]
    [HasPermission(Permissions.SalaryPayments.Update)]
    [RequireFeature(FeatureCodes.TeacherManagement)]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new CancelSalaryPaymentCommand(id), cancellationToken);

        return result.Match(
            _ => NoContent(),
            Problem);
    }
}