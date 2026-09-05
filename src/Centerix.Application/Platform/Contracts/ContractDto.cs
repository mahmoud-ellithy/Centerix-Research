namespace Centerix.Application.Platform.Contracts;

/// <summary>
/// DTO for Contract data returned to API clients.
/// Preserves the immutable commercial snapshot.
/// </summary>
public class ContractDto
{
    public Guid Id { get; set; }
    public string ContractNumber { get; set; } = default!;
    public byte Status { get; set; }
    public int PlanId { get; set; }
    public DateTime EffectiveAtUtc { get; set; }
    public DateTime EndsAtUtc { get; set; }
    public int DurationMonths { get; set; }
    public decimal MonthlyListPrice { get; set; }
    public decimal ContractualMonthlyValue { get; set; }
    public string CurrencyCode { get; set; } = default!;
    public decimal ContractedAmount { get; set; }
    public decimal DiscountAmount { get; set; }
    public string? PromotionReference { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

/// <summary>
/// DTO for a pricing tier snapshot.
/// </summary>
public class ContractPricingTierDto
{
    public Guid Id { get; set; }
    public int DurationMonths { get; set; }
    public decimal TierPrice { get; set; }
    public string CurrencyCode { get; set; } = default!;
    public decimal MonthlyListPrice { get; set; }
    public int DisplayOrder { get; set; }
}

/// <summary>
/// DTO for a contract benefit/gift.
/// </summary>
public class ContractBenefitDto
{
    public Guid Id { get; set; }
    public byte BenefitType { get; set; }
    public string Name { get; set; } = default!;
    public string? Description { get; set; }
    public decimal ContractualValue { get; set; }
    public string CurrencyCode { get; set; } = default!;
    public bool IsGranted { get; set; }
    public DateTimeOffset? GrantedAtUtc { get; set; }
}

/// <summary>
/// Detailed Contract DTO including pricing tiers and benefits.
/// </summary>
public class ContractDetailDto : ContractDto
{
    public List<ContractPricingTierDto> PricingTiers { get; set; } = [];
    public List<ContractBenefitDto> Benefits { get; set; } = [];
}
