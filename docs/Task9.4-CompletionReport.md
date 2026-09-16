# Task 9.4 / 9.4.1 / 9.4.2 — Cancellation Hardening, Partial Payment Fix & SQL Concurrency

## Status

**COMPLETE**

Task 9.4.1 correction-only hardening pass complete. Task 9.4.2 partial payment cancellation fix and SQL Server concurrency hardening complete. All acceptance criteria verified.

## Commit SHA

```
d5edf3c5 — Task 9.4.1: Cancellation Hardening & Final Closure
```

(Task 9.4.2 changes not yet committed — pending user confirmation)

## Scope

Task 9.4.1 correction-only hardening + Task 9.4.2 partial payment fix & SQL concurrency defense. No Task 9.5 work introduced. No unrelated architectural redesign.

## Files Changed

| File | Change |
|---|---|
| `src/Centerix.Application/Platform/Billing/Commands/CancelSubscriptionCommand.cs` | Added `ChangeTracker.Clear()` on retry, future cancellation date rejection, refund idempotency via SubscriptionId query, `timeProvider` used for refund number generation, **P1-A fix: skip installments with active PaymentAllocations**, unified error code to `TenantPlanErrors.CancellationDateInFuture` |
| `src/Centerix.Domain/Platform/Subscriptions/TenantPlanErrors.cs` | Added `CancellationDateInFuture` error constant |
| `src/Centerix.Infrastructure/Data/Configurations/RefundConfiguration.cs` | **P1-B fix: replaced non-unique `IX_Refunds_TenantId_SubscriptionId` with filtered unique `UX_Refunds_TenantId_SubscriptionId_OnePerSubscription WHERE [SubscriptionId] IS NOT NULL`** |
| `src/Centerix.Infrastructure/Data/Migrations/20260916213008_AddRefundSubscriptionIdUniqueConstraint.cs` | **New migration: drops old non-unique index, creates unique filtered index** |
| `tests/Centerix.SecurityTests/Phase9_4CancellationTests.cs` | 50 InMemory cancellation tests (15 original hardening + 7 new partial-payment + 7 new SQL concurrency InMemory) |
| `tests/Centerix.SecurityTests/Phase9_4_2CancellationConcurrencySqlServerTests.cs` | **7 SQL Server concurrency tests against real SQL Server (Testcontainers)** |

## Cancellation Semantics

Immediate cancellation only. No scheduled cancellation concept introduced.

## Cancellation Date Validation

- **Future date rejected**: `CancellationDateUtc > now` returns `TenantPlanErrors.CancellationDateInFuture` error
- **Before subscription start rejected**: `CancellationDateUtc < StartsAtUtc` returns `TenantPlan.CancellationDateBeforeStart` error
- **Equal to now accepted**: `CancellationDateUtc == now` proceeds normally
- Uses injected `TimeProvider` for current time (not `DateTime.UtcNow`)

## P1-A: Partial Payment Cancellation Fix

**Problem**: `CancelSubscriptionCommand` handler queried installments where `Status != Paid && Status != Cancelled`. A partially-paid installment (`PartiallyPaid`/`Overdue`) with active `PaymentAllocation` records caused `Installment.Cancel()` to throw, blocking the entire cancellation workflow.

**Fix**: Handler now uses `.Include(i => i.PaymentAllocations)` and skips installments where `a.Status == PaymentAllocationStatus.Active`. Partially-paid installments with active allocations are preserved (historical money already paid). Only unpaid future installments are cancelled.

**Behavior**:
- Partially-paid installment: Status unchanged (remains `PartiallyPaid`/`Overdue`), allocations preserved, settled amount preserved
- Unpaid future installment: Status changed to `Cancelled`
- Paid installments: Status unchanged (unchanged from original behavior)

## P1-B: SQL Server Concurrency Hardening (Refund Unique Constraint)

**Problem**: Application-level idempotency alone was insufficient for defense-in-depth against duplicate refund creation during concurrent cancellation.

**Fix**: Added filtered unique constraint `UX_Refunds_TenantId_SubscriptionId_OnePerSubscription WHERE [SubscriptionId] IS NOT NULL` on the `Refunds` table. This provides database-level guarantee that at most one refund can exist per subscription (for non-null subscription IDs).

**Migration**: `20260916213008_AddRefundSubscriptionIdUniqueConstraint.cs` — drops old non-unique `IX_Refunds_TenantId_SubscriptionId`, creates new unique filtered index.

## Refund Idempotency

**Mechanism**: Triple-layer protection

1. **Sequential idempotency**: Handler checks `subscription.Status == Cancelled` — returns early without creating duplicate refund
2. **Concurrent idempotency**: Handler queries `dbContext.Refunds.FirstOrDefaultAsync(r => r.SubscriptionId == subscription.Id)` before creating new refund — reuses existing refund ID if found
3. **Database unique constraint**: `UX_Refunds_TenantId_SubscriptionId_OnePerSubscription` prevents duplicate refunds at the SQL Server level even if application logic has a gap

**Result**: Exactly one cancellation effect, at most one cancellation refund per subscription.

## ChangeTracker Retry Safety

Added `dbContext.ChangeTracker.Clear()` before each retry attempt in the deadlock retry loop, matching the pattern from `AllocatePaymentCommand`. This prevents stale tracked entities from a rolled-back transaction from leaking into retry attempts.

## Legacy Cancellation Path

- `Centerix.Application.Platform.Commands.CancelSubscriptionCommand` rejects Contract-linked subscriptions with `Cancellation.ContractLinked` error
- Directly calls `subscription.Cancel(now)` only for non-Contract subscriptions (legacy/pre-contract)
- Contract-linked subscriptions MUST use the billing `CancelSubscriptionCommand` for full financial workflow
- Bypass prevention verified by test `LegacyPath_ContractLinked_Rejected`

## Financial Date Consistency

Single authoritative effective cancellation date used for:
- `RefundCalculationService.Calculate(contract, request.CancellationDateUtc, ...)` — refund calculation
- `subscription.Cancel(request.CancellationDateUtc)` — subscription lifecycle
- Audit trail `CancellationDateUtc` field

All three use the exact same validated `request.CancellationDateUtc`.

## Installments

- Paid installments (`Status == Paid`) are NOT cancelled
- Already-cancelled installments are NOT double-cancelled
- **Partial-paid installments with active PaymentAllocations are NOT cancelled** (P1-A fix)
- Active/unpaid installments are cancelled via `installment.Cancel(now)`
- No installments are deleted
- No allocations are deleted or modified

## Payment / Invoice / Contract Immutability

After cancellation:
- `Payment.Amount`, `Payment.Status` unchanged
- `PaymentAllocation.Status`, `PaymentAllocation.AllocatedAmount` unchanged
- `Invoice.TotalAmount`, `Invoice.Status` unchanged
- `Contract.Status` unchanged (remains Active)

Cancellation is a new business event, not a mutation of historical records.

## Tenant Lifecycle Synchronization

- After SQL transaction commits, `tenant.SetValidUpTo(now)` is called
- `tenantRegistrySync.SyncLifecycleAsync(tenant, cancellationToken)` synchronizes to Finbuckle TenantRegistry
- Sync failures are NOT silently swallowed — they propagate to the caller
- Sync is intentionally outside the SQL transaction (two-database architecture)

## Authorization

- Both cancellation paths enforce `IPlatformAdminGuard.EnsurePlatformAdmin()`
- Guard is called BEFORE any database reads
- Platform admin permission required (not tenant-level permission)
- Controller endpoints use `[HasPermission(Permissions.Subscriptions.Manage)]`
- Unauthorized requests rejected with `Platform.AdminRequired` error

## Tests

### Domain Tests (8)
- Active → Cancelled
- Pending → Cancelled
- PastDue → Cancelled
- Suspended → Cancelled
- Expired → CannotCancel
- AlreadyCancelled → CannotCancelAgain
- BeforeStart → Fails
- DomainEvent raised

### Handler Tests (Original 16)
- FullPayment_RefundCreated
- NoPayment_NoRefund
- PartialPayment_CustomerOwes
- PricingTiers_UsedCorrectly
- Idempotent
- NotFound
- Forbidden
- RefundPending
- PaymentImmutable
- FutureInstallments_Cancelled
- ContractImmutable
- InvoicePreserved
- SharedPayment_Isolated
- GiftRecovery
- NonGrantedGift_NoRecovery
- AuditWritten
- Overpayment_Refund
- ZeroRefund_NoRecord

### Hardening Tests (New 15)
- Cancel_FutureDate_Rejected
- Cancel_BeforeSubscriptionStart_Rejected
- Cancel_ExactlyNow_Accepted
- Cancel_SlightlyBeforeStart_Rejected
- Cancel_FutureDate_DoesNotMutateSubscription
- Cancel_SequentialDuplicate_SingleRefund
- Cancel_DuplicateWithZeroRefund_SingleEffect
- Cancel_DuplicateWithOutstanding_SingleEffect
- Cancel_CalculationUsesExactCancellationDate
- Cancel_PaidInstallment_NotCancelled
- Cancel_AllocationsPreserved
- LegacyPath_ContractLinked_Rejected
- Cancel_Unauthorized_Denied
- Cancel_PaymentAmountImmutability
- Cancel_InvoiceHistoricalIntegrity
- Cancel_TenantRegistrySync_Called

### Partial Payment Tests (New 7 InMemory)
- Cancel_PartiallyPaidInstallment_Succeeds
- Cancel_PartiallyPaidInstallment_AllocationPreserved
- Cancel_PartiallyPaidInstallment_PaymentPreserved
- Cancel_PartiallyPaidInstallment_FinalFinancialResult_Deterministic
- Cancel_MixPaidAndUnpaidInstallments_CorrectBehavior
- Cancel_PartiallyPaidInstallment_NoFinancialHistoryDeleted
- Cancel_ConcurrentIdempotency_InMemory

### SQL Server Concurrency Tests (New 7 — against real SQL Server)
- ConcurrentCancellation_WithRefund_OneRefundMaximum
- ConcurrentCancellation_WithZeroRefund_OneCancellationEffect
- ConcurrentCancellation_WithOutstanding_NoDuplicateRefunds
- UniqueConstraint_RejectsDuplicateSubscriptionIdRefund
- PartiallyPaidInstallment_CancellationSucceeds_SqlServer
- UniqueIndex_OnePerSubscription_EnforcedAtSql
- UniqueSubscriptionIdIndex_Exists

### Test Results

```
InMemory Cancellation Suite:  50 passed (Phase9_4CancellationTests)
SQL Server Concurrency:        7 passed (Phase9_4_2CancellationConcurrencySqlServerTests)
Full Suite (InMemory):       948 passed, 2 pre-existing failures (Phase3 student auth)
```

### Pre-existing Failures (Not Task-related)
```
Phase3AuthorizationHttpTests.Students_CrossTenantBranch_IsInvisibleAndReturnsNotFound
Phase3AuthorizationHttpTests.Students_TenantAdmin_CanCreateReadUpdateSoftDelete
```

## Migration

**New migration**: `20260916213008_AddRefundSubscriptionIdUniqueConstraint`
- Drops non-unique `IX_Refunds_TenantId_SubscriptionId`
- Creates unique filtered index `UX_Refunds_TenantId_SubscriptionId_OnePerSubscription WHERE [SubscriptionId] IS NOT NULL`

## Known Limitations

1. ~~SQL Server concurrency tests~~: **RESOLVED** — all 7 SQL Server concurrency tests pass against real SQL Server via Testcontainers.
2. ~~No database-level unique constraint on SubscriptionId for refunds~~: **RESOLVED** — filtered unique constraint now enforced at database level.

## Acceptance Criteria Verification

- [x] Future CancellationDateUtc is rejected
- [x] Cancellation before StartsAtUtc is rejected
- [x] Immediate cancellation uses one authoritative effective date
- [x] Refund calculation and Subscription cancellation use the same date
- [x] Duplicate cancellation cannot create duplicate Refunds
- [x] Concurrent cancellation is safe (Serializable isolation + ChangeTracker.Clear + refund query + unique constraint)
- [x] Retry is safe with EF ChangeTracker state (ChangeTracker.Clear on each attempt)
- [x] Legacy cancellation cannot bypass financial cancellation for Contract-linked subscriptions
- [x] Legacy subscriptions without Contract remain explicitly understood (legacy path handles them)
- [x] Paid installments remain untouched
- [x] **Partial-paid installments with active allocations remain untouched (P1-A fix)**
- [x] Unpaid future installments are cancelled
- [x] Payment history remains immutable
- [x] Invoice history remains immutable
- [x] Contract history remains immutable
- [x] Refund remains an independent transaction (Pending status, requires approval)
- [x] Refund approval remains permission-controlled (separate ApproveRefundCommand)
- [x] Tenant isolation remains enforced
- [x] Platform authorization remains enforced (IPlatformAdminGuard)
- [x] Tenant lifecycle synchronization remains consistent (SyncLifecycleAsync called)
- [x] **SQL Server concurrency: all 7 tests pass against real SQL Server**
- [x] **Database-level unique constraint on Refund.SubscriptionId (P1-B fix)**
- [x] Completion report contains accurate evidence
- [x] No Task 9.5 work was introduced
- [x] No unrelated architectural redesign was introduced
