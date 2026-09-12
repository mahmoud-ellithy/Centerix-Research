namespace Centerix.Application.Platform.Promotions;

using Centerix.Domain.Platform.Contracts.Enums;

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
    public List<OfferBenefitDto> Benefits { get; set; } = [];
}

/// <summary>
/// DTO for a benefit snapshot attached to an Offer.
/// </summary>
public class OfferBenefitDto
{
    public Guid Id { get; set; }
    public ContractBenefitType BenefitType { get; set; }
    public string Name { get; set; } = default!;
    public string? Description { get; set; }
    public decimal ContractualValue { get; set; }
    public string CurrencyCode { get; set; } = default!;
}
