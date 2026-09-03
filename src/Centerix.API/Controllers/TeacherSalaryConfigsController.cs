using Centerix.Application.Common.Interfaces;
using Centerix.Application.Teachers.TeacherSalaryConfigs.Commands;
using Centerix.Application.Teachers.TeacherSalaryConfigs.Queries;
using Centerix.Infrastructure.Auth;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Centerix.API.Controllers;

[Route("api/[controller]")]
public class TeacherSalaryConfigsController(ILocalizer localizer, IMediator mediator) : ApiController(localizer)
{
    [HttpGet]
    [HasPermission(Permissions.TeacherSalaryConfigs.Read)]
    public async Task<IActionResult> GetConfigs([FromQuery] Guid? teacherId, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetTeacherSalaryConfigsQuery(teacherId), cancellationToken);

        return result.Match(
            items => Ok(items),
            Problem);
    }

    [HttpGet("{id}")]
    [HasPermission(Permissions.TeacherSalaryConfigs.Read)]
    public async Task<IActionResult> GetConfig(int id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetTeacherSalaryConfigByIdQuery(id), cancellationToken);

        return result.Match(
            item => Ok(item),
            Problem);
    }

    [HttpPost]
    [HasPermission(Permissions.TeacherSalaryConfigs.Create)]
    [RequireFeature(FeatureCodes.TeacherManagement)]
    public async Task<IActionResult> CreateConfig(CreateTeacherSalaryConfigCommand command, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(command, cancellationToken);

        return result.Match(
            _ => StatusCode(StatusCodes.Status201Created),
            Problem);
    }

    [HttpPut("{id}")]
    [HasPermission(Permissions.TeacherSalaryConfigs.Update)]
    [RequireFeature(FeatureCodes.TeacherManagement)]
    public async Task<IActionResult> UpdateConfig(int id, UpdateTeacherSalaryConfigCommand command, CancellationToken cancellationToken)
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
    [HasPermission(Permissions.TeacherSalaryConfigs.Delete)]
    [RequireFeature(FeatureCodes.TeacherManagement)]
    public async Task<IActionResult> DeleteConfig(int id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new DeleteTeacherSalaryConfigCommand(id), cancellationToken);

        return result.Match(
            _ => NoContent(),
            Problem);
    }
}