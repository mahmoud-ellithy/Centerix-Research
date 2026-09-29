namespace Centerix.Domain.Platform.Contracts.EligibilityRules;

/// <summary>
/// A rule that requires the contract to be in Active status.
/// Parameterless singleton — use <see cref="EligibilityRule.ContractActive()"/>.
/// </summary>
public sealed class ContractActiveRule : EligibilityRule
{
    /// <summary>Singleton instance; the rule carries no parameters.</summary>
    internal static readonly ContractActiveRule Instance = new();

    private ContractActiveRule() { }

    /// <inheritdoc />
    public override bool Equals(EligibilityRule? other) => other is ContractActiveRule;

    /// <inheritdoc />
    public override int GetHashCode() => typeof(ContractActiveRule).GetHashCode();

    /// <inheritdoc />
    public override string ToString() => "ContractActive";
}

/// <summary>
/// A rule that requires no overdue required installment on the contract.
/// Parameterless singleton — use <see cref="EligibilityRule.NoOverdueInstallment()"/>.
/// </summary>
public sealed class NoOverdueInstallmentRule : EligibilityRule
{
    /// <summary>Singleton instance; the rule carries no parameters.</summary>
    internal static readonly NoOverdueInstallmentRule Instance = new();

    private NoOverdueInstallmentRule() { }

    /// <inheritdoc />
    public override bool Equals(EligibilityRule? other) => other is NoOverdueInstallmentRule;

    /// <inheritdoc />
    public override int GetHashCode() => typeof(NoOverdueInstallmentRule).GetHashCode();

    /// <inheritdoc />
    public override string ToString() => "NoOverdueInstallment";
}
