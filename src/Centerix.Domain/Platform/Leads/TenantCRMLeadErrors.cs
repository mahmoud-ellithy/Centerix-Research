namespace Centerix.Domain.Platform.Leads;

using Centerix.Domain.Common.Results;

public static class TenantCRMLeadErrors
{
    public static Error CenterNameRequired =>
        Error.Validation("Lead.CenterName_Required", "Center name is required");

    public static Error ContactNameRequired =>
        Error.Validation("Lead.ContactName_Required", "Contact name is required");

    public static Error PhoneRequired =>
        Error.Validation("Lead.Phone_Required", "Phone number is required");

    public static Error SourceRequired =>
        Error.Validation("Lead.Source_Required", "Source is required");

    public static Error StageRequired =>
        Error.Validation("Lead.Stage_Required", "Stage is required");

    public static Error InvalidStageTransition =>
        Error.Conflict("Lead.InvalidStageTransition", "The requested stage transition is not allowed");

    public static Error InvalidPhoneNumber =>
        Error.Validation("Lead.InvalidPhoneNumber", "Phone number must be 7-15 digits and may start with '+'");
}
