namespace Centerix.Domain.Platform.Promotions.Enums;

/// <summary>
/// Lifecycle status of a Promotion. Domain-controlled transitions.
/// Only Active promotions with a valid date range can be applied.
/// </summary>
public enum PromotionStatus : byte
{
    /// <summary>Promotion is being configured. Cannot be applied.</summary>
    Draft = 0,

    /// <summary>Promotion is live and can be applied (subject to date validity).</summary>
    Active = 1,

    /// <summary>Promotion has passed its EndsAtUtc. Cannot be applied.</summary>
    Expired = 2,

    /// <summary>Promotion has been manually deactivated. Cannot be applied.</summary>
    Disabled = 3
}
