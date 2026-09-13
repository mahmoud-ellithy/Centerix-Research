namespace Centerix.Application.Platform.Contracts.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Contracts.Services;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Contracts;
using Centerix.Domain.Platform.Contracts.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Command to check and update a benefit's eligibility status.
/// Evaluates the benefit against contract status, payment obligations,
/// and installment compliance (no overdue required installments).
/// </summary>
public record CheckBenefitEligibilityCommand(
    Guid ContractId,
    Guid BenefitId) : IRequest<Result<BenefitEligibilityStatus>>;

public class CheckBenefitEligibilityHandler(
    IAppDbContext dbContext,
    IBenefitEligibilityService eligibilityService) : IRequestHandler<CheckBenefitEligibilityCommand, Result<BenefitEligibilityStatus>>
{
    public async Task<Result<BenefitEligibilityStatus>> Handle(
        CheckBenefitEligibilityCommand request,
        CancellationToken cancellationToken)
    {
        var contract = await dbContext.Contracts
            .Include(c => c.Benefits)
            .FirstOrDefaultAsync(c => c.Id == request.ContractId, cancellationToken);

        if (contract is null)
            return ContractErrors.ContractNotFound(request.ContractId);

        var benefit = contract.Benefits.FirstOrDefault(b => b.Id == request.BenefitId);
        if (benefit is null)
            return ContractErrors.Benefit.NotFound(request.BenefitId);

        var tenantId = dbContext.TenantId;
        if (string.IsNullOrWhiteSpace(tenantId) || contract.TenantId != tenantId)
            return ContractErrors.Benefit.CrossTenantBenefit;

        // Calculate completed payment total for this contract
        var completedPaymentTotal = await dbContext.Payments
            .Where(p => p.TenantId == contract.TenantId
                && p.Status == Domain.Platform.Billing.Payments.Enums.PaymentStatus.Completed
                && p.Allocations.Any(a =>
                    a.Status == Domain.Platform.Billing.Payments.Enums.PaymentAllocationStatus.Active
                    && a.Invoice != null
                    && a.Invoice.ContractId == contract.Id))
            .SumAsync(p => p.Allocations
                .Where(a => a.Status == Domain.Platform.Billing.Payments.Enums.PaymentAllocationStatus.Active
                    && a.Invoice != null
                    && a.Invoice.ContractId == contract.Id)
                .Sum(a => a.AllocatedAmount), cancellationToken);

        // Check for overdue installments on this contract
        var utcNow = DateTime.UtcNow;
        var hasOverdueInstallment = await dbContext.Installments
            .AnyAsync(i => i.ContractId == contract.Id
                && i.TenantId == tenantId
                && i.Status != Domain.Platform.Billing.Installments.InstallmentStatus.Cancelled
                && i.Status != Domain.Platform.Billing.Installments.InstallmentStatus.Paid
                && i.DueDateUtc < utcNow
                && i.RemainingAmount > 0, cancellationToken);

        var determinedStatus = eligibilityService.DetermineEligibilityStatus(
            benefit, contract, completedPaymentTotal, contract.ContractedAmount, hasOverdueInstallment);

        // If benefit can become eligible and is currently NotEligible, mark it
        if (determinedStatus == BenefitEligibilityStatus.Eligible
            && benefit.EligibilityStatus == BenefitEligibilityStatus.NotEligible)
        {
            benefit.MarkEligible(DateTime.UtcNow, contract.TenantId);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return benefit.EligibilityStatus;
    }
}
