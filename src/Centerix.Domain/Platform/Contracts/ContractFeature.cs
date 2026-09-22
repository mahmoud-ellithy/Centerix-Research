namespace Centerix.Domain.Platform.Contracts;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;

/// <summary>
/// Immutable per-contract FEATURE ENTITLEMENT snapshot. Codes are copied from the plan's
/// PlanFeature rows when the contract is created, so later plan/feature changes never alter
/// what an existing tenant owns. Stored as the feature CODE (not FK) deliberately: entitlement
/// history must survive catalog reorganizations; uniqueness is enforced per contract.
/// </summary>
public class ContractFeature : GlobalAuditableEntity<Guid>
{
    public Guid ContractId { get; private set; }
    public string FeatureCode { get; private set; } = default!;

    public Contract Contract { get; private set; } = default!;

    private ContractFeature() { }

    private ContractFeature(Guid id, Guid contractId, string featureCode) : base(id)
    {
        ContractId = contractId;
        FeatureCode = featureCode;
    }

    /// <summary>
    /// Creates a normalized feature entitlement snapshot (trimmed, upper-invariant code).
    /// </summary>
    public static Result<ContractFeature> Create(Guid contractId, string featureCode)
    {
        var trimmed = featureCode?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return ContractErrors.FeatureCodeRequired;

        return new ContractFeature(Guid.NewGuid(), contractId, trimmed.ToUpperInvariant());
    }
}
