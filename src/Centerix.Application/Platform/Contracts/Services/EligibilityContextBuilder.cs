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
/// <para>
/// All fact queries are issued once, then the rule tree is evaluated in memory. We never
/// issue per-rule database queries.
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
        // Commercial + lifecycle header facts (status, payment terms, effective, duration, currency).
        var contract = await _facts.GetContractFactsAsync(tenantId, contractId, cancellationToken)
            ?? throw new ContractBenefitFreezingError(
                    "ContractFreezing.NoContract",
                    $"Contract '{contractId}' was not found for tenant '{tenantId}'.");

        // Authoritative completed-payment facts. Single query — the evaluator iterates in memory.
        var completedPayments = await _facts.GetCompletedPaymentsAsync(tenantId, contractId, cancellationToken);

        // Overdue-installment flag (single boolean query).
        var hasOverdue = await _facts.HasOverdueInstallmentAsync(tenantId, contractId, utcNow, cancellationToken);

        return EligibilityContext.Create(
            tenantId: tenantId,
            contractId: contractId,
            benefitId: benefitId,
            contractStatus: contract.Status,
            paymentTerms: contract.PaymentTerms,
            utcNow: utcNow,
            contractStartUtc: contract.EffectiveAtUtc,
            contractCurrencyCode: contract.CurrencyCode,
            contractedAmount: contract.ContractedAmount,
            contractDurationMonths: contract.DurationMonths,
            hasOverdueInstallment: hasOverdue,
            completedPayments: completedPayments);
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