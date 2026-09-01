using Centerix.Application.Common.Interfaces;
using Centerix.Application.Teachers.TeacherRatings.Commands;
using Centerix.Application.Teachers.TeacherRatings.Queries;
using Centerix.Infrastructure.Auth;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Centerix.API.Controllers;

[Route("api/[controller]")]
public class TeacherRatingsController(ILocalizer localizer, IMediator mediator) : ApiController(localizer)
{
    [HttpGet]
    [HasPermission(Permissions.TeacherRatings.Read)]
    public async Task<IActionResult> GetRatings([FromQuery] Guid? teacherId, [FromQuery] Guid? studentId, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetTeacherRatingsQuery(teacherId, studentId), cancellationToken);

        return result.Match(
            items => Ok(items),
            Problem);
    }

    [HttpPost]
    [HasPermission(Permissions.TeacherRatings.Create)]
    [RequireFeature(FeatureCodes.TeacherManagement)]
    public async Task<IActionResult> Create(CreateTeacherRatingCommand command, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(command, cancellationToken);

        return result.Match(
            _ => StatusCode(StatusCodes.Status201Created),
            Problem);
    }
}