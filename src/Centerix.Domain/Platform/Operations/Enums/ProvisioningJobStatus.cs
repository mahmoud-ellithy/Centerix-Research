namespace Centerix.Domain.Platform.Operations.Enums;

public enum ProvisioningJobStatus : byte
{
    Pending = 0,
    Creating = 1,
    Migrating = 2,
    Seeding = 3,
    Ready = 4,
    Failed = 5
}
