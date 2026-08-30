using Centerix.Application.Common.Interfaces;
using Centerix.Application.Students.Commands;
using Centerix.Application.Students.Queries;
using Centerix.Infrastructure.Auth;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Centerix.API.Controllers;

[Route("api/[controller]")]
public class AcademicYearsController(ILocalizer localizer, IMediator mediator) : ApiController(localizer)
{
    [HttpGet]
    [HasPermission(Permissions.AcademicYears.Read)]
    public async Task<IActionResult> GetAcademicYears(CancellationToken cancellationToken)
    {
        var years = await mediator.Send(new GetAcademicYearsQuery(), cancellationToken);

        return Ok(years);
    }

    [HttpGet("{id}")]
    [HasPermission(Permissions.AcademicYears.Read)]
    public async Task<IActionResult> GetAcademicYear(int id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetAcademicYearByIdQuery(id), cancellationToken);

        return result.Match(
            year => Ok(year),
            Problem);
    }

    [HttpPost]
    [HasPermission(Permissions.AcademicYears.Create)]
    public async Task<IActionResult> CreateAcademicYear(CreateAcademicYearCommand command, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(command, cancellationToken);

        return result.Match(
            _ => StatusCode(StatusCodes.Status201Created),
            Problem);
    }

    [HttpPut("{id}")]
    [HasPermission(Permissions.AcademicYears.Update)]
    public async Task<IActionResult> UpdateAcademicYear(int id, UpdateAcademicYearCommand command, CancellationToken cancellationToken)
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
