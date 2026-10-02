# TASK D — Free Months Benefit: Grant & Apply
## Verification Report

> **Current final HEAD**: `d8dd131e6b3c1741416d45bf71732e252594aa84` — `test(infrastructure): stabilize sql server integration regression`
> **Implementation correction SHA (historical)**: `3bf610f33dcea0ef286033be48465baee8a6c576` — `fix(billing): enforce active subscription for free months`
> **Original implementation SHA (historical)**: `78e38e23bf8518800cfd44ac6d053ee9d68a8f42` — `feat(billing): grant and apply free months benefit`

---

## 1. Scope

Task D implemented the fulfillment lifecycle for Free Months Benefits:

- **`GrantFreeMonthsBenefitCommand` / `GrantFreeMonthsBenefitHandler`**: Transitions `FulfillmentStatus` from `Pending → Granted`. Enforces eligibility, idempotency (already-granted is a no-op), tenant isolation via `IgnoreQueryFilters` + explicit check, and deadlock-retry serializable transactions.
- **`ApplyFreeMonthsBenefitToSubscriptionCommand` / `ApplyFreeMonthsBenefitToSubscriptionHandler`**: Transitions `FulfillmentStatus` from `Granted → AppliedToSubscription`. Atomically extends `EffectiveEndsAtUtc`, updates `AppliedFreeMonthsBenefitIds`, and marks fulfillment. Includes idempotency guard, deadlock-retry, and **active subscription enforcement**.
- **`TenantPlan.AppliedFreeMonthsBenefitIds`**: JSON column acting as the idempotency guard — prevents duplicate application.
- **`FreeMonthsBenefitErrors.ActiveSubscriptionNotFound`**: Returned when no Active + unexpired subscription exists for the Apply target.

---

## 2. Domain Behavior

### Grant State Transition

```
Pending + Eligible → Granted
```

- `Grant()` on `FreeMonthsBenefit` is idempotent: re-calling on an already-Granted benefit returns `Result.Updated` (not an error)
- Grant does NOT modify: `EligibilityStatus`, `EntitlementMonths`, `EligibilityRule`, `Contract.BonusMonths`
- `GrantFreeMonthsBenefitHandler` uses `IsolationLevel.Serializable` + EF Core transactions with up to 3 deadlock retries

### Apply State Transition

```
Granted → AppliedToSubscription
```

- `ApplyToSubscription()` on `FreeMonthsBenefit` is idempotent: re-calling on an already-Applied benefit returns `Result.Updated` (not an error)
- `TenantPlan.ApplyFreeMonthsBenefit(benefitId, entitlementMonths)` extends `EffectiveEndsAtUtc` by `entitlementMonths` calendar months; does NOT touch `BaseEndsAtUtc`, `ContractualMonthlyValue`, or `BonusMonths`
- `TenantPlan.AppliedFreeMonthsBenefitIds` is appended (list-replacement pattern for EF Core change detection)
- `ApplyFreeMonthsBenefitToSubscriptionHandler` uses `IsolationLevel.Serializable` + EF Core transactions with up to 3 deadlock retries

### Active Subscription Rule (Correction applied)

> Only a subscription satisfying **all three** conditions is a valid Apply target:
> - `Status == SubscriptionStatus.Active`
> - `EffectiveEndsAtUtc > DateTime.UtcNow` (not expired)
> - `TenantId` matches the authorized tenant
> - `ContractId` matches the benefit's contract

Expired, Cancelled, Suspended, PastDue, or Pending subscriptions are **not eligible** targets. The handler returns `FreeMonthsBenefitErrors.ActiveSubscriptionNotFound`.

### Fulfillment Lifecycle (Monotonic)

```
Pending → Granted → AppliedToSubscription
```

Forbidden: `Granted → Pending`, `AppliedToSubscription → Granted`, `AppliedToSubscription → Pending`

---

## 3. Idempotency

Three-layer idempotency:

1. **Domain**: `FreeMonthsBenefit.Grant()` returns `Result.Updated` when already Granted; `FreeMonthsBenefit.MarkAppliedToSubscription()` returns `Result.Updated` when already Applied
2. **Handler**: `GrantFreeMonthsBenefitHandler` checks `FulfillmentStatus` before granting; `ApplyFreeMonthsBenefitToSubscriptionHandler` checks `IsAlreadyApplied` before applying; `TenantPlan.ApplyFreeMonthsBenefit()` checks `AppliedFreeMonthsBenefitIds` before appending
3. **Entity**: `TenantPlan.ApplyFreeMonthsBenefit()` early-returns if `AppliedFreeMonthsBenefitIds` already contains the benefit ID

`AppliedFreeMonthsBenefitIds` is stored as a JSON array in `nvarchar(max)` with an EF value converter. The canonical empty representation is `[]` (set in migration defaultValue).

---

## 4. Concurrency

- `IsolationLevel.Serializable` on the EF Core transaction in both handlers
- Deadlock retry loop: up to 3 retries with exponential backoff (50ms × 2^attempt)
- The `Barrier` pattern in SQL-D05 verified that two concurrent Apply attempts result in exactly one effective application and exactly one benefit ID in `AppliedFreeMonthsBenefitIds`

---

## 5. Tenant Isolation

- Both handlers load benefits with `IgnoreQueryFilters()` to detect cross-tenant access (the global filter would silently return `NotFound` for another tenant's benefit)
- Explicit tenant check after load: `benefit.Contract.TenantId != tenantId` → `CrossTenantFreeMonthsBenefit` error
- EF query filters on `FreeMonthsBenefits`, `TenantPlans`, and `Contracts` prevent cross-tenant data leakage at the query layer
- `TaskCFakeCurrentTenant` (AsyncLocal-backed `ICurrentTenant`) is injected via `SqlServerWebApplicationFactory.ConfigureWebHost` to enable per-test tenant context

---

## 6. Authorization

- Both commands use `Permissions.Benefits.Manage` (already existing in the catalog)
- `AuthorizeAttribute` on the controller endpoints enforces the permission
- `GrantFreeMonthsBenefitCommand` and `ApplyFreeMonthsBenefitToSubscriptionCommand` both require the permission

---

## 7. SQL Server Environment

- **Local SQL Server** (`Server=.`) via `.\MSSQLLOCALDB` or default instance
- Connection: `Server=.;Trusted_Connection=True;TrustServerCertificate=True;Encrypt=False`
- Test database: uniquely named per test run (`CenterixSec_<guid>`), dropped after suite
- **No Docker / Testcontainers used** in this phase (local SQL Server was reachable)
- Database created via raw `CREATE DATABASE` SQL, then EF migrations applied

---

## 8. Test Results

### Task D Tests (35 total — all passing)

| Suite | Passed | Failed | Skipped |
|-------|--------|--------|---------|
| Domain Unit Tests (TaskD_FreeMonthsBenefitGrantApplyTests) | 17 | 0 | 0 |
| InMemory Application Tests (TaskD_FreeMonthsBenefitApplicationTests) | 11 | 0 | 0 |
| SQL Server Integration Tests (TaskD_FreeMonthsBenefitSqlServerTests) | 7 | 0 | 0 |
| **Task D Total** | **35** | **0** | **0** |

### Domain

Passed: 17
Failed: 0
Skipped: 0

### InMemory

Passed: 11
Failed: 0
Skipped: 0

### SQL Server

Passed: 7
Failed: 0
Skipped: 0

### SQL Server Tests (SQL-D01 through SQL-D07)

| Test | Description | Result |
|------|-------------|--------|
| SQL-D01 | Grant persists to SQL and reload confirms Granted | ✅ Pass |
| SQL-D02 | Apply persists and reload confirms AppliedToSubscription + subscription extended | ✅ Pass |
| SQL-D03 | Duplicate Apply → exactly one extension, benefit ID once | ✅ Pass |
| SQL-D04 | Cross-tenant application is rejected with CrossTenant error | ✅ Pass |
| SQL-D05 | Concurrent Apply → exactly one effective application | ✅ Pass |
| SQL-D06 | Full field round-trip through SQL persistence | ✅ Pass |
| SQL-D07 | Expired subscription cannot receive Free Months benefit | ✅ Pass |

### Full Solution Regression

**FULL SOLUTION REGRESSION — current final, post-infrastructure-fix result**

Command: `dotnet test Centerix.slnx --no-build --verbosity normal`

| Result | Count |
|--------|-------|
| Total | 1779 |
| Passed | 1778 |
| Failed | **0** |
| Skipped | 1 |
| Duration | 11.9043 Minutes |
| Exit code | **0** |

Docker/Testcontainers: Not used for this phase.

> **Historical pre-infrastructure-fix result (no longer the current state).**
> Before the SQL Server test-infrastructure fix at `d8dd131`, the same command produced
> `1779 total / 1619 passed / 159 failed / 1 skipped / exit code 1`. All 159 failures were
> caused by the test fake-tenant infrastructure (see §15.5 below), NOT by Task D production
> behavior. Zero Task D tests failed in that run either; every Task D test (35/35) has
> been green throughout.

---

## 9. EF Verification

### AppDbContext

```bash
dotnet ef migrations has-pending-model-changes --context AppDbContext --project src\Centerix.Infrastructure
```

**Result**: `No changes have been made to the model since the last migration.`

### TenantDbContext

```bash
dotnet ef migrations has-pending-model-changes --context TenantDbContext --project src\Centerix.Infrastructure
```

**Result**: `No changes have been made to the model since the last migration.`

Migration `AddAppliedFreeMonthsBenefitIds` (applied at `20260930131536`) creates the `nvarchar(max) AppliedFreeMonthsBenefitIds` column with `defaultValue: "[]"` (canonical empty JSON array). This was corrected from the original `""` to `"[]"` in this correction phase.

---

## 9.5 SQL Regression Infrastructure Fix (Historical Context)

The original `1619 passed / 159 failed` line-item above is **historical** and is no longer
the current state. The 159 failures were caused by the SQL Server **test-infrastructure
fake tenant** (`TaskCFakeCurrentTenant` in `tests/Centerix.SecurityTests/SqlServerIntegrationFactory.cs`),
not by any Task D production code.

### Root cause (historical)

`TaskCFakeCurrentTenant` did not expose the production-shaped `_authorizedTenantId` /
`_isAuthorized` instance fields that the existing reflection-based SQL integration test
helpers expected. Twenty-four `*SqlServerTests` classes configure the tenant via:

```csharp
type.GetField("_authorizedTenantId", BindingFlags.NonPublic | BindingFlags.Instance)!
    .SetValue(currentTenant, tenantId);
type.GetField("_isAuthorized", BindingFlags.NonPublic | BindingFlags.Instance)!
    .SetValue(currentTenant, true);
```

`GetField(...)` returned `null` on the fake's runtime type and `.SetValue(null, …)` threw
`NullReferenceException` at every call. **All 159 failures traced to that single NRE**.

### After fixing the NRE — 9 remaining (related second issue)

Once the instance fields were added, 9 HTTP-driven tests (`SqlServerInvitationFlowTests`
+ `Phase5TeachersConcurrencySqlServerTests.SalaryPayment_ConcurrentMarkPaid_…`) began
failing with HTTP 403. The fake did not read the Finbuckle-resolved tenant from the
HTTP `tenant` request header — `TenantGuardMiddleware` therefore queried
`TenantMemberships` filtered on the AsyncLocal/default tenant and found no match.

### Fix commit

`d8dd131e6b3c1741416d45bf71732e252594aa84` — `test(infrastructure): stabilize sql server integration regression`

Two precise edits, both inside `SqlServerIntegrationFactory.cs`, no test code touched:

1. Added `_authorizedTenantId` / `_isAuthorized` instance fields to the fake; routed
   `TenantId` / `ResolvedTenantId` / `IsAuthorized` / `IsResolved` / `IsActive` /
   `ValidUpTo` through them, falling back to AsyncLocal then default.
2. Wired `IMultiTenantContextAccessor<CenterixTenantInfo>` into the fake after host
   construction so HTTP-driven tests see the same resolved tenant production
   `CurrentTenant` does.

### Post-fix regression result

```
Total:     1779
Passed:    1778
Failed:    0
Skipped:   1
Exit code: 0
Duration:  11.9043 Minutes
```

Zero tests disabled, zero assertions weakened, zero new skips, zero SQL tests converted
to InMemory / SQLite. Production business behavior was not modified. Full evidence:
`docs/TASK-D-SQL-REGRESSION-INFRASTRUCTURE-VERIFICATION-REPORT.md`.

---

## 10. Git Verification

- **Current final HEAD**: `d8dd131e6b3c1741416d45bf71732e252594aa84`
- **Previous base commit (historical)**: `78e38e23bf8518800cfd44ac6d053ee9d68a8f42`
- **Implementation correction SHA (historical)**: `3bf610f33dcea0ef286033be48465baee8a6c576`
- **Working tree**: Clean after infrastructure-fix commit
- **Implementation commit (historical)**: `feat(billing): grant and apply free months benefit` (`78e38e2`)
- **Correction commit (historical)**: `fix(billing): enforce active subscription for free months` (`3bf610f`)
- **Documentation commit (historical)**: `docs(billing): finalize Task D verification evidence` (`ffcf023`)
- **Report update commit (historical)**: `docs(billing): update Task D verification report with current session evidence` (`b76d8ad`)
- **Infrastructure-fix commit (current HEAD)**: `test(infrastructure): stabilize sql server integration regression` (`d8dd131`)
- **Changed files** (6 files, +320 −11):
  - `src/Centerix.Application/Common/Interfaces/IAppDbContext.cs` — removed unnecessary `DbSet<OfferFreeMonthsBenefit>` (was added in original Task D but never used in application code)
  - `src/Centerix.Application/Platform/Contracts/Commands/ApplyFreeMonthsBenefitToSubscriptionHandler.cs` — added `Status == Active && EffectiveEndsAtUtc > DateTime.UtcNow` guard; updated idempotency path to also check Active+unexpired; added `ActiveSubscriptionNotFound` error
  - `src/Centerix.Domain/Platform/Contracts/FreeMonthsBenefitErrors.cs` — added `ActiveSubscriptionNotFound` error
  - `src/Centerix.Infrastructure/Data/Migrations/20260930131536_AddAppliedFreeMonthsBenefitIds.cs` — corrected migration defaultValue from `""` to `"[]"`
  - `tests/Centerix.SecurityTests/TaskD_FreeMonthsBenefitApplicationTests.cs` — added 3 new tests: InMem09 (expired subscription rejected), InMem10 (pending subscription rejected), InMem11 (Active but EffectiveEndsAtUtc expired rejected)
  - `tests/Centerix.SecurityTests/TaskD_FreeMonthsBenefitSqlServerTests.cs` — added SQL-D07 (expired subscription rejected via real SQL Server)

---

## 11. Search Audit

Task D changes do **not** alter the commercial semantics of:
- `BonusMonths` — unchanged
- `PromotionType` — unchanged
- `Refund` — unchanged
- `Cancellation` — unchanged
- `PaymentTerms` — unchanged
- `PhysicalGift` — unchanged
- `Referral` — unchanged
- `PaymentObligation` — unchanged

Only `TenantPlan.EffectiveEndsAtUtc` and `TenantPlan.AppliedFreeMonthsBenefitIds` are modified on successful application. `Contract.BonusMonths` is NOT inferred or modified.

---

## 12. Acceptance Criteria Checklist

| Criterion | Status |
|-----------|--------|
| Grant implemented | ✅ |
| Apply implemented | ✅ |
| Domain state transitions correct | ✅ |
| Eligibility/fulfillment remain independent | ✅ |
| Free Months do not become billable months | ✅ |
| Contract.BonusMonths is not modified/inferred | ✅ |
| **Active + unexpired subscription guard enforced** | ✅ |
| Application is idempotent | ✅ |
| AppliedFreeMonthsBenefitIds cannot duplicate the same benefit | ✅ |
| Atomic persistence is preserved | ✅ |
| Concurrent application cannot double-apply | ✅ |
| Tenant isolation verified | ✅ |
| Authorization verified | ✅ |
| Unit tests pass | ✅ 17/17 |
| InMemory production-flow tests pass | ✅ 11/11 |
| Local SQL Server integration tests pass | ✅ 7/7 |
| Full solution regression actually executed | ✅ 1779 total |
| Task D tests in full regression | ✅ 0 failures (all 35 pass) |
| Pre-existing SQL infrastructure failures | ✅ **Resolved** by commit `d8dd131` — see §9.5; current regression has 0 failures |
| No unexplained skips | ✅ 1 skip (pre-existing, see below) |
| EF AppDbContext has no pending model changes | ✅ |
| EF TenantDbContext has no pending model changes | ✅ |
| No unrelated production changes | ✅ |
| Verification report is accurate | ✅ |
| Git working tree is clean | ✅ |

---

## 13. Build

```
Command:    dotnet build Centerix.slnx --no-restore
Errors:     0
Warnings:   6112 (all pre-existing SA StyleCop warnings; non-blocking)
Exit code:  0
```

---

## 14. Skips

Exactly one test in the full suite is skipped. It is pre-existing and unrelated to Task D:

```text
Centerix.SecurityTests.Task18_5CreditEconomicOriginSqlServerTests
    .Test15_Task1851_MixedLineageProportionalTransferredOrigin

Skip reason (verbatim from its [Fact] attribute):
  "Complex overlapping subscription scenario - covered by Test16 and other tests"
```

No Task D test is skipped.

---

## 15. SQL Server Environment

```
SQL Server environment: Local SQL Server
Connection mode: Server=.;Trusted_Connection=True;TrustServerCertificate=True;Encrypt=False
SQL Server version: MSSQLLOCALDB / default local instance
Task D SQL tests: 7
Passed: 7
Failed: 0
Skipped: 0
```

Docker/Testcontainers: Not used for this phase.

---

## 16. Final Verification Evidence (Current Session — post `d8dd131`)

### Build
- **Command**: `dotnet build Centerix.slnx --no-restore`
- **Result**: ✅ Exit code 0, 0 errors, 6112 pre-existing StyleCop warnings

### Task D Domain Tests
- **Command**: `dotnet test Centerix.slnx --no-build --filter "FullyQualifiedName~TaskD_FreeMonthsBenefitGrantApplyTests"`
- **Result**: 17 passed, 0 failed, 0 skipped

### Task D InMemory Tests
- **Command**: `dotnet test Centerix.slnx --no-build --filter "FullyQualifiedName~TaskD_FreeMonthsBenefitApplicationTests"`
- **Result**: 11 passed, 0 failed, 0 skipped

### Task D SQL Server Tests
- **Command**: `dotnet test Centerix.slnx --no-build --filter "FullyQualifiedName~TaskD_FreeMonthsBenefitSqlServerTests"` (Local SQL Server: `Server=.`)
- **Result**: 7 passed, 0 failed, 0 skipped (SQL-D01 through SQL-D07)

### FULL SOLUTION REGRESSION
- **Command**: `dotnet test Centerix.slnx --no-build --verbosity normal`
- **Total**: 1779
- **Passed**: 1778
- **Failed**: **0**
- **Skipped**: 1 (pre-existing, see §14)
- **Duration**: 11.9043 Minutes
- **Exit code**: **0** (Test Run Successful, Build succeeded)

### EF Migrations
- `dotnet ef migrations has-pending-model-changes --context AppDbContext`: `No changes have been made to the model since the last migration.`
- `dotnet ef migrations has-pending-model-changes --context TenantDbContext`: `No changes have been made to the model since the last migration.`

### Git
- **Current HEAD**: `d8dd131e6b3c1741416d45bf71732e252594aa84` — `test(infrastructure): stabilize sql server integration regression`
- **Working tree**: Clean

### SQL Server
- Local SQL Server (`Server=.`) — no Docker / Testcontainers

### Task D Functional Verification (Re-confirmed at `d8dd131`)

#### Grant

`Pending + Eligible → Granted`

Grant does NOT modify:
- `EligibilityStatus`
- `EntitlementMonths`
- `EligibilityRule`
- `Contract.BonusMonths`

#### Apply

`Granted → AppliedToSubscription`

Target subscription must satisfy ALL:
- `TenantId` == authorized tenant
- `ContractId` == benefit.ContractId
- `Status == Active`
- `EffectiveEndsAtUtc > DateTime.UtcNow`

The handler rejects (with `FreeMonthsBenefitErrors.ActiveSubscriptionNotFound`):
- Pending
- Expired
- Cancelled
- Suspended
- PastDue
- Active-but-`EffectiveEndsAtUtc` already expired

Application behavior:
- Extends `EffectiveEndsAtUtc` by `entitlementMonths` calendar months
- Does NOT modify `BaseEndsAtUtc`
- Does NOT modify `ContractualMonthlyValue`
- Does NOT modify `Contract.BonusMonths`
- Records benefit ID exactly once in `AppliedFreeMonthsBenefitIds`
- Duplicate Apply is a no-op (idempotent)
- Concurrent Apply produces exactly one effective application
- Tenant isolation enforced (`IgnoreQueryFilters` + explicit check)
- Authorization enforced (`Permissions.Benefits.Manage`)

No refund behavior, evaluator redesign, PaymentObligation, PhysicalGift behavior, or Referral behavior was introduced.

---

## 17. Final Status

**TASK D — VERIFIED AND CLOSED**

Acceptance checklist:

- [x] Grant lifecycle verified
- [x] Apply lifecycle verified
- [x] Active + unexpired subscription guard verified
- [x] Idempotency verified
- [x] Concurrency verified
- [x] Tenant isolation verified
- [x] Local SQL Server verification passed
- [x] Full solution regression passed (1778 / 0 / 1)
- [x] Failed = 0
- [x] Existing single skip explicitly documented
- [x] EF AppDbContext clean
- [x] EF TenantDbContext clean
- [x] No production behavior changed by regression fix
- [x] Working tree clean after commit

Task D feature verification:
- 17 domain tests passed
- 11 InMemory tests passed
- 7 SQL Server tests passed (Local SQL Server, no Docker / Testcontainers)
- **35 / 35 Task D tests green**

Full solution regression (post-infrastructure-fix `d8dd131`):
- Total: 1779
- Passed: 1778
- Failed: 0
- Skipped: 1 (pre-existing `Test15_Task1851_MixedLineageProportionalTransferredOrigin`)
- Exit code: 0
- Duration: 11.9043 Minutes

Current HEAD: `d8dd131e6b3c1741416d45bf71732e252594aa84`
Working tree: clean
