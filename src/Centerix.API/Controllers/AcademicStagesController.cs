using Centerix.Application.Common.Interfaces;
using Centerix.Application.Students.Lookups.Commands;
using Centerix.Application.Students.Lookups.Queries;
using Centerix.Infrastructure.Auth;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Centerix.API.Controllers;

[Route("api/[controller]")]
public class AcademicStagesController(ILocalizer localizer, IMediator mediator) : ApiController(localizer)
{
    [HttpGet]
    [HasPermission(Permissions.AcademicStages.Read)]
    public async Task<IActionResult> GetAcademicStages(CancellationToken cancellationToken)
    {
        var stages = await mediator.Send(new GetAcademicStagesQuery(), cancellationToken);

        return Ok(stages);
    }

    [HttpGet("{id}")]
    [HasPermission(Permissions.AcademicStages.Read)]
    public async Task<IActionResult> GetAcademicStage(int id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetAcademicStageByIdQuery(id), cancellationToken);

        return result.Match(
            stage => Ok(stage),
            Problem);
    }

    [HttpPost]
    [HasPermission(Permissions.AcademicStages.Create)]
    public async Task<IActionResult> CreateAcademicStage(CreateAcademicStageCommand command, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(command, cancellationToken);

        return result.Match(
            _ => StatusCode(StatusCodes.Status201Created),
            Problem);
    }

    [HttpPut("{id}")]
    [HasPermission(Permissions.AcademicStages.Update)]
    public async Task<IActionResult> UpdateAcademicStage(int id, UpdateAcademicStageCommand command, CancellationToken cancellationToken)
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
}
