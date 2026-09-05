namespace Centerix.Domain.Platform.Contracts.Enums;

/// <summary>
/// Type/category of a commercial benefit or gift granted under a Contract.
/// </summary>
public enum ContractBenefitType : byte
{
    /// <summary>Physical product (e.g., Barcode Printer, Desktop Computer).</summary>
    PhysicalGift = 0,

    /// <summary>Service-based benefit (e.g., free training, premium support).</summary>
    Service = 1,

    /// <summary>Financial credit applied to the contract or future invoices.</summary>
    FinancialCredit = 2,

    /// <summary>Extended subscription term (additional bonus months).</summary>
    ExtendedTerm = 3,

    /// <summary>Other benefit types not covered above.</summary>
    Other = 4
}
