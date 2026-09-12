namespace Centerix.Domain.Platform.Promotions;

using Centerix.Domain.Platform.Plans;

/// <summary>
/// Result of applying a Promotion to a Plan at a specific point in time.
/// Contains all commercial terms needed to create a Contract snapshot.
/// </summary>
public sealed record CalculatedOffer
{
    public int PlanId { get; init; }
    public int DurationMonths { get; init; }
    public decimal BaseAmount { get; init; }
    public decimal DiscountAmount { get; init; }
    public decimal FinalAmount { get; init; }
    public int? PromotionId { get; init; }
    public string? PromotionName { get; init; }
    public string? PromotionCode { get; init; }
    public string PromotionType { get; init; } = default!;
    public decimal? DiscountPercentage { get; init; }
    public int? ChargedMonths { get; init; }
    public decimal MonthlyListPrice { get; init; }
    public string CurrencyCode { get; init; } = default!;
    public DateTime CalculatedAtUtc { get; init; }
    public DateTime? ExpiresAtUtc { get; init; }
}
