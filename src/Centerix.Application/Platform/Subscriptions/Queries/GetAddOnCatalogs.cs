namespace Centerix.Application.Platform.Subscriptions.Queries;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;

using Microsoft.EntityFrameworkCore;

using Mapster;

using MediatR;

public record GetAddOnCatalogsQuery : IRequest<Result<IEnumerable<AddOnCatalogDto>>>;

public class GetAddOnCatalogsHandler(IAppDbContext dbContext) : IRequestHandler<GetAddOnCatalogsQuery, Result<IEnumerable<AddOnCatalogDto>>>
{
    public async Task<Result<IEnumerable<AddOnCatalogDto>>> Handle(
        GetAddOnCatalogsQuery request,
        CancellationToken cancellationToken)
    {
        var addOnCatalogs = await dbContext.AddOnCatalogs
            .ProjectToType<AddOnCatalogDto>()
            .ToListAsync(cancellationToken);

        return addOnCatalogs;
    }
}
