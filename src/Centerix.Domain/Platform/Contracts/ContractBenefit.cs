namespace Centerix.Domain.Platform.Contracts;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Contracts.Enums;

/// <summary>
/// A commercial benefit (gift) granted as part of a Contract agreement.
/// Preserves its contractual financial value as an immutable snapshot so future
/// commercial configuration changes do not alter the contractual terms.
/// </summary>
/// <remarks>
/// Examples:
///   Physical Gift: Barcode Printer, Value = 1,500
///   Physical Gift: Desktop Computer, Value = 8,000
///
/// The financial invariant requires that the total value of all benefits under a Contract
/// must not exceed three months of the customer's contractual monthly value.
/// </remarks>
public class ContractBenefit : Entity
{
    public Guid Id { get; private set; }
    public Guid ContractId { get; private set; }

    /// <summary>Category/type of benefit (e.g., PhysicalGift, Service).</summary>
    public ContractBenefitType BenefitType { get; private set; }

    /// <summary>Human-readable name of the benefit.</summary>
    public string Name { get; private set; } = default!;

    /// <summary>Optional description providing additional detail.</summary>
    public string? Description { get; private set; }

    /// <summary>Contractual value of this benefit at the time of contract creation (immutable).</summary>
    public decimal ContractualValue { get; private set; }

    /// <summary>Currency code (ISO-4217, e.g., EGP, USD).</summary>
    public string CurrencyCode { get; private set; } = default!;

    /// <summary>Whether this benefit has been actually granted/delivered to the tenant.</summary>
    public bool IsGranted { get; private set; }

    /// <summary>UTC timestamp when the benefit was granted/delivered.</summary>
    public DateTime? GrantedAtUtc { get; private set; }

    /// <summary>The Contract this benefit belongs to.</summary>
    public Contract Contract { get; private set; } = default!;

    private ContractBenefit() { }

    private ContractBenefit(
        Guid id,
        Guid contractId,
        ContractBenefitType benefitType,
        string name,
        string? description,
        decimal contractualValue,
        string currencyCode)
    {
        Id = id;
        ContractId = contractId;
        BenefitType = benefitType;
        Name = name;
        Description = description;
        ContractualValue = contractualValue;
        CurrencyCode = currencyCode;
    }

    /// <summary>
    /// Creates a ContractBenefit with validated parameters.
    /// </summary>
    public static Result<ContractBenefit> Create(
        Guid id,
        Guid contractId,
        ContractBenefitType benefitType,
        string name,
        string? description,
        decimal contractualValue,
        string currencyCode)
    {
        if (id == Guid.Empty)
            return ContractErrors.Benefit.IdRequired;

        if (contractId == Guid.Empty)
            return ContractErrors.Benefit.ContractIdRequired;

        if (string.IsNullOrWhiteSpace(name))
            return ContractErrors.Benefit.NameRequired;

        if (contractualValue < 0)
            return ContractErrors.Benefit.InvalidValue;

        if (string.IsNullOrWhiteSpace(currencyCode) || currencyCode.Trim().Length != 3)
            return ContractErrors.Benefit.InvalidCurrency;

        return new ContractBenefit(
            id,
            contractId,
            benefitType,
            name.Trim(),
            description?.Trim(),
            contractualValue,
            currencyCode.Trim().ToUpperInvariant());
    }

    /// <summary>
    /// Marks this benefit as granted/delivered to the tenant.
    /// </summary>
    public Result<Updated> MarkGranted(DateTime utcNow)
    {
        if (IsGranted)
            return Result.Updated;

        IsGranted = true;
        GrantedAtUtc = utcNow;
        return Result.Updated;
    }
}
