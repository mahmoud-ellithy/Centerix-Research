namespace Centerix.Domain.Platform.Promotions.Enums;

/// <summary>
/// Lifecycle of a commercial Offer.
/// </summary>
public enum OfferStatus
{
    /// <summary>Offer has been calculated but not yet accepted by the customer.</summary>
    Calculated = 0,

    /// <summary>Customer has accepted the offer. Immutable snapshot is locked.</summary>
    Accepted = 1,

    /// <summary>Offer has been converted into a Contract. Cannot be re-accepted.</summary>
    ConvertedToContract = 2,

    /// <summary>Offer has expired before acceptance.</summary>
    Expired = 3
}
