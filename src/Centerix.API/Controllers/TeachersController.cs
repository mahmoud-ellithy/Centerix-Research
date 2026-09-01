using Centerix.Application.Common.Interfaces;
using Centerix.Application.Teachers.Teachers.Commands;
using Centerix.Application.Teachers.Teachers.Queries;
using Centerix.Infrastructure.Auth;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Centerix.API.Controllers;

[Route("api/[controller]")]
public class TeachersController(ILocalizer localizer, IMediator mediator) : ApiController(localizer)
{
    [HttpGet]
    [HasPermission(Permissions.Teachers.Read)]
    public async Task<IActionResult> GetTeachers([FromQuery] Guid? branchId, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetTeachersQuery(branchId), cancellationToken);

        return result.Match(
            teachers => Ok(teachers),
            Problem);
    }

    [HttpGet("{id}")]
    [HasPermission(Permissions.Teachers.Read)]
    public async Task<IActionResult> GetTeacher(Guid id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetTeacherByIdQuery(id), cancellationToken);

        return result.Match(
            teacher => Ok(teacher),
            Problem);
    }

    [HttpPost]
    [HasPermission(Permissions.Teachers.Create)]
    [RequireFeature(FeatureCodes.TeacherManagement)]
    public async Task<IActionResult> CreateTeacher(CreateTeacherCommand command, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(command, cancellationToken);

        return result.Match(
            _ => StatusCode(StatusCodes.Status201Created),
            Problem);
    }

    [HttpPut("{id}")]
    [HasPermission(Permissions.Teachers.Update)]
    public async Task<IActionResult> UpdateTeacher(Guid id, UpdateTeacherCommand command, CancellationToken cancellationToken)
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

    [HttpDelete("{id}")]
    [HasPermission(Permissions.Teachers.Delete)]
    public async Task<IActionResult> DeleteTeacher(Guid id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new DeleteTeacherCommand(id), cancellationToken);

        return result.Match(
            _ => NoContent(),
            Problem);
    }
}