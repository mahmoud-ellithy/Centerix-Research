# TASK 20.2 — Final Verification & Closure Plan

## 1. Summary

**Objective:** Verify all Task 20.1 blockers are resolved, execute full regression, and produce auditable evidence that Task 20 can be closed.

**Task 20.1 Status (from exploration):**
- JWT secret → FIXED (`"Secret": null` in both appsettings files)
- BillingCycle RowVersion → FIXED (migration + configuration)
- AllocatePayment IPlatformAdminGuard → FIXED (handler updated)
- Payment idempotency tests → PRESENT (6 tests, InMemory)
- Combined settlement concurrency → PRESENT (3 SQL Server tests)
- Test15 → SKIPPED (documented reason)
- Test22 → UNSKIPPED (passing)

## 2. Current State Analysis

### 2.1 Task 20.1 Fixes Already Verified by Exploration

| Item | Evidence |
|------|----------|
| JWT Secret removed | `appsettings.json` + `appsettings.Development.json` both have `"Secret": null` |
| BillingCycle RowVersion | `20260924073836_Task201_BillingCycleRowVersion` migration exists; `BillingCycleConfiguration.cs` lines 75-78 configure `IsRowVersion()` |
| AllocatePayment IPlatformAdminGuard | `AllocatePaymentHandler` line 25 injects `IPlatformAdminGuard`; line 42 calls `EnsurePlatformAdmin()` |
| Combined settlement tests | `Task201_CombinedSettlementConcurrencyTests.cs` exists with 3 SQL Server tests |
| Test22 unskipped | `Task18_5CreditEconomicOriginSqlServerTests.cs` line 1599 has no Skip attribute |
| Test15 skipped | Line 1163 has Skip attribute (documented reason) |

### 2.2 Known Issues from Task 20.1 Report

| ID | Issue | Severity | Current Status |
|----|-------|----------|----------------|
| GAP-01 | JWT secret committed | HIGH | **FIXED** — both files have `null` |
| GAP-03 | AllocatePayment unguarded | HIGH | **FIXED** — guard added |
| GAP-04 | BillingCycle no RowVersion | HIGH | **FIXED** — migration + config |
| API-01 | JWT secret in appsettings | HIGH | **FIXED** — nullified |
| GAP-06 | PlatformAdminGuard no test class | MEDIUM | **FIXED** — `Task201_PlatformAdminGuardTests.cs` exists |
| TEST-05 | Payment idempotency tests | TEST GAP | **FIXED** — `Task201_PaymentIdempotencyTests.cs` exists |

### 2.3 Remaining Concerns from Exploration

1. **Payment Idempotency Test Quality** — `Task201_PaymentIdempotencyTests.cs` line 82: `Assert.True(resultB.IsSuccess || !resultB.IsSuccess);` is a tautological assertion (always true). This needs fixing.

2. **Missing SQL Race Test for Payment Idempotency** — The task requires Case D (concurrent same-key requests) to run against SQL Server. Current tests use InMemory only.

3. **Weak Payment Tests** — Tests only verify basic handler behavior, not the actual idempotency guarantees under SQL Server constraints.

## 3. Proposed Changes

### 3.1 Fix Tautological Assertion (HIGH PRIORITY)

**File:** `tests/Centerix.SecurityTests/Task201_PaymentIdempotencyTests.cs`

**Change:** Replace line 82:
```csharp
// OLD (tautological - always true):
Assert.True(resultB.IsSuccess || !resultB.IsSuccess);

// NEW (validates deterministic conflict behavior):
Assert.False(resultB.IsSuccess, "Same key + different payload must return conflict, not success");
```

### 3.2 Add SQL Server Payment Idempotency Race Test (HIGH PRIORITY)

**File:** Create `tests/Centerix.SecurityTests/Task201_PaymentIdempotencySqlServerTests.cs`

**Content:** Add SQL Server race test for Case D (concurrent same-key same-payload requests).

### 3.3 Run Full Regression Suite (MANDATORY)

Run the following commands and record exact counts:

```bash
# Build
dotnet build Centerix.slnx --nologo -v minimal

# EF pending changes
dotnet ef migrations has-pending-model-changes --project src/Centerix.Infrastructure

# Full test suite
dotnet test tests/Centerix.SecurityTests/Centerix.SecurityTests.csproj --nologo -v minimal

# SQL Server tests separately (if available)
dotnet test tests/Centerix.SecurityTests/Centerix.SecurityTests.csproj --nologo -v minimal --filter "Category=SqlServer"
```

### 3.4 Verify Skipped Tests

Inspect `Task18_5CreditEconomicOriginSqlServerTests.cs` line 1163:
- Test15 — **MUST REMAIN SKIPPED** (documented reason: overlapping subscription scenario covered by Test16+)
- Confirm Test22 is UNSKIPPED and verify it passes

### 3.5 Update Final Report

Update `docs/TASK-20-FINAL-REGRESSION-SECURITY-VERIFICATION.md`:
- Remove stale GAP claims (GAP-01, GAP-03, GAP-04, API-01, GAP-06, TEST-05 are FIXED)
- Add fresh test execution evidence
- Document remaining technical debt items

## 4. Verification Steps

### 4.1 Build Verification
```
dotnet build Centerix.slnx --nologo -v minimal
```
Expected: 0 errors

### 4.2 EF Verification
```
dotnet ef migrations has-pending-model-changes --project src/Centerix.Infrastructure
```
Expected: No pending model changes

### 4.3 Full Regression Execution
```
dotnet test tests/Centerix.SecurityTests/Centerix.SecurityTests.csproj --nologo -v minimal
```
Expected: All tests pass (exact count to be recorded)

### 4.4 SQL Server Tests (if infrastructure available)
```
dotnet test tests/Centerix.SecurityTests/Centerix.SecurityTests.csproj --nologo -v minimal --filter "Category=SqlServer"
```
Expected: All SQL Server tests pass (exact count to be recorded)

## 5. Files to Modify

| File | Change |
|------|--------|
| `tests/Centerix.SecurityTests/Task201_PaymentIdempotencyTests.cs` | Fix tautological assertion on line 82 |
| `tests/Centerix.SecurityTests/Task201_PaymentIdempotencySqlServerTests.cs` | Create new SQL Server race test file |
| `docs/TASK-20-FINAL-REGRESSION-SECURITY-VERIFICATION.md` | Update with fresh evidence, close fixed GAPs |

## 6. Assumptions

1. SQL Server test infrastructure is available (Testcontainers or local SQL Server)
2. The Build and EF migration verification will pass
3. All SQL Server tests will pass after tautological assertion fix
4. Test15 skip reason is acceptable (covered by other tests)

## 7. Exit Criteria

| Criterion | Evidence Required |
|-----------|-------------------|
| Build succeeds | `dotnet build` output showing 0 errors |
| No pending EF changes | `dotnet ef migrations has-pending-model-changes` output |
| All tests pass | Exact pass/fail/skip counts recorded |
| Tautological assertion fixed | Code change + test still passes |
| SQL Server race test added | New test file exists and passes |
| Report updated | GAPs closed, fresh evidence recorded |

## 8. Execution Order

1. Fix tautological assertion in `Task201_PaymentIdempotencyTests.cs`
2. Create SQL Server race test file `Task201_PaymentIdempotencySqlServerTests.cs`
3. Run `dotnet build`
4. Run `dotnet ef migrations has-pending-model-changes`
5. Run full `dotnet test` suite
6. Update final report documentation
7. Final verdict: CLOSED or NOT CLOSED with remaining blockers
