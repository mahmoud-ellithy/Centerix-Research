# Task 13/13.1/13.1.1 — Refund Settlement & Payment Source Allocation

## Task 13.1.1 Status: COMPLETE

## Final Implementation SHA
411c9226580f2ec79e2ae56118f09f9ad144b3f6

## Files Changed (cumulative across 13, 13.1, 13.1.1)

- src/Centerix.Domain/Platform/Billing/Refunds/RefundAllocation.cs
- src/Centerix.Domain/Platform/Billing/Refunds/RefundErrors.cs
- src/Centerix.Domain/Platform/Billing/Refunds/Refund.cs
- src/Centerix.Application/Platform/Billing/Commands/CreateRefundCommand.cs
- src/Centerix.Application/Platform/Billing/Commands/ExecuteRefundCommand.cs
- src/Centerix.Infrastructure/Data/Configurations/RefundAllocationConfiguration.cs
- src/Centerix.Infrastructure/Data/Configurations/RefundConfiguration.cs
- src/Centerix.API/Controllers/RefundsController.cs
- tests/Centerix.SecurityTests/Phase13RefundAllocationTests.cs
- tests/Centerix.SecurityTests/Phase13RefundAllocationSqlServerTests.cs
- docs/TASK-13-REFUND-SETTLEMENT-REPORT.md

---

## Task 13.1.1 — PaymentMethod Authority & Strict Idempotency

### 1. Payment Method Authority

The authoritative payment method is derived from the Payment entity, not from the caller-supplied RefundAllocation snapshot:

```
RefundAllocation.PaymentId
        ↓
Payment.Method
```

At execution time, ExecuteRefundHandler verifies:

```
RefundAllocation.PaymentMethod == Payment.Method.ToString()
```

If a caller creates a RefundAllocation with a PaymentMethod that does not match the authoritative Payment.Method, execution fails with:

```
Refund.PaymentMethodMismatch
```

**Tampering Protection Test (`TamperedPaymentMethod_Rejected`):**
- Payment created with `PaymentMethod.Cash`
- RefundAllocation deliberately created with `PaymentMethod.InstaPay`
- Execution attempted via `ExecuteRefundHandler`
- Expected: execution rejected, refund remains Pending, no financial settlement
- Actual: PASS

### 2. Strict Idempotency (Same Key / Different Payload)

When two concurrent requests use the **same IdempotencyKey** but target **different Refunds** (different payload), the result must be deterministic:

```
1 Success
1 IdempotencyKeyConflict
0 Unexpected
```

`Refund.ExecutionConcurrencyConflict` is **NOT** accepted as the semantic result for this scenario.

**Implementation:** After a `DbUpdateConcurrencyException` or deadlock, the handler re-reads the persisted Refund state and resolves the semantic result as `AllocationIdempotencyKeyConflict` when the existing Refund already carries the same IdempotencyKey. This ensures the idempotency owner is determined from persisted state, not from in-memory assumptions.

### 3. Same Key / Same Refund (Idempotent Retry)

When the **same Refund** is executed twice with the **same IdempotencyKey**:

```
One financial execution
Second request is idempotent (returns success)
One RefundSettlement ledger entry
One RefundAllocation record
```

### 4. Existing Financial Invariants (Preserved)

All invariants from Task 13 and 13.1 are preserved:

- **Payment refundable balance:** `Payment.Amount - SUM(RefundAllocations for OTHER refunds in Processing/Completed status)`
- **RefundAllocation total <= Payment.Amount:** Enforced at execution time via lock + balance check
- **Completed-payment-only source:** Only `PaymentStatus.Completed` payments can be refund sources
- **Same tenant:** Cross-tenant allocation rejected
- **Same currency:** Cross-currency allocation rejected (`Refund.CurrencyMismatch`)
- **Historical Payment immutability:** Payment.Amount, Payment.Status cannot change after completion
- **Invoice.TotalAmount immutability:** Invoice amounts are read-only after issue
- **PaymentAllocation immutability:** Allocation amounts cannot change after creation
- **No duplicate financial settlement:** Unique constraint `UX_CustomerLedgerEntries_SettlementByRefund`
- **SQL Server concurrency protection:** Serializable isolation + UPDLOCK + deadlock retry

---

## Exact Financial Invariant Implemented

```
Refundable Payment Amount
  =
  Completed Payment Amount
  -
  SUM(RefundAllocation.Amount for OTHER refunds in Processing or Completed status)
```

For every completed payment:
```
TotalRefundAllocations (from Processing/Completed refunds)
    <=
Payment.Amount
```

and:
```
NewRefundAllocation.Amount
    <=
Payment.Amount - ExistingRefundedAmount
```

## Concurrency Strategy

- Serializable isolation level for all financial transactions
- UPDLOCK + ROWLOCK + HOLDLOCK on Payment reads to serialize concurrent refund source consumption
- ChangeTracker.Clear() before each deadlock retry
- Bounded exponential backoff (3 retries: 50ms, 100ms, 200ms)
- Re-read persisted idempotency owner after concurrency/deadlock to resolve semantic result

## Idempotency

ExecuteRefundCommand accepts an explicit IdempotencyKey parameter:
- Stored on the Refund entity during first execution
- Same key + same Refund = idempotent success
- Same key + different Refund = IdempotencyKeyConflict
- Unique filtered index UX_Refunds_TenantId_IdempotencyKey enforces at DB level
- Concurrency/deadlock handlers re-read persisted state to determine correct semantic outcome

## SQL Server Concurrency Test Results

### Phase 13.1.1 Tests

| Scenario | Required Result | Actual Result |
|----------|----------------|---------------|
| Tampered PaymentMethod (Cash vs InstaPay) | Rejected with Refund.PaymentMethodMismatch, refund remains Pending | PASS |
| Same key, different payload | Exactly 1 success + 1 IdempotencyKeyConflict + 0 unexpected | PASS |

### Phase 13.1 Tests (preserved)

| Scenario | Required Result | Actual Result |
|----------|----------------|---------------|
| Two refunds, same payment (700+700 against 1000) | 1 success + 1 InsufficientPaymentSource + 0 unexpected | PASS |
| Same refund, same idempotency key | 1 financial execution + 1 idempotent success + 0 unexpected | PASS |
| Same key, different payload | 1 success + 1 IdempotencyKeyConflict + 0 unexpected | PASS |
| Multiple payment sources | Allocation total equals refund amount | PASS |

## Post-Concurrency Financial Invariants

After every concurrency test:
- SUM(RefundAllocation.Amount per Payment from Completed refunds) <= Payment.Amount
- Exactly 1 ledger settlement for the winning refund
- No duplicate ledger settlements
- Payment.Amount unchanged
- Payment.Status unchanged
- PaymentAllocation.Amount unchanged
- Invoice.TotalAmount unchanged

## Test Matrix

### InMemory Tests (22 total)
- 6 domain entity tests
- 9 handler tests (CreateRefund allocation + ExecuteRefund validation)
- 3 financial invariant tests
- 3 historical integrity tests
- 1 authorization test

### SQL Server Tests (5 total)
- CompetingRefunds_SamePayment_ExactOneSuccessOneInsufficient
- SameRefund_SameIdempotencyKey_ExactOneFinancialExecution
- SameKey_DifferentPayload_ExactOneSuccessOneConflict
- MultiplePaymentRefundAllocation_SumsCorrectly
- TamperedPaymentMethod_Rejected (Task 13.1.1)

## Full Regression Results

- 1155 InMemory tests: PASS
- 5 Phase13 SQL Server tests: PASS
- 6 Phase12_1 SQL Server tests: PASS
- 2 Phase10_1 SQL Server tests: PASS
- Total: 1168 tests, 0 failures (1 pre-existing Phase9 failure excluded)

---

TASK 13.1.1 — CLOSED
