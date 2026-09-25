namespace Centerix.Domain.Platform.Contracts;

using Centerix.Domain.Platform.Subscriptions;

/// <summary>
/// Immutable snapshot of all commercial terms from a Contract, used to create a Subscription
/// without querying mutable Plan data. Built exclusively from Contract's own fields.
/// </summary>
public record SubscriptionSnapshot(
    decimal MonthlyListPrice,
    decimal MonthlyCharge,
    decimal ContractualMonthlyValue,
    string CurrencyCode,
    int DurationMonths,
    int BonusMonths,
    int ChargedMonths,
    int MaxStudents,
    int MaxUsers,
    int MaxBranches,
    int MaxTeachers,
    int StorageGb,
    int SmsQuota,
    IReadOnlyList<string> FeatureCodes);
