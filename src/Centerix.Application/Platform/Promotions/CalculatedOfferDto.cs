namespace Centerix.Application.Platform.Promotions;

using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Promotions;

/// <summary>
/// DTO for the result of a promotion calculation (commercial offer).
/// </summary>
public class CalculatedOfferDto
{
    public int PlanId { get; set; }
    public int DurationMonths { get; set; }
    public decimal BaseAmount { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal FinalAmount { get; set; }
    public int? PromotionId { get; set; }
    public string? PromotionName { get; set; }
    public string? PromotionCode { get; set; }
    public string PromotionType { get; set; } = default!;
    public decimal? DiscountPercentage { get; set; }
    public int? ChargedMonths { get; set; }
    public decimal MonthlyListPrice { get; set; }
    public string CurrencyCode { get; set; } = default!;
}
