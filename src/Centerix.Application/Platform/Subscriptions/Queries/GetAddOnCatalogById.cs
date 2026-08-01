namespace Centerix.Application.Platform.Subscriptions.Queries;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;

using Microsoft.EntityFrameworkCore;

using MediatR;

using Mapster;

public record GetAddOnCatalogByIdQuery(int Id) : IRequest<Result<AddOnCatalogDto>>;

public class GetAddOnCatalogByIdHandler(IAppDbContext dbContext) : IRequestHandler<GetAddOnCatalogByIdQuery, Result<AddOnCatalogDto>>
{
    public async Task<Result<AddOnCatalogDto>> Handle(GetAddOnCatalogByIdQuery request, CancellationToken cancellationToken)
    {
        var addOnCatalog = await dbContext.AddOnCatalogs
            .Where(a => a.Id == request.Id)
            .ProjectToType<AddOnCatalogDto>()
            .FirstOrDefaultAsync(cancellationToken);

        if (addOnCatalog is null)
        {
            return Error.NotFound("AddOnCatalog.NotFound", $"Add-on catalog with id '{request.Id}' was not found.");
        }

        return addOnCatalog;
    }
}
