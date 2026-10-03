namespace Centerix.Application.Platform.Promotions;

using Centerix.Domain.Platform.Contracts.Enums;

/// <summary>
/// DTO for Promotion data returned to API clients.
/// </summary>
public class PromotionDto
{
    public int Id { get; set; }
    public string Name { get; set; } = default!;
    public string? Code { get; set; }
    public byte Type { get; set; }
    public byte Status { get; set; }
    public int PlanId { get; set; }
    public int DurationMonths { get; set; }
    public DateTime StartsAtUtc { get; set; }
    public DateTime EndsAtUtc { get; set; }
    public int Priority { get; set; }
    public decimal? Percentage { get; set; }
    public decimal? FixedAmount { get; set; }
    public decimal? PromotionalPrice { get; set; }
    public int? ChargedMonths { get; set; }
    public int? FreeMonthsCount { get; set; }
    public string? BenefitName { get; set; }
    public string? BenefitDescription { get; set; }
    public decimal? BenefitValue { get; set; }
    public ContractBenefitType? BenefitType { get; set; }
    public string? BenefitCurrencyCode { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}
