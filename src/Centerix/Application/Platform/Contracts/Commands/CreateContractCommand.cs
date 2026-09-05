namespace Centerix.Application.Platform.Contracts.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Contracts;

using MediatR;

/// <summary>
/// Request to create a new Contract with commercial snapshot.
/// </summary>
public record CreateContractCommand(
    string ContractNumber,
    int PlanId,
    DateTime EffectiveAtUtc,
    DateTime EndsAtUtc,
    int DurationMonths,
    decimal MonthlyListPrice,
    string CurrencyCode,
    decimal ContractedAmount,
    decimal DiscountAmount = 0,
    string? PromotionReference = null) : IRequest<Result<Guid>>;

/// <summary>
/// Pricing tier data for contract creation.
/// </summary>
public record CreatePricingTierRequest(
    int DurationMonths,
    decimal TierPrice,
    string CurrencyCode,
    decimal MonthlyListPrice,
    int DisplayOrder = 0);

/// <summary>
/// Benefit/gift data for contract creation.
/// </summary>
public record CreateBenefitRequest(
    ContractBenefitType BenefitType,
    string Name,
    string? Description,
    decimal ContractualValue,
    string CurrencyCode);

public class CreateContractHandler(
    IAppDbContext dbContext,
    IAuditWriter auditWriter) : IRequestHandler<CreateContractCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CreateContractCommand request, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();

        var result = Contract.Create(
            id,
            dbContext.TenantId!,
            request.ContractNumber,
            request.PlanId,
            request.EffectiveAtUtc,
            request.EndsAtUtc,
            request.DurationMonths,
            request.MonthlyListPrice,
            request.CurrencyCode,
            request.ContractedAmount,
            request.DiscountAmount,
            request.PromotionReference);

        if (!result.IsSuccess)
        {
            return result.Errors!;
        }

        var contract = result.Value;

        dbContext.Contracts.Add(contract);
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "Contract.Create",
            entityType: nameof(Contract),
            entityId: id.ToString(),
            newValue: AuditPayload.Serialize(new
            {
                contract.ContractNumber,
                contract.PlanId,
                contract.EffectiveAtUtc,
                contract.EndsAtUtc,
                contract.DurationMonths,
                contract.MonthlyListPrice,
                contract.CurrencyCode,
                contract.ContractedAmount,
                contract.Status
            }),
            cancellationToken: cancellationToken);

        return id;
    }
}
