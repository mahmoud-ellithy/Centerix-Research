namespace Centerix.Domain.Platform.Billing.Credits.Enums;

public enum CreditStatus : byte
{
    Available = 0,
    PartiallyApplied = 1,
    Applied = 2,
    Expired = 3,
    Revoked = 4,
    Reversed = 5
}
