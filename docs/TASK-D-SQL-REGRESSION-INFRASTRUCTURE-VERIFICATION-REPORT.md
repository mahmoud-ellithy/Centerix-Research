# SQL Server Regression Infrastructure Verification

## Root Cause

The 159 SQL-Server integration-test failures had a **single, deterministic root cause**:

`tests/Centerix.SecurityTests/SqlServerIntegrationFactory.cs` registers `TaskCFakeCurrentTenant`
as the `ICurrentTenant` replacement so per-test `AsyncLocal` tenant writes are visible across
the shared singleton's scope. The fake did **not** carry the production
`CurrentTenant._authorizedTenantId` / `_isAuthorized` instance fields.

Twenty-four SQL-integration test classes configure the tenant via reflection:

```csharp
private static void AuthorizeTenant(IServiceProvider services, string tenantId)
{
    var currentTenant = services.GetRequiredService<ICurrentTenant>();
    var type = currentTenant.GetType();
    type.GetField("_authorizedTenantId", BindingFlags.NonPublic | BindingFlags.Instance)!
        .SetValue(currentTenant, tenantId);
    type.GetField("_isAuthorized", BindingFlags.NonPublic | BindingFlags.Instance)!
        .SetValue(currentTenant, true);
}
```

Because the fake's runtime class has neither, `GetField(...)` returned `null` and
`SetValue(null, ...)` threw `NullReferenceException` at every reflection-based test
helper call (SeedOldContractAsync, SeedTenantAsync, …). That single NRE was the
sole initial-cause.

Once the NRE was removed, a second, related issue surfaced. The fake originally had
hard-coded `IsAuthorized = true` and `IsResolved = true`, but **`ResolvedTenantId`
returned the AsyncLocal / default value, not the Finbuckle-resolved tenant from the
HTTP `tenant` request header**. `TenantGuardMiddleware` then queried `TenantMemberships`
filtered on the AsyncLocal value, found no matching membership, and rejected with 403.
Eight `SqlServerInvitationFlowTests` and one `Phase5TeachersConcurrencySqlServerTests`
test failed with this exact pattern.

## Failure Classification Before Fix

| Cause | Count | What happened |
|---|---|---|
| AuthorizeTenant / task-tenant context (reflection NRE) | 159 | `TaskCFakeCurrentTenant` did not expose `_authorizedTenantId` / `_isAuthorized`; reflection-based test helpers called `SetValue(null, …)` → NRE |
| SQL Server deadlock / shared DB | 0 | (none observed — distinct category) |
| Other | 0 | (none observed) |
| **Total** | **159** | All same root cause |

After step 1 of the fix (adding the instance fields):

| Cause | Count |
|---|---|
| HTTP 403 because `ResolvedTenantId` did not read from Finbuckle | 9 |
| **Total** | **9** |

After step 2 of the fix (wiring `IMultiTenantContextAccessor` into the fake):

| Cause | Count |
|---|---|
| (no failures) | **0** |

## Fix

**File**: `tests/Centerix.SecurityTests/SqlServerIntegrationFactory.cs`

Two precise edits, both inside the existing fixture, no test code touched:

1. Added the production-shape instance fields to `TaskCFakeCurrentTenant`:

```csharp
private string? _authorizedTenantId;
private bool _isAuthorized;
```

…and made `TenantId`/`ResolvedTenantId`/`IsAuthorized`/`IsResolved`/`IsActive`/
`ValidUpTo` reflect those fields plus the existing `AsyncLocal` value (whichever
was set). `AuthorizeTenant()` now promotes the resolved/AsyncLocal tenant into
the authorized fields.

2. Wired Finbuckle's `IMultiTenantContextAccessor<CenterixTenantInfo>` into the
fake so HTTP-driven tests see the tenant resolved from the `tenant` request
header the same way production `CurrentTenant` does:

```csharp
// In SqlServerWebApplicationFactory.ConfigureWebHost:
services.AddSingleton<TaskCFakeCurrentTenant>();
services.AddSingleton<ICurrentTenant>(sp => sp.GetRequiredService<TaskCFakeCurrentTenant>());

// In SqlServerIntegrationFactory.InitializeAsync (after the host is built):
Factory.Services.GetRequiredService<TaskCFakeCurrentTenant>()
    .SetMultiTenantContextAccessor(
        Factory.Services.GetRequiredService<IMultiTenantContextAccessor<CenterixTenantInfo>>());
```

No other production or test code was modified.

## Why the fix is safe

- **Tests are not weakened.** Assertions are unchanged. The fake now exposes the
  fields the test reflection expects; tests that previously exploded at line 1
  of their seed helpers now run to completion.
- **Concurrency tests remain real.** `Phase9FinancialConcurrencySqlServerTests`,
  `Phase10_1CreditConcurrencySqlServerTests`, `Phase12_1CreditConcurrencySqlServerTests`,
  `Task201_CombinedSettlementConcurrencyTests`, `Phase9_4_2CancellationConcurrencySqlServerTests`,
  `Phase9_5_1NaturalExpirationConcurrencySqlServerTests`, `Phase5TeachersConcurrencySqlServerTests`,
  `Task15_1ConcurrencySqlServerTests`, `Register_ConcurrentSameToken_ExactlyOneSucceeds_…` —
  every `[Concurrency.test]` is verified by the full regression passing.
- **Tenant isolation remains real.** The fake still scopes per-async-flow tenant
  state via `AsyncLocal`. The `TenantGuardMiddleware` membership check uses
  `currentTenant.ResolvedTenantId`; that property now reads from the Finbuckle
  accessor, exactly as production does.
- **SQL Server remains real.** All SQL Server integration tests still execute
  against Local SQL Server (`Server=.`) via the existing
  `SqlServerIntegrationFactory` — no InMemory / SQLite / Testcontainers was
  substituted.
- **Production business behavior is not bypassed.** Only the test fake's field
  shape changed. No production handler, command, domain rule, or migration was
  touched.

## Verification

### Build

```text
Command:  dotnet build Centerix.slnx --no-restore
Errors:   0
Warnings: 6112 (all pre-existing SA StyleCop warnings)
Result:   0 Error(s)
```

### Targeted Tests After Fix (9 previously failing)

```text
Command:  dotnet test … --filter "FullyQualifiedName~SqlServerInvitationFlowTests|FullyQualifiedName~Phase5TeachersConcurrencySqlServerTests.SalaryPayment_ConcurrentMarkPaid_HttpReturns409Conflict"
Total:    16
Passed:   16
Failed:   0
Duration: 20.7567 s
```

### Full Solution Regression

```text
Command:   dotnet test Centerix.slnx --no-build --verbosity normal
Total:     1779
Passed:    1778
Failed:    0
Skipped:   1
Duration:  11.9043 Minutes
Exit code: 0
```

Test Run Successful. Build succeeded.

### EF

```text
Command:  dotnet ef migrations has-pending-model-changes --context AppDbContext --project src\Centerix.Infrastructure
Result:   No changes have been made to the model since the last migration.

Command:  dotnet ef migrations has-pending-model-changes --context TenantDbContext --project src\Centerix.Infrastructure
Result:   No changes have been made to the model since the last migration.
```

## Skips

Exactly one test is skipped (pre-existing, unrelated to this fix):

```text
Centerix.SecurityTests.Task18_5CreditEconomicOriginSqlServerTests
    .Test15_Task1851_MixedLineageProportionalTransferredOrigin

Skip reason (verbatim from its [Fact] attribute):
  "Complex overlapping subscription scenario - covered by Test16 and other tests"
```

No new skips were added.

## SQL Server

```text
Local SQL Server (Server=.) — confirmed reachable.
Docker / Testcontainers: NOT used.
```

## Git

- **HEAD (after commit)**: see `git log -1`
- **Changed files (this commit, 1 file, +47 −9)**:
  - `tests/Centerix.SecurityTests/SqlServerIntegrationFactory.cs`
    - `TaskCFakeCurrentTenant` — added `_authorizedTenantId` / `_isAuthorized`
      instance fields, wired optional `IMultiTenantContextAccessor<CenterixTenantInfo>`,
      promoted `AuthorizeTenant()` and resolution properties accordingly.
    - `SqlServerWebApplicationFactory.ConfigureWebHost` — register
      `TaskCFakeCurrentTenant` as a singleton and alias `ICurrentTenant` to it.
    - `SqlServerIntegrationFactory.InitializeAsync` — after host build, inject
      Finbuckle's accessor into the fake.

- **Working tree**: Clean after commit.

## Final Acceptance

- [x] Root cause of the 159 failures identified
- [x] Root cause fixed
- [x] No tests disabled
- [x] No assertions weakened
- [x] No skips added
- [x] No SQL tests converted to InMemory / SQLite
- [x] Intentional concurrency tests remain real
- [x] Tenant context is deterministic
- [x] SQL database isolation is deterministic
- [x] SQL integration tests can execute reliably
- [x] Build passes
- [x] Full solution regression passes
- [x] Failed = 0
- [x] No unexplained skips
- [x] EF AppDbContext clean
- [x] EF TenantDbContext clean
- [x] Working tree clean
- [x] Verification report accurately documents the root cause and fix

**TASK D — 159 SQL REGRESSION FAILURES — FIXED AND VERIFIED**