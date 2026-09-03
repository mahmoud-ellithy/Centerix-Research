# Phase 5 (Teachers) Test Remediation Report

Date: 2026-09-03

## Final Results

| Suite | Traitle | Result |
|---|---|---|
| Phase 5 HTTP authorization / soft-delete visibility (InMemory) | `Category=Phase5Http` | **23/23 passed** |
| Phase 5 SQL Server RowVersion concurrency (real SQL Server) | `Category=Phase5Sql` | **4/4 passed** |

Command used (from the solution root):

```
dotnet test "tests\Centerix.SecurityTests\Centerix.SecurityTests.csproj" --filter "Category=Phase5Http" --nologo -v minimal
dotnet test "tests\Centerix.SecurityTests\Centerix.SecurityTests.csproj" --filter "Category=Phase5Sql"  --nologo -v minimal
```

## Files Changed

All changes are test-side, in `tests\Centerix.SecurityTests\`. **No production (`src\`) code was modified.**

### Phase5TeachersAuthorizationHttpTests.cs

- `SeedSubjectAsync` now allocates a **unique** `AcademicStage` id (`MaxAsync + 1` via `IgnoreQueryFilters`) and returns `(subjectId, stageId)`.
- Root cause: `AcademicStage` is tenant-scoped (inherits `TenantId`) despite its global int key. The old `Id == 1` existence check ran in a tenant-less DI scope, so it always returned `false` while the shared InMemory store already held stage `1` from another tenant → `ArgumentException: An item with the same key has already been added. Key: 1`.
- The 4 subject tests were updated to destructure the tuple; update tests pass the real `stageId`, delete tests use `var (subjectId, _) = ...`.

### Phase5SoftDeleteVisibilityHttpTests.cs

- `DeleteTeacherAsync`: added `IgnoreQueryFilters()` to the direct-scope lookup. Outside an HTTP request `_currentTenant.TenantId` is empty, so the fail-closed tenant filter matched nothing and the lookup returned `null` → `Assert.NotNull` failures.
- `SeedActiveTenantAsync`: added the `StudentManagement` feature grant to the plan. `DELETE /api/students/{id}` requires `[RequireFeature("Students")]`, which was returning 403 instead of executing the soft delete.
- Fixed the teacher-rating URL typo: `/api/teacher/rratings` → `/api/teacherratings` (the 404 had made the test vacuous).
- `TenantIsolation_Preserves_Active_Teacher_Quality`: replaced a vacuous direct-scope DB assertion with API-payload assertions — tenant A's `GET /api/teachers` listing excludes the deleted teacher, tenant B's listing is unaffected.

### Phase5TeachersConcurrencySqlServerTests.cs

- Added `IgnoreQueryFilters()` to **all** test load/verify reads in fresh DI scopes (3× `SingleAsync` "Sequence contains no elements" failures on real SQL Server — same fail-closed tenant-filter root cause).
- New `EnsurePermissionGrantsAsync` helper: seeds the `PermissionCatalog` and grants every permission to the target role and `PlatformAdmin`. Wired into the 409 test — mark-paid requires `[HasPermission(SalaryPayments.Update)]`, which is resolved per-request from `TenantMembership → Role → RolePermissions` (see `TenantGuardMiddleware` and `PermissionAuthorizationHandler`). The missing grant produced 403 instead of the 409 under test.
- `Teacher_ConcurrentUpdates_OneWins_OtherThrowsDbUpdateConcurrencyException`: `Teacher.Update`'s first parameter is `branchId`; the test passed `Guid.NewGuid()`, violating `FK_Teachers_Branches_BranchId`. It now reuses the seeded branch so the RowVersion race is the only failure path.
- Removed the dead, unreferenced `SeedTeacherWithAuthAsync` helper.

## Root Causes (3 distinct patterns)

1. **Direct-scope reads under fail-closed tenant query filters** — `_currentTenant.TenantId` is empty outside HTTP requests, so every query over `IHasTenantId` / `SoftDeletableEntity` types matched nothing. Fix: `IgnoreQueryFilters()` in test-only reads. Production code untouched.
2. **Tenant-scoped entity with a global key** — `AcademicStage` collided across tenants in the shared InMemory store. Fix: unique id allocation in the test seed.
3. **Incomplete auth seeding** — a missing feature grant (Students) and missing `RolePermissions` grants (SalaryPayments.Update) turned the intended targets of the tests (soft-delete behavior, RowVersion 409) into 403s. Fix: grant the required feature and permission catalog in the seed helpers.

## Verification

- Phase5Http re-run after fixes: `Failed: 0, Passed: 23`.
- Phase5Sql re-run after fixes: `Failed: 0, Passed: 4` (local SQL Server via `SqlServerIntegrationFactory`, unique database per run, dropped on dispose).
