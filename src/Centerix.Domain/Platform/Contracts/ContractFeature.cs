namespace Centerix.Domain.Platform.Contracts;

using Centerix.Domain.Common;

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

    public static ContractFeature Create(Guid contractId, string featureCode)
        => new(Guid.NewGuid(), contractId, featureCode.Trim());
}
