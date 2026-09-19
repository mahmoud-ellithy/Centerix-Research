# Task 13/13.1 — Refund Settlement & Payment Source Allocation

## Implementation SHA
ffe63aa

## Files Changed
- src/Centerix.Domain/Platform/Billing/Refunds/RefundAllocation.cs
- src/Centerix.Domain/Platform/Billing/Refunds/RefundErrors.cs
- src/Centerix.Domain/Platform/Billing/Refunds/Refund.cs
- src/Centerix.Application/Platform/Billing/Commands/CreateRefundCommand.cs
- src/Centerix.Application/Platform/Billing/Commands/ExecuteRefundCommand.cs
- src/Centerix.Infrastructure/Data/Configurations/RefundAllocationConfiguration.cs
- src/Centerix.Infrastructure/Data/Configurations/RefundAllocationConfiguration.cs
- src/Centerix.Infrastructure/Data/Configurations/RefundConfiguration.cs
- src/Centerix.API/Controllers/RefundsController.cs
- tests/Centerix.SecurityTests/Phase13RefundAllocationTests.cs
- tests/Centerix.SecurityTests/Phase13RefundAllocationSqlServerTests.cs
- docs/TASK-13-REFUND-SETTLEMENT-REPORT.md

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

## Idempotency

ExecuteRefundCommand accepts an explicit IdempotencyKey parameter:
- Stored on the Refund entity during first execution
- Same key + same Refund = idempotent success
- Same key + different Refund = IdempotencyKeyConflict
- Unique filtered index UX_Refunds_TenantId_IdempotencyKey enforces at DB level

## SQL Server Concurrency Test Results

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

### SQL Server Tests (4 total)
- CompetingRefunds_SamePayment_ExactOneSuccessOneInsufficient
- SameRefund_SameIdempotencyKey_ExactOneFinancialExecution
- SameKey_DifferentPayload_ExactOneSuccessOneConflict
- MultiplePaymentRefundAllocation_SumsCorrectly

## Full Regression Results

- 1155 InMemory tests: PASS
- 4 Phase13 SQL Server tests: PASS
- 6 Phase12_1 SQL Server tests: PASS
- 2 Phase10_1 SQL Server tests: PASS
- Total: 1167 tests, 0 failures
- EF migration check: No pending model changes
