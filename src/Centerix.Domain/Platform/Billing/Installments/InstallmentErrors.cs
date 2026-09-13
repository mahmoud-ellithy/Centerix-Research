namespace Centerix.Domain.Platform.Billing.Installments;

using Centerix.Domain.Common.Results;

/// <summary>
/// Error codes for the Installment (payment obligation) module.
/// </summary>
public static class InstallmentErrors
{
    public static Error IdRequired =>
        Error.Validation("Installment.Id_Required", "Installment ID is required.");

    public static Error TenantIdRequired =>
        Error.Validation("Installment.TenantId_Required", "Tenant ID is required.");

    public static Error ContractIdRequired =>
        Error.Validation("Installment.ContractId_Required", "Contract ID is required.");

    public static Error ContractNotFound =>
        Error.NotFound("Installment.ContractNotFound", "Contract was not found.");

    public static Error ContractNotActive =>
        Error.Validation("Installment.ContractNotActive", "Installments can only be created for Active contracts.");

    public static Error AmountMustBePositive =>
        Error.Validation("Installment.Amount_MustBePositive", "Installment amount must be greater than zero.");

    public static Error CurrencyRequired =>
        Error.Validation("Installment.Currency_Required", "Currency code is required.");

    public static Error CurrencyMismatch(string expected) =>
        Error.Validation("Installment.Currency_Mismatch",
            $"Installment currency must match the contract currency '{expected}'.");

    public static Error DueDateRequired =>
        Error.Validation("Installment.DueDate_Required", "Due date is required.");

    public static Error CoveredPeriodRequired =>
        Error.Validation("Installment.CoveredPeriod_Required", "Covered period start and end are required.");

    public static Error CoveredPeriodInvalid =>
        Error.Validation("Installment.CoveredPeriod_Invalid", "Covered period end must be after covered period start.");

    public static Error CoveredPeriodExceedsContract =>
        Error.Validation("Installment.CoveredPeriod_ExceedsContract",
            "Covered period end must not exceed the contract end date.");

    public static Error CoveredPeriodStartBeforeContract =>
        Error.Validation("Installment.CoveredPeriod_StartBeforeContract",
            "Covered period start must not be before the contract effective date.");

    public static Error OverlappingPeriod(Guid existingInstallmentId) =>
        Error.Conflict("Installment.OverlappingPeriod",
            $"Installment covers a period that overlaps with an existing installment (ID: {existingInstallmentId}).");

    public static Error GapInSchedule =>
        Error.Validation("Installment.GapInSchedule",
            "Installment covered period must be contiguous with the previous installment's covered period.");

    public static Error TotalScheduleExceedsContractDuration =>
        Error.Validation("Installment.TotalSchedule_ExceedsContractDuration",
            "Total covered duration of all installments exceeds the contract duration.");

    public static Error TotalScheduleAmountMismatch(decimal expected, decimal actual) =>
        Error.Validation("Installment.TotalSchedule_AmountMismatch",
            $"Total installment amount ({actual}) does not match the required amount ({expected}).");

    public static Error ScheduleAlreadyComplete =>
        Error.Conflict("Installment.ScheduleAlreadyComplete",
            "A complete installment schedule already exists for this contract.");

    public static Error NotFound =>
        Error.NotFound("Installment.NotFound", "Installment was not found.");

    public static Error CannotUpdatePaidOrCancelled =>
        Error.Conflict("Installment.CannotUpdatePaidOrCancelled",
            "Cannot update an installment that is Paid or Cancelled.");

    public static Error CannotCancelPaidOrCancelled =>
        Error.Conflict("Installment.CannotCancelPaidOrCancelled",
            "Cannot cancel an installment that is already Paid or Cancelled.");

    public static Error CannotCancelHasAllocations =>
        Error.Conflict("Installment.CannotCancelHasAllocations",
            "Cannot cancel an installment that has payment allocations.");

    public static Error InvalidStateTransition(InstallmentStatus current, string action) =>
        Error.Conflict("Installment.InvalidStateTransition",
            $"Cannot {action} an installment in status '{current}'.");

    public static Error CrossTenantAccess =>
        Error.Forbidden("Installment.CrossTenant", "Cannot access an installment belonging to a different tenant.");

    public static Error AllocationExceedsInstallment =>
        Error.Conflict("Installment.AllocationExceedsInstallment",
            "Total allocations exceed the installment amount.");

    public static Error SequenceNumberMustBePositive =>
        Error.Validation("Installment.SequenceNumber_MustBePositive", "Sequence number must be positive.");

    public static Error DuplicateSequenceNumber(int sequenceNumber) =>
        Error.Conflict("Installment.DuplicateSequenceNumber",
            $"An installment with sequence number {sequenceNumber} already exists for this contract.");

    public static Error CannotUpdateNonPending =>
        Error.Conflict("Installment.CannotUpdateNonPending",
            "Only Pending installments can be updated.");

    public static Error ScheduleExceedsContractObligation(decimal scheduledTotal, decimal contractedAmount) =>
        Error.Validation("Installment.Schedule_ExceedsContractObligation",
            $"Total scheduled installment amount ({scheduledTotal}) would exceed the contract obligation ({contractedAmount}).");

    public static Error AmountWouldCorruptSettlement =>
        Error.Conflict("Installment.AmountWouldCorruptSettlement",
            "New amount would result in settled amount exceeding the installment amount.");
}
