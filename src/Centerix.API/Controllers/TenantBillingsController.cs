using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform;
using Centerix.Infrastructure.Auth;
using Microsoft.AspNetCore.Mvc;

namespace Centerix.API.Controllers;

[Route("api/[controller]")]
public class TenantBillingsController(ILocalizer localizer, IPlatformService platformService) : ApiController(localizer)
{
    private readonly IPlatformService _platformService = platformService;

    [HttpGet]
    [HasPermission(Permissions.TenantBillings.Read)]
    public async Task<IActionResult> GetTenantBillings(CancellationToken cancellationToken)
    {
        var result = await _platformService.GetTenantBillingsAsync(cancellationToken);
        return result.Match(
            billings => Ok(billings),
            Problem);
    }

    [HttpPost]
    [HasPermission(Permissions.TenantBillings.Create)]
    public async Task<IActionResult> CreateTenantBilling(TenantBillingDto billing, CancellationToken cancellationToken)
    {
        var result = await _platformService.CreateTenantBillingAsync(billing, cancellationToken);

        return result.Match(
            _ => StatusCode(StatusCodes.Status201Created),
            Problem);
    }
}
