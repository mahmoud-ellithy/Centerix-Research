namespace Centerix.Application.Platform.Promotions;

/// <summary>
/// DTO for an Offer returned to API clients.
/// </summary>
public class OfferDto
{
    public Guid Id { get; set; }
    public byte Status { get; set; }
    public int PlanId { get; set; }
    public int DurationMonths { get; set; }
    public decimal BaseAmount { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal FinalAmount { get; set; }
    public decimal MonthlyListPrice { get; set; }
    public string CurrencyCode { get; set; } = default!;
    public int? PromotionId { get; set; }
    public string? PromotionName { get; set; }
    public string? PromotionCode { get; set; }
    public string PromotionType { get; set; } = default!;
    public decimal? DiscountPercentage { get; set; }
    public int? ChargedMonths { get; set; }
    public DateTime CalculatedAtUtc { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
    public DateTime? AcceptedAtUtc { get; set; }
    public DateTime? ConvertedAtUtc { get; set; }
    public Guid? ContractId { get; set; }
}
