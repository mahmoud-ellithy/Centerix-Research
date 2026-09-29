namespace Centerix.Infrastructure.Data.Configurations;

using Centerix.Domain.Platform.Contracts;
using Centerix.Domain.Platform.Contracts.EligibilityRules;
using Centerix.Domain.Platform.Promotions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// EF Core configuration for <see cref="FreeMonthsBenefit"/>.
///
/// Per <c>docs/COMMERCIAL-BENEFIT-DESIGN-VALIDATION.md</c> §D and §F, FreeMonths
/// is a separate persistence aggregate from <c>ContractBenefit</c> because the
/// two entitlements fulfill on different lifecycles (subscription extension vs.
/// physical handover). They share the <c>EligibilityRule</c> value-object and
/// canonical JSON serialization.
///
/// All commercial columns are non-nullable. The migration that adds this table
/// is schema-only: it does NOT manufacture rows from
/// <c>Contract.BonusMonths &gt; 0</c> (per design invariant 35: no historical
/// commercial facts are invented).
/// </summary>
public class FreeMonthsBenefitConfiguration : IEntityTypeConfiguration<FreeMonthsBenefit>
{
    public void Configure(EntityTypeBuilder<FreeMonthsBenefit> builder)
    {
        builder.ToTable("FreeMonthsBenefits", "Platform");

        builder.HasKey(b => b.Id);

        builder.Property(b => b.ContractId)
            .IsRequired();

        // EntitlementMonths: commercial entitlement. Immutable after creation.
        builder.Property(b => b.EntitlementMonths)
            .IsRequired();

        builder.Property(b => b.CurrencyCode)
            .HasMaxLength(3)
            .IsRequired();

        // Reversible eligibility state. Stored as a single byte to match the
        // enum's underlying type, matching the convention used by ContractBenefit.
        builder.Property(b => b.EligibilityStatus)
            .HasConversion<byte>()
            .IsRequired();

        builder.Property(b => b.EligibleAtUtc);

        // Monotone fulfillment state.
        builder.Property(b => b.FulfillmentStatus)
            .HasConversion<byte>()
            .IsRequired();

        builder.Property(b => b.GrantedAtUtc);

        builder.Property(b => b.AppliedAtUtc);

        // EligibilityRule is REQUIRED on every FreeMonthsBenefit (per design
        // invariant 26). Stored as nvarchar(4000) of canonical JSON (no CLR
        // type names, no executable code) — identical to ContractBenefit.
        builder.Property(b => b.EligibilityRule)
            .HasConversion(
                rule => rule != null ? EligibilityRuleSerializer.Serialize(rule) : null,
                json => !string.IsNullOrEmpty(json) ? EligibilityRuleSerializer.Deserialize(json) : null)
            .HasMaxLength(4000)
            .IsRequired();

        builder.HasIndex(b => b.ContractId)
            .HasDatabaseName("IX_FreeMonthsBenefits_ContractId");

        builder.HasIndex(b => new { b.ContractId, b.FulfillmentStatus })
            .HasDatabaseName("IX_FreeMonthsBenefits_ContractId_FulfillmentStatus");

        builder.HasIndex(b => new { b.ContractId, b.EligibilityStatus })
            .HasDatabaseName("IX_FreeMonthsBenefits_ContractId_EligibilityStatus");
    }
}

/// <summary>
/// EF Core configuration for <see cref="OfferFreeMonthsBenefit"/>.
///
/// Mirror of <see cref="FreeMonthsBenefitConfiguration"/> for the Offer-side
/// snapshot. The Contract-side <c>FreeMonthsBenefit.EligibilityRule</c> is
/// snapshotted verbatim from this row when a Contract is created from an
/// Accepted Offer (see <c>CreateContractFromOfferCommand</c>).
/// </summary>
public class OfferFreeMonthsBenefitConfiguration : IEntityTypeConfiguration<OfferFreeMonthsBenefit>
{
    public void Configure(EntityTypeBuilder<OfferFreeMonthsBenefit> builder)
    {
        builder.ToTable("OfferFreeMonthsBenefits", "Platform");

        builder.HasKey(b => b.Id);

        builder.Property(b => b.OfferId)
            .IsRequired();

        builder.Property(b => b.EntitlementMonths)
            .IsRequired();

        builder.Property(b => b.CurrencyCode)
            .HasMaxLength(3)
            .IsRequired();

        // EligibilityRule is REQUIRED on every OfferFreeMonthsBenefit (design
        // invariant 26). A commercial entitlement must never exist without a
        // rule defining when it becomes eligible.
        builder.Property(b => b.EligibilityRule)
            .HasConversion(
                rule => rule != null ? EligibilityRuleSerializer.Serialize(rule) : null,
                json => !string.IsNullOrEmpty(json) ? EligibilityRuleSerializer.Deserialize(json) : null)
            .HasMaxLength(4000)
            .IsRequired();

        builder.HasIndex(b => b.OfferId)
            .HasDatabaseName("IX_OfferFreeMonthsBenefits_OfferId");
    }
}
