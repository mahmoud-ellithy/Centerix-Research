namespace Centerix.Domain.Platform.Tenants.Enums;

public enum LifecycleStatus : byte
{
    Provisioning = 0,
    Active = 1,
    Suspended = 2,
    Trial = 3,
    Cancelled = 4
}
