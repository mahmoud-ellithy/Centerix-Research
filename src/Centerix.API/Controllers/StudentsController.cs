using Centerix.Application.Common.Interfaces;
using Centerix.Application.Students.Students.Commands;
using Centerix.Application.Students.Students.Queries;
using Centerix.Infrastructure.Auth;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Centerix.API.Controllers;

[Route("api/[controller]")]
public class StudentsController(ILocalizer localizer, IMediator mediator) : ApiController(localizer)
{
    [HttpGet]
    [HasPermission(Permissions.Students.Read)]
    public async Task<IActionResult> GetStudents(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetStudentsQuery(), cancellationToken);

        return result.Match(
            students => Ok(students),
            Problem);
    }

    [HttpGet("{id}")]
    [HasPermission(Permissions.Students.Read)]
    public async Task<IActionResult> GetStudent(Guid id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetStudentByIdQuery(id), cancellationToken);

        return result.Match(
            student => Ok(student),
            Problem);
    }

    [HttpPost]
    [HasPermission(Permissions.Students.Create)]
    public async Task<IActionResult> CreateStudent(CreateStudentCommand command, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(command, cancellationToken);

        return result.Match(
            _ => StatusCode(StatusCodes.Status201Created),
            Problem);
    }

    [HttpPut("{id}")]
    [HasPermission(Permissions.Students.Update)]
    public async Task<IActionResult> UpdateStudent(Guid id, UpdateStudentCommand command, CancellationToken cancellationToken)
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
    [HasPermission(Permissions.Students.Delete)]
    public async Task<IActionResult> DeleteStudent(Guid id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new DeleteStudentCommand(id), cancellationToken);

        return result.Match(
            _ => NoContent(),
            Problem);
    }
}
