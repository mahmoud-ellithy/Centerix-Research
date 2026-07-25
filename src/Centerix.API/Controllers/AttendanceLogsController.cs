using Centerix.Application.Common.Interfaces;
using Centerix.Application.Students.Attendance.Commands;
using Centerix.Application.Students.Attendance.Queries;
using Centerix.Infrastructure.Auth;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Centerix.API.Controllers;

[Route("api/[controller]")]
public class AttendanceLogsController(ILocalizer localizer, IMediator mediator) : ApiController(localizer)
{
    [HttpGet]
    [HasPermission(Permissions.AttendanceLogs.Read)]
    public async Task<IActionResult> GetAttendanceLogs(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetAttendanceLogsQuery(), cancellationToken);

        return result.Match(
            logs => Ok(logs),
            Problem);
    }

    [HttpGet("{id}")]
    [HasPermission(Permissions.AttendanceLogs.Read)]
    public async Task<IActionResult> GetAttendanceLogById(long id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetAttendanceLogByIdQuery(id), cancellationToken);

        return result.Match(
            log => Ok(log),
            Problem);
    }

    [HttpPost]
    [HasPermission(Permissions.AttendanceLogs.Create)]
    public async Task<IActionResult> CreateAttendanceLog(CreateAttendanceLogCommand command, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(command, cancellationToken);

        return result.Match(
            _ => StatusCode(StatusCodes.Status201Created),
            Problem);
    }
}
