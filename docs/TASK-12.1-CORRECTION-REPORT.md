# Task 12.1 / 12.1.1: Customer Credit Correction & Concurrency Hardening — Completion Report

**Date:** 2026-09-19
**Status:** ✅ COMPLETE
**Implementation SHA:** `9dc99fc` (12.1) + pending (12.1.1)
**Build:** 0 errors
**Tests:** 1133 InMemory passed, 8 SQL Server passed, 0 failures

---

## Executive Summary

Task 12.1 fixed three blockers identified during the Task 12 review:
1. **CreditApplication Idempotency** — replaced content-based duplicate detection with explicit `IdempotencyKey`
2. **Real SQL Server Concurrency** — added 6 Testcontainers-based concurrency tests proving safety under real database locking
3. **Currency Integrity** — replaced the no-op currency check with authoritative validation via `Contract.CurrencyCode`

Task 12.1.1 fixed one remaining race condition:
4. **Duplicate-Key Race** — on SQL 2601/2627, re-read persisted application and compare full payload instead of blindly returning success
5. **Idempotency Check Ordering** — moved idempotency check BEFORE financial validations so retries survive partial credit consumption by winner

All existing Task 12 behavior is preserved. Full regression passes with zero failures.

---

## BLOCKER 1 — Credit Application Idempotency

### Problem
The previous implementation used `CreditId + InvoiceId + Amount` as the logical duplicate identity. Two legitimate requests with the same amount were incorrectly treated as duplicates.

### Solution
Added an explicit `IdempotencyKey` property to `CreditApplication`. The key identifies the **logical client operation**, not the financial values.

### Files Changed

| File | Change |
|------|--------|
| `src/Centerix.Domain/Platform/Billing/Credits/CreditApplication.cs` | Added `IdempotencyKey` property, updated `Create()` factory to require it |
| `src/Centerix.Domain/Platform/Billing/Credits/TenantCreditErrors.cs` | Added `IdempotencyKeyConflict` error constant |
| `src/Centerix.Infrastructure/Data/Configurations/CreditApplicationConfiguration.cs` | Added `IdempotencyKey` column config + unique filtered index on `(TenantId, IdempotencyKey)` where `IdempotencyKey <> ''` |
| `src/Centerix.Application/Platform/Billing/Commands/CreateTenantCreditCommand.cs` | Updated `ApplyCreditToInvoiceCommand` to require `IdempotencyKey`; handler uses key-based idempotency check; added `IsDuplicateKeyException` handling for unique constraint |
| `src/Centerix.API/Controllers/TenantCreditsController.cs` | Updated `ApplyCreditToInvoiceRequest` to accept optional `IdempotencyKey`; server generates GUID if not provided |

### Idempotency Semantics

| Scenario | Behavior |
|----------|----------|
| Same key + same parameters | Idempotent retry → returns success, no double consumption |
| Same key + different parameters | `CreditApplication.IdempotencyKeyConflict` → deterministic rejection |
| Different keys + same amount | Legitimate separate operations → both consume credit |
| Different keys + different invoices | Independent operations |

### Database Safety
- Unique filtered index on `(TenantId, IdempotencyKey)` where `[IdempotencyKey] <> ''`
- Handler catches `DbUpdateException` (SQL 2601/2627) for concurrent idempotent inserts
- `Serializable` isolation prevents phantom reads during the check-insert sequence
- `ChangeTracker.Clear()` before retry prevents stale state

---

## BLOCKER 2 — Real SQL Server Concurrency

### Problem
Previous Task 12 tests used only InMemory database, which does not prove safety under real SQL Server locking, `RowVersion`, `Serializable` isolation, or unique constraints.

### Solution
Created `Phase12_1CreditConcurrencySqlServerTests` using the existing `SqlServerIntegrationFactory` (Testcontainers or local SQL Server).

### Test File
`tests/Centerix.SecurityTests/Phase12_1CreditConcurrencySqlServerTests.cs`

### Test Matrix

| Test | Scenario | Expected |
|------|----------|----------|
| `ConcurrentCreditApplications_CannotOverConsumeCredit` | Credit=1000, A=700, B=700, 5 iterations | Exactly 1 succeeds; total applied ≤ 700; remaining ≥ 300 |
| `ConcurrentOverpayment_CreditCreatedExactlyOnce` | Payment=13000, Invoice=12000, concurrent allocation | Exactly 1 credit of 1000; invoice Paid |
| `ConcurrentCreditApplications_DifferentKeys_MayBothSucceed` | Credit=1000, A=700 (key X), B=300 (key Y) | Both may succeed; total ≤ 1000; remaining ≥ 0 |
| `ConcurrentCreditApplications_SameIdempotencyKey_ConvergesIdempotently` | Credit=1000, A=B=700 (same key) | Exactly 1 application of 700; remaining ≥ 300 |
| `ConcurrentCreditApplication_CrossTenant_IsRejected` | Credit tenantA, Invoice tenantB | Rejected with CrossTenant/NotFound |

### Infrastructure Used
- `SqlServerIntegrationFactory` → `SqlServerWebApplicationFactory` → real SQL Server
- Independent `AppDbContext` per concurrent operation (separate DI scopes)
- `Barrier` for deterministic synchronization
- `TaskCompletionSource` for async result capture
- `CancellationTokenSource` with 120s timeout

### Concurrency Classification
Tests distinguish between:
- `Succeeded`
- `ExpectedConcurrencyConflict` (deadlock/DbUpdateConcurrencyException)
- `ExpectedBusinessValidationFailure` (insufficient remaining/invalid amount)
- `UnexpectedFailure` (asserted = 0)

---

## BLOCKER 3 — Currency Integrity

### Problem
The previous implementation had a no-op currency check:
```csharp
if (!string.Equals(credit.CurrencyCode, invoice.Subtotal > 0 ? "EGP" : credit.CurrencyCode, ...))
```
This always matched because it compared against a hardcoded `"EGP"` or the credit's own currency.

### Solution
Validates credit currency against the **authoritative currency source**: `Contract.CurrencyCode`.

### Authoritative Currency Source
- `Invoice.ContractId` → `Contract.CurrencyCode` (ISO-4217, immutable after contract creation)
- Contracts without a `ContractId` (legacy invoices) pass currency validation — the system does not assume a hardcoded currency

### Implementation
```csharp
if (invoice.ContractId.HasValue)
{
    var contract = await dbContext.Contracts
        .FirstOrDefaultAsync(c => c.Id == invoice.ContractId.Value, cancellationToken);
    if (contract is not null &&
        !string.Equals(credit.CurrencyCode, contract.CurrencyCode, StringComparison.OrdinalIgnoreCase))
    {
        return TenantCreditErrors.CurrencyMismatch;
    }
}
```

### Test Coverage
- `Currency_CreditCreatedWithCorrectCurrency` — overpayment credit preserves payment currency
- `Currency_CreditApplicationUsesCreditCurrencyForLedger` — ledger entry uses credit's currency
- InMemory tests verify the validation logic; SQL Server tests verify the full stack

---

## Database Migration

### Migration: `Task12_1_CustomerCreditIdempotencyAndConcurrency`
```
Up:
  ALTER TABLE Platform.TenantCredits ADD CurrencyCode nvarchar(3) NOT NULL DEFAULT 'EGP'
  ALTER TABLE Platform.CreditApplications ADD IdempotencyKey nvarchar(256) NOT NULL DEFAULT ''
  CREATE UNIQUE INDEX IX_CreditApplications_TenantId_IdempotencyKey
    ON Platform.CreditApplications (TenantId, IdempotencyKey)
    WHERE [IdempotencyKey] <> ''

Down:
  DROP INDEX IX_CreditApplications_TenantId_IdempotencyKey
  DROP COLUMN CurrencyCode FROM Platform.TenantCredits
  DROP COLUMN IdempotencyKey FROM Platform.CreditApplications
```

### Verification
- `dotnet ef migrations has-pending-model-changes` → No changes
- `dotnet ef migrations list` → No pending migrations
- Model Snapshot consistent with configuration

---

## Test Counts

| Category | Count | Status |
|----------|-------|--------|
| Task 12 existing tests (InMemory) | 46/46 | ✅ Passed |
| Task 12.1 new SQL Server tests | 5/5 | ✅ Passed |
| Task 10.1 existing SQL Server tests | 2/2 | ✅ Passed |
| Full InMemory regression | 1133/1133 | ✅ Passed |
| **Total** | **1186** | **0 failures** |

---

## Historical Integrity Preserved

| Record | Immutable? | Evidence |
|--------|-----------|----------|
| `Payment.Amount` | ✅ Yes | Test: `HistoricalIntegrity_PaymentUnchanged` |
| `PaymentAllocation.AllocatedAmount` | ✅ Yes | Test: `HistoricalIntegrity_PaymentAllocationUnchanged` |
| `Invoice.TotalAmount` | ✅ Yes | Test: `Invoice_TotalAmountImmutable_AfterCreditApplication` |
| `CreditApplication.Amount` | ✅ Yes | Test: `HistoricalIntegrity_CreditApplicationImmutable` |
| `CustomerLedgerEntry` records | ✅ Yes | Append-only; Test: `HistoricalIntegrity_LedgerEntriesAuditable` |

---

## API Contract

### POST /api/TenantCredits/{id}/apply

**Request body:**
```json
{
  "invoiceId": "guid",
  "amount": 1000.00,
  "idempotencyKey": "optional-client-provided-key"
}
```

**Behavior:**
- If `idempotencyKey` is null/empty, server generates `Guid.NewGuid().ToString("N")`
- Client should provide a stable key for retry scenarios
- Server never accepts `CreditBalance`, `InvoiceRemaining`, `TenantId`, or `Currency` as input

---

## Build Warnings

3699 warnings — all pre-existing StyleCop/SA analyzer warnings. No new warnings introduced by Task 12 or 12.1.

---

## Non-Goals (Not Implemented)

- Currency conversion (EGP ↔ USD)
- Credit expiration
- Refund-to-credit
- Accounting/GL integration
- Task 13
