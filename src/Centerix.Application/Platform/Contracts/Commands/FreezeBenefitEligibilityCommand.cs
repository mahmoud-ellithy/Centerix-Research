using Centerix.Application.Platform.Contracts.Services;
using Centerix.Domain.Common.Results;
using MediatR;

namespace Centerix.Application.Platform.Contracts.Commands;

/// <summary>
/// Command to freeze the eligibility snapshot for a single <c>ContractBenefit</c> by
/// evaluating the closed rule algebra against current tenant facts.
/// </summary>
/// <remarks>
/// This command is a thin MediatR wrapper around <see cref="IFreezeEligibilityService"/>.
/// The command delegates all decisions to the service so the same logic is reusable from
/// non-MediatR callers (e.g. controllers, hosted services).
/// </remarks>
public record FreezeBenefitEligibilityCommand(Guid BenefitId)
    : IRequest<Result<FreezeEligibilityResponse>>;

public class FreezeBenefitEligibilityHandler(
    IFreezeEligibilityService freezeService)
    : IRequestHandler<FreezeBenefitEligibilityCommand, Result<FreezeEligibilityResponse>>
{
    public Task<Result<FreezeEligibilityResponse>> Handle(
        FreezeBenefitEligibilityCommand request,
        CancellationToken cancellationToken)
        => freezeService.FreezeBenefitAsync(request.BenefitId, cancellationToken);
}