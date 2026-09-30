namespace Centerix.Application.Platform.Contracts.Commands;

using Centerix.Domain.Common.Results;
using MediatR;

/// <summary>
/// Grants a FreeMonthsBenefit (Pending → Granted).
/// Idempotent: if the benefit is already Granted, returns success without mutation.
/// </summary>
/// <param name="BenefitId">The ID of the FreeMonthsBenefit to grant.</param>
public record GrantFreeMonthsBenefitCommand(Guid BenefitId) : IRequest<Result<GrantFreeMonthsBenefitResult>>;

/// <summary>Result of a successful grant operation.</summary>
public sealed record GrantFreeMonthsBenefitResult
{
    public Guid BenefitId { get; init; }
    public DateTime GrantedAtUtc { get; init; }
    public bool IsAlreadyGranted { get; init; }
}
