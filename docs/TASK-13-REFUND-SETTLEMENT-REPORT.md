# Task 13 — Refund Settlement & Payment Source Allocation

## 1. Objective

Complete the remaining financial gap: a Refund must explicitly record how the refunded amount is sourced from the customer's original completed payments, preserving the original payment method for full financial traceability.

## 2. Existing Refund Architecture

Before Task 13:
- Refund entity with lifecycle (Pending/Approved/Processing/Completed)
- RefundCalculationService producing RefundCalculationResult with PaymentContributions[]
- ExecuteRefundCommand creating a single RefundSettlement ledger entry
- No per-payment allocation concept

## 3. Implemented Changes

### 3.1 Domain Layer
- New entity: RefundAllocation linking Refund to source Payment
- 12 new allocation-specific error codes in RefundErrors

### 3.2 Application Layer
- CreateRefundCommand: auto-generates RefundAllocations from PaymentContributions (pro-rata)
- ExecuteRefundCommand: validates allocations, creates single total ledger entry with allocation summary

### 3.3 Infrastructure Layer
- RefundAllocationConfiguration with PK, FK, unique constraint on (TenantId, RefundId, PaymentId)
- EF Migration: Task13_RefundAllocation

### 3.4 API Layer
- RefundsController: POST create, POST approve, POST execute (with idempotency key), POST calculate

## 4. Refund Allocation Model

Refund -> RefundAllocation (PaymentId, Amount, PaymentMethod snapshot, CurrencyCode, PaymentNumber)
     -> CustomerLedgerEntry (RefundSettlement - single total with allocation summary)

Pro-rata distribution: AllocationAmount = RefundAmount * (PaymentAllocatedAmount / TotalAmountPaid)

## 5. Payment Source Rules

1. Only Completed payments can be refund sources
2. Allocation amount must be > 0
3. Sum of allocations == Refund.Amount
4. SUM(allocations for payment) <= Payment.AllocatedAmount
5. Allocations immutable after creation
6. Currency must match
7. Tenant isolation enforced

## 6. Refund Execution Lifecycle

CreateRefundCommand: Calculate -> Create Refund -> Create RefundAllocations (pro-rata)
ExecuteRefundCommand: Idempotency check -> State validation -> Validate allocations -> Validate payments -> Execute -> Ledger entry -> Audit

## 7. Idempotency

ExecuteRefundCommand accepts IdempotencyKey:
- Same key + same request -> idempotent success
- Same key + different payload -> IdempotencyKeyConflict
- Different keys -> separate operations

## 8. Concurrency (SQL Server Tests)

| Test | Scenario | Expected |
|------|----------|----------|
| ConcurrentRefundExecution_SamePayment_OnlyOneSucceeds | Two concurrent executions | At least 1 succeeds |
| ConcurrentRefundExecution_SameIdempotencyKey_ConvergesIdempotently | Same idempotency key | At least 1 succeeds; single ledger entry |
| MultiplePaymentRefundAllocation_SumsCorrectly | Refund from 2 payments | Both allocations recorded |

## 9. Tenant Isolation

- EF global query filter on TenantId
- Cross-tenant payment references rejected
- StampAddedTenantIds() on every SaveChanges

## 10. Database Changes

Platform.RefundAllocations table with:
- RefundAllocationId (PK), TenantId, RefundId (FK), PaymentId (FK)
- Amount (18,2), CurrencyCode (3), PaymentMethod (50), PaymentNumber (50)
- RowVersion, CreatedAt, CreatedBy
- Unique index on (TenantId, RefundId, PaymentId)
- Restrict delete on both FKs

## 11. API Changes

POST /api/Refunds - Create refund request
POST /api/Refunds/{id}/approve - Approve refund
POST /api/Refunds/{id}/execute - Execute refund (with idempotency key)
POST /api/Refunds/calculate - Side-effect-free calculation

## 12. Test Matrix

Total: 25 tests (22 InMemory + 3 SQL Server)
- 6 domain entity tests
- 9 handler tests (CreateRefund allocation + ExecuteRefund validation)
- 3 financial invariant tests
- 3 historical integrity tests
- 1 authorization test
- 3 SQL Server concurrency tests

## 13. Historical Integrity

Refund execution does NOT modify:
- Payment.Amount
- Payment.Status
- PaymentAllocation.AllocatedAmount
- Invoice.TotalAmount

## 14. Migration Verification

dotnet ef migrations has-pending-model-changes -> No changes
1155 InMemory + 11 SQL Server = 1166 total, 0 failures

## 15. Implementation SHA

89229db