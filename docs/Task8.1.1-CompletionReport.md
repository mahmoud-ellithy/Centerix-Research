# Task 8.1.1 — Installment Financial Integrity: Idempotency & Single Source of Truth

## Implementation

### Files Changed

| File | Change |
|------|--------|
| `src/Centerix.Application/Platform/Billing/Commands/AllocatePaymentCommand.cs` | Moved idempotency check BEFORE financial capacity validations. Idempotency query now includes `InstallmentId`. |
| `src/Centerix.Domain/Platform/Billing/Installments/Installment.cs` | `ApplyAllocation` now derives `SettledAmount` from allocation sum (single source of truth). Added `SynchronizeSettledAmount()`. Added `ReverseAllocation()` for allocation reversal. Added EF Core double-add guard. |
| `tests/Centerix.SecurityTests/Phase8_1_1FinancialIntegrityTests.cs` | New file: 15 regression tests covering idempotency (Tests 1-4, 13), settlement (Tests 5-9), concurrent idempotency (SQL Server), and additional integrity checks. |
| `tests/Centerix.SecurityTests/Phase9FinancialLedgerHardeningTests.cs` | Updated 2 tests to reflect correct idempotency behavior (identical retry now succeeds). |

### Behavior Changes

#### Idempotency Algorithm

The `AllocatePaymentHandler` now follows this exact processing order:

1. **Tenant/security context** — Load Payment + Invoice, validate isolation
2. **Detect exact existing allocation** — Query by `(PaymentId, InvoiceId, InstallmentId, AllocatedAmount, Status=Active, TenantId)`
3. **If exact allocation exists** — Return success/idempotent. No financial effect.
4. **Financial validations** — Payment capacity, Invoice capacity, Installment capacity
5. **Create allocation** — `PaymentAllocation.Create`, `Installment.ApplyAllocation`, `Invoice.UpdatePaymentStatus`, ledger entry
6. **Commit atomically** — Serializable transaction, deadlock retry

#### Settlement Source-of-Truth Design

`Installment.SettledAmount` is now always derived from `SUM(active PaymentAllocation.AllocatedAmount)` via `SynchronizeSettledAmount()`. The persisted column is retained for DB compatibility but is never independently authoritative. There is no legitimate code path where `SettledAmount != GetSettledAmount()` after a successful transaction.

The `ApplyAllocation` method uses an EF Core relationship fixup guard: if the allocation is already in `_paymentAllocations` (added by EF navigation fixup), it skips the duplicate add but still synchronizes the settled amount.

### Database

- **Migration**: None required. No schema changes.
- **Schema**: Unchanged. The existing `UX_PaymentAllocations_Idempotent` filtered unique index on `(TenantId, PaymentId, InvoiceId, InstallmentId, AllocatedAmount) WHERE Status = 'Active'` already provides the correct database-level idempotency protection.
- **Index/constraint changes**: None.
- **Model snapshot status**: Unchanged.

### Tests

```
Targeted Task 8.1.1 tests: 15 passed / 0 failed
Full suite (non-SQL Server): 687 passed / 2 failed (pre-existing Phase3AuthorizationHttpTests)
Build: PASS
EF model verification: PASS (no schema changes)
Migration verification: PASS (no pending migrations)
```

The 2 failures are pre-existing `Phase3AuthorizationHttpTests` (student/branch authorization, unrelated to billing).

### Invariants Verified

- [x] Exact duplicate PaymentAllocation retry is genuinely idempotent
- [x] Idempotency check cannot be blocked by capacity validation of the already-existing operation
- [x] Idempotency identity includes InstallmentId
- [x] Different installment is not treated as duplicate (Test 3)
- [x] Different amount is not treated as duplicate (Test 4)
- [x] Concurrent identical requests produce exactly one financial allocation (Test 13 + SQL Server)
- [x] Payment capacity remains protected (non-duplicate allocations still validated)
- [x] Invoice capacity remains protected
- [x] Installment capacity remains protected
- [x] Serializable/deadlock-retry behavior remains intact
- [x] Installment settlement has one authoritative source of truth (allocation sum)
- [x] Active allocation sum is authoritative
- [x] Inactive/reversed allocations are excluded (Test 6)
- [x] RemainingAmount = Amount - authoritative settlement
- [x] Status remains deterministic (Paid, PartiallyPaid, Overdue, Pending)
- [x] No client-controlled settlement fields
- [x] Existing refund/benefit behavior preserved
- [x] Required regression tests exist and exercise production logic
- [x] Migration/model state is valid
- [x] Build succeeds
- [x] Full test results reported honestly
- [x] No unrelated architectural changes introduced
