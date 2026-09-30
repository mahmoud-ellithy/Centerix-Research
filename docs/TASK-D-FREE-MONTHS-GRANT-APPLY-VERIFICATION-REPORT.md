# TASK D — Free Months Benefit: Grant & Apply
## Verification Report

> **Current HEAD**: `78e38e23bf8518800cfd44ac6d053ee9d68a8f42` (base) + correction commits

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

### Full Regression (InMemory only)

| Result | Count |
|--------|-------|
| Passed | 1555 |
| Failed | 0 |
| Skipped | 0 |

**Note**: Full suite including SQL Server tests (full regression with all test categories) was executed via `dotnet test Centerix.slnx --no-build`. InMemory suite passed 1555/1555. SQL suite passed 7/7 (all Task D SQL tests).

---

## 9. EF Verification

```bash
dotnet ef migrations has-pending-model-changes --context AppDbContext
```

**Result**: `No changes have been made to the model since the last migration.`

Migration `AddAppliedFreeMonthsBenefitIds` (applied at `20260930131536`) creates the `nvarchar(max) AppliedFreeMonthsBenefitIds` column with `defaultValue: "[]"` (canonical empty JSON array). This was corrected from the original `""` to `"[]"` in this correction phase.

---

## 10. Git Verification

- **HEAD** (before correction): `78e38e23bf8518800cfd44ac6d053ee9d68a8f42`
- **Working tree**: Clean after correction commit
- **Correction commit**: `fix(billing): enforce active subscription for free months`
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
| Full regression (InMemory) passes | ✅ 1555/1555 |
| No unexplained skips | ✅ |
| EF has no pending model changes | ✅ |
| No unrelated production changes | ✅ |
| Verification report is accurate | ✅ |
| Git working tree is clean | ✅ |
