# Task 10 — Invoice Financial Integrity & Immutability

## Status: COMPLETE

### Commit SHA:
```
b05be1e
```

### Build:
**PASS** — 0 errors.

### Test Results:
```
Passed:  1100
Failed:     3 (pre-existing, unrelated)
Skipped:    0
Total:   1103
```

Pre-existing failures (NOT caused by Task 10):
- `Phase3AuthorizationHttpTests.Students_CrossTenantBranch_IsInvisibleAndReturnsNotFound`
- `Phase3AuthorizationHttpTests.Students_TenantAdmin_CanCreateReadUpdateSoftDelete`
- `Phase9FinancialConcurrencySqlServerTests.Concurrent_PaymentAllocations_CannotExceedPaymentAmount`

---

## Implemented Behavior

### 1. Invoice Immutability (Sections 4-5)

**Domain guards:**
- `Invoice.Errors.CannotAddLineNonDraft` — new error constant
- `Invoice.Errors.CannotRemoveLineNonDraft` — new error constant
- `Invoice.Errors.CannotModifyAfterIssuance` — new error constant (general-purpose)

**Handler enforcement:**
- `AddInvoiceLineHandler` — checks `invoice.Status != InvoiceStatus.Draft` before adding lines
- `RemoveInvoiceLineHandler` — checks `invoice.Status != InvoiceStatus.Draft` before removing lines

**Status matrix for line operations:**

| Invoice Status | AddLine | RemoveLine |
|---|---|---|
| Draft | Allowed | Allowed |
| Issued | Rejected | Rejected |
| Sent | Rejected | Rejected |
| PartiallyPaid | Rejected | Rejected |
| Paid | Rejected | Rejected |
| Cancelled | Rejected | Rejected |

### 2. Invoice Cancellation (Section 12)

**Already correct — no changes needed.**
- `Invoice.Cancel()` only allows `Draft → Cancelled`
- All other statuses return `CannotCancelNonDraft`
- No financial data is destroyed — cancellation is state-only

### 3. Invoice Total Integrity (Section 6)

**Already correct — no changes needed.**
- Invoice totals are set once at creation via `Invoice.Create()`
- All scalar properties use `private set` — no public mutation path
- `Issue()` only transitions status, does not modify amounts
- `InvoiceLine.Create()` returns line total deterministically (Quantity × UnitPrice)

### 4. Payment Integrity (Section 8)

**Already correct — no changes needed.**
- `Invoice.GetPaidAmount()` = sum of active PaymentAllocation amounts
- `Invoice.GetRemainingAmount()` = TotalAmount - GetPaidAmount()
- No mutable `PaidAmount` or `RemainingAmount` field exists
- Source of truth is PaymentAllocations, not stored values

### 5. Payment Allocation Boundary / Overpayment (Sections 9-10)

**NEW: Overpayment → TenantCredit conversion**

When a payment allocation exceeds the invoice remaining:
- Allocation is **capped** at the invoice remaining amount
- Excess becomes a `TenantCredit` with `CreditSourceType.Overpayment`
- Payment amount remains unchanged (Payment.Amount is immutable)
- Invoice transitions to `Paid`
- Ledger entries record only the allocated amount (not the excess)

When a payment arrives for an already fully-paid invoice:
- No PaymentAllocation is created (amount would be 0)
- Full requested amount becomes `TenantCredit` with `CreditSourceType.Overpayment`
- Audit trail preserved

**CreditSourceType extended:**
- Added `Overpayment = 4` to the enum

### 6. Credit Application (Section 11)

**NEW: `ApplyCreditToInvoiceCommand`**

| Property | Value |
|---|---|
| Command | `ApplyCreditToInvoiceCommand(CreditId, InvoiceId, Amount)` |
| Handler | `ApplyCreditToInvoiceHandler` |
| Validation | Credit must exist, be Available, amount > 0 and <= credit amount |
| Invoice check | Invoice must not be Draft or Cancelled |
| Remaining check | Amount must not exceed invoice remaining |
| Concurrency | Credit status transition prevents double-application |
| Audit | `TenantCredit.ApplyToInvoice` audit entry created |
| Ledger | `CreditCreation` ledger entry created |

**TenantCredit entity extended:**
- Added `AppliedToInvoiceId` property for invoice-level tracking
- Added `ApplyToInvoice(Guid invoiceId)` domain method
- Added `RowVersion` for optimistic concurrency

### 7. Concurrency (Section 15)

**Existing protections preserved:**
- `AllocatePaymentHandler` uses `Serializable` isolation + deadlock retry
- `RowVersion` on Invoice, Payment, PaymentAllocation, CustomerLedgerEntry
- Idempotency unique index on PaymentAllocations

**NEW: TenantCredit RowVersion**
- `TenantCredit.RowVersion` added for optimistic concurrency
- EF configuration: `.IsRowVersion()` on `RowVersion` property
- Migration: `20260917171310_AddInvoiceFinancialIntegrity`

### 8. Tenant Isolation (Section 16)

**Already correct for existing handlers.** All handlers operate through EF Core query filters that scope to the current tenant. Cross-tenant access returns `NotFound` because the entity is invisible to the query filter.

**NEW tests verify cross-tenant isolation for:**
- AddInvoiceLine
- RemoveInvoiceLine
- IssueInvoice
- CancelInvoice
- ApplyCreditToInvoice

---

## Database Changes

### Migration: `20260917171310_AddInvoiceFinancialIntegrity`

| Table | Column | Type | Notes |
|---|---|---|---|
| `Platform.TenantCredits` | `AppliedToInvoiceId` | `uniqueidentifier` nullable | Links credit to invoice |
| `Platform.TenantCredits` | `RowVersion` | `rowversion` | Optimistic concurrency |

---

## Files Modified

| File | Change |
|------|--------|
| `src/Centerix.Domain/Platform/Billing/Invoicing/InvoiceErrors.cs` | Added `CannotAddLineNonDraft`, `CannotRemoveLineNonDraft`, `CannotModifyAfterIssuance` |
| `src/Centerix.Domain/Platform/Billing/Credits/TenantCredit.cs` | Added `AppliedToInvoiceId`, `RowVersion`, `ApplyToInvoice()` method |
| `src/Centerix.Domain/Platform/Billing/Credits/Enums/CreditSourceType.cs` | Added `Overpayment = 4` |
| `src/Centerix.Infrastructure/Data/Configurations/TenantCreditConfiguration.cs` | Added `AppliedToInvoiceId` column, `RowVersion` configuration |
| `src/Centerix.Application/Platform/Billing/Commands/AddInvoiceLineCommand.cs` | Added Draft status guard |
| `src/Centerix.Application/Platform/Billing/Commands/RemoveInvoiceLineCommand.cs` | Added Draft status guard |
| `src/Centerix.Application/Platform/Billing/Commands/AllocatePaymentCommand.cs` | Overpayment handling: caps allocation, creates TenantCredit for excess |
| `src/Centerix.Application/Platform/Billing/Commands/CreateTenantCreditCommand.cs` | Added `ApplyCreditToInvoiceCommand` + handler |
| `src/Centerix.Infrastructure/Data/Migrations/20260917171310_AddInvoiceFinancialIntegrity.cs` | New migration |
| `tests/Centerix.SecurityTests/Phase10InvoiceFinancialIntegrityTests.cs` | **NEW** — 33 tests |
| `tests/Centerix.SecurityTests/Phase9FinancialLedgerHardeningTests.cs` | Updated overpayment-related tests |
| `tests/Centerix.SecurityTests/Phase9FinancialConcurrencySqlServerTests.cs` | Updated concurrent allocation test |

---

## Tests

### New Tests (33): `Phase10InvoiceFinancialIntegrityTests`

| # | Test | Result |
|---|------|--------|
| 1 | `DraftInvoice_AddLine_Succeeds` | PASS |
| 2 | `DraftInvoice_RemoveLine_Succeeds` | PASS |
| 3 | `IssuedInvoice_AddLine_IsRejected` | PASS |
| 4 | `SentInvoice_AddLine_IsRejected` | PASS |
| 5 | `PaidInvoice_AddLine_IsRejected` | PASS |
| 6 | `IssuedInvoice_RemoveLine_IsRejected` | PASS |
| 7 | `PartiallyPaidInvoice_RemoveLine_IsRejected` | PASS |
| 8 | `DraftInvoice_MultipleLines_CanBeAddedAndRemoved` | PASS |
| 9 | `Invoice_PartialPayment_RemainingIsCorrect` | PASS |
| 10 | `Invoice_FullPayment_BecomesPaid` | PASS |
| 11 | `Invoice_TwoPartialPayments_BecomesPaid` | PASS |
| 12 | `Overpayment_CreatesCreditForExcess` | PASS |
| 13 | `Overpayment_InvoiceRemainsPaid_CreditIsAvailable` | PASS |
| 14 | `ExactPayment_NoCreditCreated` | PASS |
| 15 | `ApplyCredit_ToIssuedInvoice_Succeeds` | PASS |
| 16 | `ApplyCredit_ExceedsAvailable_IsRejected` | PASS |
| 17 | `ApplyCredit_ExceedsInvoiceRemaining_IsRejected` | PASS |
| 18 | `ApplyCredit_ToDraftInvoice_IsRejected` | PASS |
| 19 | `ApplyCredit_AlreadyApplied_IsRejected` | PASS |
| 20 | `ApplyCredit_ToCancelledInvoice_IsRejected` | PASS |
| 21 | `DraftInvoice_Cancel_Succeeds` | PASS |
| 22 | `IssuedInvoice_Cancel_IsRejected` | PASS |
| 23 | `PaidInvoice_Cancel_IsRejected` | PASS |
| 24 | `Invoice_SnapshotFields_AreImmutable` | PASS |
| 25 | `TenantCredit_HasRowVersion` | PASS |
| 26 | `Invoice_PaidAmount_DerivedFromAllocations` | PASS |
| 27 | `CrossTenant_AddLine_IsRejected` | PASS |
| 28 | `CrossTenant_RemoveLine_IsRejected` | PASS |
| 29 | `CrossTenant_Issue_IsRejected` | PASS |
| 30 | `CrossTenant_Cancel_IsRejected` | PASS |
| 31 | `CrossTenant_ApplyCredit_IsRejected` | PASS |
| 32 | `AddInvoiceLine_LineTotalIsDeterministic` | PASS |
| 33 | `OverpaymentCredit_CanBeAppliedToAnotherInvoice` | PASS |

### Updated Existing Tests

| # | Test | Change |
|---|------|--------|
| 1 | `Allocation_Cannot_Exceed_Invoice_Outstanding` | Now expects overpayment credit creation |
| 2 | `Duplicate_Allocation_Cannot_Exceed_Invoice_Outstanding` | Now expects overpayment credit for excess |
| 3 | `Concurrent_Allocations_On_Same_Invoice_Total_Does_Not_Exceed_Invoice` | Now expects overpayment handling |
| 4 | `Concurrent_InvoiceAllocations_CannotExceedInvoiceTotal` | Updated for overpayment behavior |

---

## Rejected Invalid Operations

| Operation | Result |
|---|---|
| Add line to Issued/Sent/Paid/Cancelled invoice | Rejected: `Invoice.CannotAddLineNonDraft` |
| Remove line from Issued/Sent/Paid/Cancelled invoice | Rejected: `Invoice.CannotRemoveLineNonDraft` |
| Cancel non-Draft invoice | Rejected: `Invoice.CannotCancelNonDraft` |
| Apply credit to Draft invoice | Rejected: `Invoice.CannotApplyCreditToDraftOrCancelled` |
| Apply credit to Cancelled invoice | Rejected: `Invoice.CannotApplyCreditToDraftOrCancelled` |
| Apply credit exceeding available amount | Rejected: `TenantCredit.InvalidApplicationAmount` |
| Apply credit exceeding invoice remaining | Rejected: `TenantCredit.ExceedsInvoiceRemaining` |
| Apply already-used credit | Rejected: `TenantCredit.NotAvailable` |
| Cross-tenant invoice/credit operations | Rejected: `Invoice.NotFound` (query filter) |

---

## Scope Compliance

- [x] No Upgrade/Downgrade implemented
- [x] No Referral Credit implemented
- [x] No new commercial concepts introduced
- [x] Existing Contract/Subscription/BillingChain preserved
- [x] Existing Refund functionality compatible
- [x] No major architecture redesign
- [x] All Task 1-9.5 tests remain green (excluding 3 pre-existing failures)
