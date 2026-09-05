using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Contracts.Commands;
using Centerix.Application.Platform.Contracts.Queries;
using Centerix.Infrastructure.Auth;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Centerix.API.Controllers;

[Route("api/[controller]")]
public class ContractsController(ILocalizer localizer, IMediator mediator) : ApiController(localizer)
{
    [HttpGet]
    [HasPermission(Permissions.Contracts.Read)]
    public async Task<IActionResult> GetContracts(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new ListContractsQuery(), cancellationToken);

        return result.Match(
            contracts => Ok(contracts),
            Problem);
    }

    [HttpGet("{id}")]
    [HasPermission(Permissions.Contracts.Read)]
    public async Task<IActionResult> GetContract(Guid id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetContractByIdQuery(id), cancellationToken);

        return result.Match(
            contract => Ok(contract),
            Problem);
    }

    [HttpPost]
    [HasPermission(Permissions.Contracts.Create)]
    public async Task<IActionResult> CreateContract(CreateContractCommand command, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(command, cancellationToken);

        return result.Match(
            contractId => CreatedAtAction(nameof(GetContract), new { id = contractId }, contractId),
            Problem);
    }
}
