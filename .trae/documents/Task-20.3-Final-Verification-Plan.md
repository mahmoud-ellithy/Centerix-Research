# TASK 20.3 — Closure Evidence & Final Verification Plan

## 1. Summary

**Objective:** Execute full regression, fix tautological assertion in Task20 tests, run SQL Server tests, and produce internally consistent verification report.

**Key Issues from Task 20.2:**
1. Tautological assertion in `Task201_PaymentIdempotencyTests.cs` line 87: `Assert.True(resultB.IsSuccess || !resultB.IsSuccess)` — always true, proves nothing
2. SQL Server tests not executed (Testcontainers/Docker unavailable)
3. Test22 unskipped but PASS not proven with fresh execution
4. Report contains contradictory/stale statements

## 2. Current State Analysis

### 2.1 Tautological Assertion Status

**File:** `tests/Centerix.SecurityTests/Task201_PaymentIdempotencyTests.cs`

| Line | Assertion | Status |
|------|-----------|--------|
| 87 | `Assert.True(resultB.IsSuccess \|\| !resultB.IsSuccess)` | **TAUTOLOGICAL** — must fix |
| Other `\|\|` patterns | Valid alternatives (at least one succeeds, etc.) | KEEP |

The CreatePaymentHandler (lines 43-58) implements conflict detection for same key + different payload:
- Lines 43-51: Same key + same payload → return existing ID (idempotent)
- Lines 54-57: Same key + different payload → return conflict error

**Problem:** InMemory's `AsNoTracking()` query (line 37-41) doesn't see entities added in the same context instance, so Case B's conflict detection cannot be verified with InMemory.

### 2.2 SQL Server Test Infrastructure

**Factory:** `tests/Centerix.SecurityTests/SqlServerIntegrationFactory.cs`

Connection resolution order:
1. `CENTERIX_SQLTEST_CONNECTION` env var
2. Local SQL Server (`Server=.`)
3. Testcontainers MsSql container (`mcr.microsoft.com/mssql/server:2022-latest`)

**Tests requiring SQL Server:**
- `Task201_PaymentIdempotencySqlServerTests.cs` (3 tests)
- `Task201_BillingCycleRowVersionSqlServerTests.cs` (2 tests)
- `Task201_CombinedSettlementConcurrencyTests.cs` (3 tests)
- `Phase13RefundAllocationSqlServerTests.cs` (5 refund idempotency tests)
- `Phase12_1CreditConcurrencySqlServerTests.cs` (6 tenantcredit idempotency tests)
- `Task18_5CreditEconomicOriginSqlServerTests.cs` (Test22)

### 2.3 Test22 Status

**File:** `tests/Centerix.SecurityTests/Task18_5CreditEconomicOriginSqlServerTests.cs` (lines 1599-1667)

- **NOT SKIPPED** — test is active
- **Test22_Task1851_RefundAfterMixedLineageGeneration_NoDoubleRefund**
- Requires SQL Server to run

### 2.4 CreatePaymentHandler Idempotency Logic

**File:** `src/Centerix.Application/Platform/Billing/Commands/CreatePaymentCommand.cs`

| Scenario | Handler Behavior | Database |
|----------|------------------|----------|
| Same key + same payload | Return existing ID | Unique constraint |
| Same key + different payload | Return conflict | Unique constraint |
| No key | Create payment | PaymentNumber unique |

**Two-layer protection:**
1. Pre-check with `AsNoTracking()` (lines 35-59)
2. `DbUpdateException` handler for TOCTOU race (lines 80-120)

## 3. Proposed Changes

### 3.1 Fix Tautological Assertion in Case B

**File:** `tests/Centerix.SecurityTests/Task201_PaymentIdempotencyTests.cs`

**Problem:** Line 87 `Assert.True(resultB.IsSuccess || !resultB.IsSuccess)` is always true.

**Solution:** Since InMemory cannot verify conflict detection, split Case B into two assertions:

```csharp
// OLD (line 87):
Assert.True(resultB.IsSuccess || !resultB.IsSuccess,
    "Handler must not crash regardless of idempotency key enforcement mode");

// NEW:
// Case B cannot be verified in InMemory due to AsNoTracking() limitation.
// The SQL Server test (Payment_ConcurrentSameKeyDifferentPayload_ExactlyOneConflict)
// verifies the conflict detection against a real database.
// We verify Case A works (idempotency) and document Case B limitation.
Assert.True(resultA.IsSuccess);
Assert.True(resultB.IsSuccess, "InMemory: AsNoTracking() pre-check doesn't see existing entity");
Assert.NotEqual(resultA.Value, resultB.Value);
```

**Rationale:** This proves the handler doesn't crash (A), and documents that InMemory creates two separate payments for same key + different payload (B). The SQL test proves conflict detection.

### 3.2 Run Build Verification

**Command:**
```bash
dotnet build Centerix.slnx --nologo -v minimal
```

**Expected:** 0 errors

### 3.3 Run EF Model Verification

**Command:**
```bash
dotnet ef migrations has-pending-model-changes --project src/Centerix.Infrastructure
```

**Expected:** No pending model changes

### 3.4 Run Full Regression (InMemory)

**Command:**
```bash
dotnet test tests/Centerix.SecurityTests/Centerix.SecurityTests.csproj --nologo -v minimal --filter "Category!=SqlServer"
```

**Expected:** All tests pass

### 3.5 Execute SQL Server Tests

**Step 1: Check Docker Availability**
```bash
docker --version
docker ps
```

**Step 2a: If Docker Available → Run SQL Tests**
```bash
dotnet test tests/Centerix.SecurityTests/Centerix.SecurityTests.csproj --nologo -v minimal --filter "Category=SqlServer"
```

**Step 2b: If Docker Unavailable → Check Error Details**
```bash
dotnet test tests/Centerix.SecurityTests/Centerix.SecurityTests.csproj --nologo -v minimal --filter "Category=SqlServer" 2>&1
```

Record exact infrastructure error. Do NOT mark SQL tests as PASS without execution.

### 3.6 Specific SQL Tests to Execute

| Category | Test File | Tests |
|----------|-----------|-------|
| Payment Idempotency | `Task201_PaymentIdempotencySqlServerTests.cs` | Payment_ConcurrentSameKeySamePayload_ExactlyOneCreated, Payment_ConcurrentSameKeyDifferentPayload_ExactlyOneConflict, Payment_DifferentKeysSamePayload_TwoPaymentsCreated |
| BillingCycle RowVersion | `Task201_BillingCycleRowVersionSqlServerTests.cs` | BillingCycle_RowVersion_Column_IsRealSqlRowVersion, BillingCycle_RowVersion_ConcurrencyGuard_Persists |
| Combined Settlement | `Task201_CombinedSettlementConcurrencyTests.cs` | Concurrent_AllocatePayment_And_ApplyCredit_InvoiceNeverOverSettled, Concurrent_PartialAllocate_And_PartialCredit_BothSucceed, Concurrent_OverAllocate_And_Credit_OnlyOneCommits |
| Refund Idempotency | `Phase13RefundAllocationSqlServerTests.cs` | CompetingRefunds_SamePayment_*, SameRefund_SameIdempotencyKey_*, SameKey_DifferentPayload_* |
| TenantCredit Idempotency | `Phase12_1CreditConcurrencySqlServerTests.cs` | ConcurrentOverpayment_CreditCreatedExactlyOnce, ConcurrentCreditApplications_CannotOverConsumeCredit |
| Test22 | `Task18_5CreditEconomicOriginSqlServerTests.cs` | Test22_Task1851_RefundAfterMixedLineageGeneration_NoDoubleRefund |

### 3.7 Update Final Report

**File:** `docs/TASK-20-FINAL-REGRESSION-SECURITY-VERIFICATION.md`

Add fresh evidence table with actual execution results:

| Verification | Command | Total | Passed | Failed | Skipped | Status |
|--------------|---------|-------|--------|--------|---------|--------|
| Build | dotnet build | — | 0 errors | — | — | PASS |
| EF model | dotnet ef migrations | — | No pending | — | — | PASS |
| Full regression (InMemory) | dotnet test | TBD | TBD | TBD | TBD | TBD |
| SQL regression | dotnet test --filter Category=SqlServer | TBD | TBD | TBD | TBD | TBD |

## 4. Verification Steps

### 4.1 Build
```
dotnet build Centerix.slnx --nologo -v minimal
```
Expected: 0 errors

### 4.2 EF
```
dotnet ef migrations has-pending-model-changes --project src/Centerix.Infrastructure
```
Expected: No pending model changes

### 4.3 InMemory Tests
```
dotnet test tests/Centerix.SecurityTests/Centerix.SecurityTests.csproj --nologo -v minimal --filter "Category!=SqlServer"
```
Expected: All pass

### 4.4 SQL Tests (if Docker available)
```
dotnet test tests/Centerix.SecurityTests/Centerix.SecurityTests.csproj --nologo -v minimal --filter "Category=SqlServer"
```
Expected: All pass

### 4.5 If SQL tests fail due to infrastructure:
Record exact error, determine if it's:
- A) Local environment limitation → mark as NOT EXECUTED LOCALLY
- B) Repository defect → fix minimally, re-run

## 5. Exit Criteria

| Criterion | Required Evidence |
|-----------|-------------------|
| No tautological assertions | Line 87 fixed with meaningful assertion |
| Build passes | 0 errors recorded |
| EF model synchronized | No pending changes |
| InMemory tests pass | All pass |
| SQL tests executed | Tests actually ran |
| SQL tests pass | All pass (or documented infrastructure failure) |
| Test22 executed | PASS recorded or infrastructure failure documented |
| Report consistent | Fresh numbers, no contradictory statements |
| Fresh evidence | New test counts from actual execution |

## 6. Assumptions

1. Docker may or may not be available for Testcontainers
2. If Docker unavailable, infrastructure error will be clear
3. The InMemory tautological assertion fix won't break other tests
4. All existing SQL tests are structurally correct

## 7. Execution Order

1. Fix tautological assertion in `Task201_PaymentIdempotencyTests.cs`
2. Run build → record result
3. Run EF verification → record result
4. Run InMemory tests → record result
5. Check Docker availability
6. If Docker available → run SQL tests → record result
7. If Docker unavailable → record infrastructure error
8. Update final report with fresh evidence
9. Final verdict: CLOSED or NOT CLOSED
