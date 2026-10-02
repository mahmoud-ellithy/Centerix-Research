using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Platform.Contracts.EligibilityRules;

namespace Centerix.Application.Platform.Contracts.Services;

/// <summary>
/// Builds an <see cref="EligibilityContext"/> by composing facts fetched exclusively via
/// <see cref="IOwnerOnlyFactQuery"/>. The builder deliberately does NOT take
/// <c>IAppDbContext</c>: all data access must go through the owner-only fact query seam,
/// which enforces the cross-tenant guard.
/// </summary>
/// <remarks>
/// <para>
/// The builder is stateless and thread-safe; it is therefore registered as a singleton.
/// </para>
/// </remarks>
public sealed class EligibilityContextBuilder
{
    private readonly IOwnerOnlyFactQuery _facts;

    public EligibilityContextBuilder(IOwnerOnlyFactQuery facts) => _facts = facts;

    /// <summary>
    /// Assembles the context for the supplied tenant/contract/benefit triple. Throws
    /// <see cref="TenantScopeException"/> when any fact query reveals a cross-tenant row.
    /// </summary>
    public async Task<EligibilityContext> BuildAsync(
        string tenantId,
        Guid contractId,
        Guid benefitId,
        DateTime utcNow,
        CancellationToken cancellationToken = default)
    {
        // Commercial header facts (status, payment terms)
        var commercial = await _facts.GetContractCommercialFactsAsync(tenantId, contractId, cancellationToken)
            ?? throw new ContractBenefitFreezingError(
                    "ContractFreezing.NoContract",
                    $"Contract '{contractId}' was not found for tenant '{tenantId}'.");

        // Snapshot facts (effective date, contracted amount, duration)
        var snapshot = await _facts.GetContractSnapshotFactsAsync(tenantId, contractId, cancellationToken)
            ?? throw new ContractBenefitFreezingError(
                    "ContractFreezing.NoContract",
                    $"Contract '{contractId}' was not found for tenant '{tenantId}'.");

        // Settlement totals
        var amountPaid = await _facts.GetAmountPaidAsync(tenantId, contractId, cancellationToken);
        var paymentMethod = await _facts.GetCurrentPaymentMethodAsync(tenantId, contractId, cancellationToken);
        var hasOverdue = await _facts.HasOverdueInstallmentAsync(tenantId, contractId, utcNow, cancellationToken);

        // Days from contract start: floor((utcNow - effectiveAt) by day boundary).
        var daysFromContractStart = ComputeDaysFromContractStart(snapshot.EffectiveAtUtc, utcNow);

        return EligibilityContext.Create(
            tenantId: tenantId,
            contractId: contractId,
            benefitId: benefitId,
            contractStatus: commercial.Status,
            paymentTerms: commercial.PaymentTerms,
            paymentMethod: paymentMethod,
            utcNow: utcNow,
            amountPaid: amountPaid,
            contractedAmount: snapshot.ContractedAmount,
            daysFromContractStart: daysFromContractStart,
            contractDurationMonths: snapshot.DurationMonths,
            hasOverdueInstallment: hasOverdue);
    }

    private static int ComputeDaysFromContractStart(DateTime effectiveAtUtc, DateTime utcNow)
    {
        // Treat effective as midnight UTC for stable day arithmetic.
        var from = effectiveAtUtc.Date;
        var to = utcNow.Date;
        var diff = to - from;
        return diff.TotalDays >= 0 ? (int)diff.TotalDays : 0;
    }
}

/// <summary>
/// Domain failure raised by the freeze pipeline. Maps to an <c>Error.Validation</c> when surfaced
/// through MediatR; tests assert the typed <c>Code</c>.
/// </summary>
public sealed class ContractBenefitFreezingError : Exception
{
    public string Code { get; }

    public ContractBenefitFreezingError(string code, string message) : base(message) => Code = code;
}