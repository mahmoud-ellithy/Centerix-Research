namespace Centerix.Domain.Platform.Subscriptions;

/// <summary>
/// Canonical limit-type codes shared by Plan limits, subscription snapshots, overrides and
/// usage counters. Stable contract for future business modules.
/// </summary>
public static class LimitTypeCodes
{
    public const string Students = "Students";
    public const string Users = "Users";
    public const string Branches = "Branches";
    public const string Teachers = "Teachers";

    public static IReadOnlyCollection<string> All => [Students, Users, Branches, Teachers];
}
