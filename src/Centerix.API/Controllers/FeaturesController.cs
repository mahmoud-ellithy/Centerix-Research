using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform;
using Centerix.Infrastructure.Auth;
using Microsoft.AspNetCore.Mvc;

namespace Centerix.API.Controllers;

[Route("api/[controller]")]
public class FeaturesController(ILocalizer localizer, IPlatformService platformService) : ApiController(localizer)
{
    private readonly IPlatformService _platformService = platformService;

    [HttpGet]
    [HasPermission(Permissions.Features.Read)]
    public async Task<IActionResult> GetFeatures(CancellationToken cancellationToken)
    {
        var result = await _platformService.GetFeaturesAsync(cancellationToken);

        return result.Match(
            features => Ok(features),
            Problem);
    }

    [HttpGet("{id}")]
    [HasPermission(Permissions.Features.Read)]
    public async Task<IActionResult> GetFeature(int id, CancellationToken cancellationToken)
    {
        var result = await _platformService.GetFeatureByIdAsync(id, cancellationToken);

        return result.Match(
            feature => Ok(feature),
            Problem);
    }

    [HttpPost]
    [HasPermission(Permissions.Features.Create)]
    public async Task<IActionResult> CreateFeature(FeatureDto feature, CancellationToken cancellationToken)
    {
        var result = await _platformService.CreateFeatureAsync(feature, cancellationToken);

        return result.Match(
            _ => StatusCode(StatusCodes.Status201Created),
            Problem);
    }

    [HttpPut("{id}")]
    [HasPermission(Permissions.Features.Update)]
    public async Task<IActionResult> UpdateFeature(int id, FeatureDto feature, CancellationToken cancellationToken)
    {
        var result = await _platformService.UpdateFeatureAsync(id, feature, cancellationToken);

        return result.Match(
            _ => NoContent(),
            Problem);
    }

    [HttpDelete("{id}")]
    [HasPermission(Permissions.Features.Delete)]
    public async Task<IActionResult> DeleteFeature(int id, CancellationToken cancellationToken)
    {
        var result = await _platformService.DeleteFeatureAsync(id, cancellationToken);

        return result.Match(
            _ => NoContent(),
            Problem);
    }
}
