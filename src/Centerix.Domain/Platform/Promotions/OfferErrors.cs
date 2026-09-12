namespace Centerix.Domain.Platform.Promotions;

using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Promotions.Enums;

public static class OfferErrors
{
    public static Error NotFound(Guid id) =>
        Error.NotFound("Offer.NotFound", $"Offer with ID '{id}' was not found");

    public static Error Expired =>
        Error.Validation("Offer.Expired", "This offer has expired and can no longer be accepted");

    public static Error AlreadyAccepted =>
        Error.Conflict("Offer.AlreadyAccepted", "This offer has already been accepted");

    public static Error AlreadyConverted =>
        Error.Conflict("Offer.AlreadyConverted", "This offer has already been converted to a contract");

    public static Error InvalidStateTransition(OfferStatus current, string action) =>
        Error.Conflict("Offer.InvalidStateTransition",
            $"Cannot {action} an offer in status '{current}'");

    public static Error CrossTenantOffer =>
        Error.Forbidden("Offer.CrossTenant", "Cannot access an offer belonging to a different tenant");

    public static Error TenantNotResolved =>
        Error.Validation("Offer.TenantNotResolved", "No authorized tenant context found");

    public static Error PlanNotFound =>
        Error.NotFound("Offer.PlanNotFound", "The specified plan was not found");

    public static Error PlanInactive =>
        Error.Validation("Offer.PlanInactive", "Cannot calculate an offer for an inactive plan");
}
