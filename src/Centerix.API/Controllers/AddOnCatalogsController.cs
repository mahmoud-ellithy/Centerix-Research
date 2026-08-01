using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Subscriptions.Commands;
using Centerix.Application.Platform.Subscriptions.Queries;
using Centerix.Infrastructure.Auth;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Centerix.API.Controllers;

[Route("api/[controller]")]
public class AddOnCatalogsController(ILocalizer localizer, IMediator mediator) : ApiController(localizer)
{
    [HttpGet]
    [HasPermission(Permissions.AddOnCatalogs.Read)]
    public async Task<IActionResult> GetAddOnCatalogs(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetAddOnCatalogsQuery(), cancellationToken);

        return result.Match(
            addOnCatalogs => Ok(addOnCatalogs),
            Problem);
    }

    [HttpGet("{id}")]
    [HasPermission(Permissions.AddOnCatalogs.Read)]
    public async Task<IActionResult> GetAddOnCatalog(int id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetAddOnCatalogByIdQuery(id), cancellationToken);

        return result.Match(
            addOnCatalog => Ok(addOnCatalog),
            Problem);
    }

    [HttpPost]
    [HasPermission(Permissions.AddOnCatalogs.Create)]
    public async Task<IActionResult> CreateAddOnCatalog(CreateAddOnCatalogCommand command, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(command, cancellationToken);

        return result.Match(
            _ => StatusCode(StatusCodes.Status201Created),
            Problem);
    }

    [HttpPost("{id}/deactivate")]
    [HasPermission(Permissions.AddOnCatalogs.Update)]
    public async Task<IActionResult> DeactivateAddOnCatalog(int id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new DeactivateAddOnCatalogCommand(id), cancellationToken);

        return result.Match(
            _ => NoContent(),
            Problem);
    }

    [HttpPost("{id}/activate")]
    [HasPermission(Permissions.AddOnCatalogs.Update)]
    public async Task<IActionResult> ActivateAddOnCatalog(int id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new ActivateAddOnCatalogCommand(id), cancellationToken);

        return result.Match(
            _ => NoContent(),
            Problem);
    }
}
