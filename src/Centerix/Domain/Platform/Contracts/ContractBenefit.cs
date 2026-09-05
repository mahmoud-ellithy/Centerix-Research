namespace Centerix.Domain.Platform.Contracts;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Contracts.Enums;

/// <summary>
/// A commercial benefit or gift granted under a Contract.
/// Preserves its contractual financial value as an immutable snapshot.
/// </summary>
/// <remarks>
/// Business rule: The total value of benefits granted under a Contract must never
/// exceed the value of three months of the customer's contractual subscription value.
///
/// Examples:
///   Physical Gift: Barcode Printer, Value = 1,500
///   Physical Gift: Desktop Computer, Value = 8,000
/// </remarks>
public class ContractBenefit : Entity
{
    public Guid Id { get; private set; }
    public Guid ContractId { get; private set; }

    /// <summary>Type/category of benefit (Physical Gift, Service, Financial Credit, etc.).</summary>
    public ContractBenefitType BenefitType { get; private set; }

    /// <summary>Human-readable name/description of the benefit.</summary>
    public string Name { get; private set; } = default!;

    /// <summary>Optional detailed description.</summary>
    public string? Description { get; private set; }

    /// <summary>Contractual financial value of the benefit (snapshot at grant time).</summary>
    public decimal ContractualValue { get; private set; }

    /// <summary>Currency code for the contractual value.</summary>
    public string CurrencyCode { get; private set; } = default!;

    /// <summary>Whether the benefit has been granted/delivered to the tenant.</summary>
    public bool IsGranted { get; private set; }

    /// <summary>Date when the benefit was granted/delivered (if applicable).</summary>
    public DateTime? GrantedAtUtc { get; private set; }

    /// <summary>Who granted the benefit (user or system identifier).</summary>
    public string? GrantedBy { get; private set; }

    /// <summary>Optional notes about eligibility, conditions, or delivery.</summary>
    public string? Notes { get; private set; }

    /// <summary>The Contract this benefit belongs to.</summary>
    public Contract Contract { get; private set; } = default!;

    private ContractBenefit() { }

    private ContractBenefit(
        Guid id,
        Guid contractId,
        ContractBenefitType benefitType,
        string name,
        decimal contractualValue,
        string currencyCode,
        string? description = null,
        string? notes = null)
    {
        Id = id;
        ContractId = contractId;
        BenefitType = benefitType;
        Name = name;
        ContractualValue = contractualValue;
        CurrencyCode = currencyCode;
        Description = description;
        Notes = notes;
        IsGranted = false;
    }

    /// <summary>
    /// Creates a benefit/gift with validated commercial terms.
    /// </summary>
    public static Result<ContractBenefit> Create(
        Guid id,
        Guid contractId,
        ContractBenefitType benefitType,
        string name,
        decimal contractualValue,
        string currencyCode,
        string? description = null,
        string? notes = null)
    {
        if (id == Guid.Empty)
            return ContractErrors.Benefit.IdRequired;

        if (contractId == Guid.Empty)
            return ContractErrors.Benefit.ContractIdRequired;

        if (!Enum.IsDefined(benefitType))
            return ContractErrors.Benefit.InvalidType;

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
            contractualValue,
            currencyCode.Trim().ToUpperInvariant(),
            description,
            notes);
    }

    /// <summary>
    /// Marks the benefit as granted/delivered.
    /// </summary>
    public Result<Updated> MarkGranted(DateTime grantedAtUtc, string? grantedBy = null)
    {
        if (IsGranted)
            return ContractErrors.Benefit.AlreadyGranted;

        IsGranted = true;
        GrantedAtUtc = grantedAtUtc;
        GrantedBy = grantedBy;

        return Result.Updated;
    }
}
