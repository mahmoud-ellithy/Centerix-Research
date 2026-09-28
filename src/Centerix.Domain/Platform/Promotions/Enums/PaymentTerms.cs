namespace Centerix.Domain.Platform.Promotions.Enums;

/// <summary>
/// Commercial payment mode for a contract.
///
/// This value is the authoritative commercial decision recorded on an Offer and
/// snapshotted onto a Contract. It is set explicitly by the platform operator when
/// the Offer is created/calculated; it MUST NOT be derived from
/// <c>PromotionType</c>, <c>Plan.BonusMonths</c>, installment rows, or any other
/// indirect field.
///
/// Invariants (see docs/COMMERCIAL-BENEFIT-DESIGN-VALIDATION.md):
///   - <c>Offer.PaymentTerms</c> is explicit.
///   - <c>Contract.PaymentTerms == accepted Offer.PaymentTerms</c>.
///   - <c>Contract.PaymentTerms</c> is immutable after Contract creation.
///   - <c>FullUpfront</c> ⇒ no installment schedule may be created.
///   - Installment rows never determine or rewrite PaymentTerms.
/// </summary>
public enum PaymentTerms
{
    /// <summary>
    /// Customer pays the full contracted amount up-front. No installment
    /// schedule is permitted for the contract.
    /// </summary>
    FullUpfront = 0,

    /// <summary>
    /// Customer pays the contracted amount across multiple installments.
    /// A schedule is required before the first invoice can be issued.
    /// </summary>
    Installments = 1
}
