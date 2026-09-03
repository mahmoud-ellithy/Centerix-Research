using Centerix.Application.Common.Interfaces;
using Centerix.Application.Teachers.Subjects.Commands;
using Centerix.Application.Teachers.Subjects.Queries;
using Centerix.Infrastructure.Auth;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Centerix.API.Controllers;

[Route("api/[controller]")]
public class SubjectsController(ILocalizer localizer, IMediator mediator) : ApiController(localizer)
{
    [HttpGet]
    [HasPermission(Permissions.Subjects.Read)]
    public async Task<IActionResult> GetSubjects([FromQuery] int? stageId, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetSubjectsQuery(stageId), cancellationToken);

        return result.Match(
            subjects => Ok(subjects),
            Problem);
    }

    [HttpGet("{id}")]
    [HasPermission(Permissions.Subjects.Read)]
    public async Task<IActionResult> GetSubject(int id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetSubjectByIdQuery(id), cancellationToken);

        return result.Match(
            subject => Ok(subject),
            Problem);
    }

    [HttpPost]
    [HasPermission(Permissions.Subjects.Create)]
    [RequireFeature(FeatureCodes.TeacherManagement)]
    public async Task<IActionResult> CreateSubject(CreateSubjectCommand command, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(command, cancellationToken);

        return result.Match(
            _ => StatusCode(StatusCodes.Status201Created),
            Problem);
    }

    [HttpPut("{id}")]
    [HasPermission(Permissions.Subjects.Update)]
    [RequireFeature(FeatureCodes.TeacherManagement)]
    public async Task<IActionResult> UpdateSubject(int id, UpdateSubjectCommand command, CancellationToken cancellationToken)
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
    [HasPermission(Permissions.Subjects.Delete)]
    [RequireFeature(FeatureCodes.TeacherManagement)]
    public async Task<IActionResult> DeleteSubject(int id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new DeleteSubjectCommand(id), cancellationToken);

        return result.Match(
            _ => NoContent(),
            Problem);
    }
}