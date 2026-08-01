namespace Centerix.Domain.Platform.Subscriptions.UsageCounters;

using Centerix.Domain.Common;

/// <summary>
/// Central usage counters per tenant. PK is TenantId (1:1 with Tenant).
/// Uses GlobalAuditableEntity to avoid IHasTenantId conflict (TenantId IS the PK).
/// </summary>
public class TenantUsageCounter : GlobalAuditableEntity<Guid>
{
    public int StudentsCount { get; private set; }
    public int UsersCount { get; private set; }
    public int BranchesCount { get; private set; }
    public int TeachersCount { get; private set; }
    public int StorageUsedMB { get; private set; }
    public int SMSUsedThisCycle { get; private set; }
    public int EffectiveMaxStudents { get; private set; }
    public int EffectiveMaxUsers { get; private set; }
    public int EffectiveMaxBranches { get; private set; }
    public int EffectiveMaxTeachers { get; private set; }
    public DateTime CalculatedAt { get; private set; }
    public SyncStatus SyncStatus { get; private set; }

    private TenantUsageCounter() { }

    private TenantUsageCounter(
        Guid id,
        int studentsCount,
        int usersCount,
        int branchesCount,
        int teachersCount,
        int storageUsedMB,
        int smsUsedThisCycle,
        int effectiveMaxStudents,
        int effectiveMaxUsers,
        int effectiveMaxBranches,
        int effectiveMaxTeachers,
        DateTime calculatedAt,
        SyncStatus syncStatus)
        : base(id)
    {
        StudentsCount = studentsCount;
        UsersCount = usersCount;
        BranchesCount = branchesCount;
        TeachersCount = teachersCount;
        StorageUsedMB = storageUsedMB;
        SMSUsedThisCycle = smsUsedThisCycle;
        EffectiveMaxStudents = effectiveMaxStudents;
        EffectiveMaxUsers = effectiveMaxUsers;
        EffectiveMaxBranches = effectiveMaxBranches;
        EffectiveMaxTeachers = effectiveMaxTeachers;
        CalculatedAt = calculatedAt;
        SyncStatus = syncStatus;
    }

    public static TenantUsageCounter Create(
        Guid id,
        int studentsCount,
        int usersCount,
        int branchesCount,
        int teachersCount,
        int storageUsedMB,
        int smsUsedThisCycle,
        int effectiveMaxStudents,
        int effectiveMaxUsers,
        int effectiveMaxBranches,
        int effectiveMaxTeachers,
        DateTime calculatedAt)
    {
        return new TenantUsageCounter(
            id,
            studentsCount,
            usersCount,
            branchesCount,
            teachersCount,
            storageUsedMB,
            smsUsedThisCycle,
            effectiveMaxStudents,
            effectiveMaxUsers,
            effectiveMaxBranches,
            effectiveMaxTeachers,
            calculatedAt,
            SyncStatus.Pending);
    }

    public void UpdateCounts(
        int studentsCount,
        int usersCount,
        int branchesCount,
        int teachersCount,
        int storageUsedMB,
        int smsUsedThisCycle)
    {
        StudentsCount = studentsCount;
        UsersCount = usersCount;
        BranchesCount = branchesCount;
        TeachersCount = teachersCount;
        StorageUsedMB = storageUsedMB;
        SMSUsedThisCycle = smsUsedThisCycle;
        CalculatedAt = DateTime.UtcNow;
    }

    public void MarkSynced()
    {
        SyncStatus = SyncStatus.Completed;
    }

    public void MarkSyncFailed()
    {
        SyncStatus = SyncStatus.Failed;
    }
}
