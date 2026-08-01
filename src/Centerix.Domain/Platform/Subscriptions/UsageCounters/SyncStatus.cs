namespace Centerix.Domain.Platform.Subscriptions.UsageCounters;

public enum SyncStatus : byte
{
    Pending = 0,
    Syncing = 1,
    Completed = 2,
    Failed = 3
}
