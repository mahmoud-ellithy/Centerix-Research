namespace Centerix.Domain.Platform.Billing.Credits.Enums;

public enum CreditStatus : byte
{
    Available = 0,
    Applied = 1,
    Expired = 2,
    Revoked = 3
}
