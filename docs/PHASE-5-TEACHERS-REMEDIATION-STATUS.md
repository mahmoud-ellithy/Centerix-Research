# PHASE 5 TEACHERS — PRODUCTION REMEDIATION STATUS CHECK

**Date:** 2026-09-03
**Mode:** Verification / status check only (no source, test, migration, or configuration changes were made)
**Source of truth:** Current working tree (HEAD = `1e02168` "feat(phase5-teachers): add concurrency controls and feature gating", clean tree)

**Audit references used:** `ARCHITECTURE-BASELINE.md`, `PHASE-3-VERIFICATION-REPORT.md`, `PHASE-5-TEACHERS-AUDIT-REPORT.md`, `PHASE-5-TEACHERS-TEST-REMEDIATION-REPORT.md`

---

## Scope note (critical for interpreting this report)

The test-remediation report states "No production (`src/`) code was modified." That statement is true **only for the test-fixing step**. It does not describe the full remediation work. The working tree contains a preceding commit (`1e02168`, whose parent is `1fc2e57` "feat(teacher-salary): add salary management module") that bundles:

1. Production remediation for all four Phase 5 blockers, **and**
2. The new Phase 5 test suites, **and**
3. The Phase 5 documentation.

This status check inspects the **current working tree** as the single source of truth and verifies each production fix independently, file by file. Passing tests were **not** used as evidence of remediation; each invariant below was checked against production source.

---

## 1. F-01 / H-01 — Feature gating

**Requirement:** All Teachers/Subjects/TeacherSalaryConfigs/SalaryPayments mutation endpoints must carry the required feature gate and authorization.

### Verified current state (production code)

| Endpoint | File | Lines | Attributes |
|---|---|---|---|
| Teachers.Create | `src/Centerix.API/Controllers/TeachersController.cs` | 35–37 | `[HasPermission(Permissions.Teachers.Create)]` + `[RequireFeature(FeatureCodes.TeacherManagement)]` |
| Teachers.Update | `src/Centerix.API/Controllers/TeachersController.cs` | 47–49 | `[HasPermission(Permissions.Teachers.Update)]` + `[RequireFeature(FeatureCodes.TeacherManagement)]` |
| Teachers.Delete | `src/Centerix.API/Controllers/TeachersController.cs` | 64–66 | `[HasPermission(Permissions.Teachers.Delete)]` + `[RequireFeature(FeatureCodes.TeacherManagement)]` |
| Subjects.Create | `src/Centerix.API/Controllers/SubjectsController.cs` | 35–37 | `[HasPermission(Permissions.Subjects.Create)]` + `[RequireFeature(FeatureCodes.TeacherManagement)]` |
| Subjects.Update | `src/Centerix.API/Controllers/SubjectsController.cs` | 47–49 | `[HasPermission(Permissions.Subjects.Update)]` + `[RequireFeature(FeatureCodes.TeacherManagement)]` |
| Subjects.Delete | `src/Centerix.API/Controllers/SubjectsController.cs` | 64–66 | `[HasPermission(Permissions.Subjects.Delete)]` + `[RequireFeature(FeatureCodes.TeacherManagement)]` |
| TeacherSalaryConfigs.Create | `src/Centerix.API/Controllers/TeacherSalaryConfigsController.cs` | 35–37 | `[HasPermission(Permissions.TeacherSalaryConfigs.Create)]` + `[RequireFeature(FeatureCodes.TeacherManagement)]` |
| TeacherSalaryConfigs.Update | `src/Centerix.API/Controllers/TeacherSalaryConfigsController.cs` | 47–49 | `[HasPermission(Permissions.TeacherSalaryConfigs.Update)]` + `[RequireFeature(FeatureCodes.TeacherManagement)]` |
| TeacherSalaryConfigs.Delete | `src/Centerix.API/Controllers/TeacherSalaryConfigsController.cs` | 64–66 | `[HasPermission(Permissions.TeacherSalaryConfigs.Delete)]` + `[RequireFeature(FeatureCodes.TeacherManagement)]` |
| SalaryPayments.Create | `src/Centerix.API/Controllers/SalaryPaymentsController.cs` | 35–37 | `[HasPermission(Permissions.SalaryPayments.Create)]` + `[RequireFeature(FeatureCodes.TeacherManagement)]` |
| SalaryPayments.MarkPaid | `src/Centerix.API/Controllers/SalaryPaymentsController.cs` | 47–49 | `[HasPermission(Permissions.SalaryPayments.Update)]` + `[RequireFeature(FeatureCodes.TeacherManagement)]` |
| SalaryPayments.Cancel | `src/Centerix.API/Controllers/SalaryPaymentsController.cs` | 59–61 | `[HasPermission(Permissions.SalaryPayments.Update)]` + `[RequireFeature(FeatureCodes.TeacherManagement)]` |

All 11 required mutation endpoints are gated. **All 11.**

### Gate pipeline (verified production wiring)

- `RequireFeatureAttribute` (`src/Centerix.Infrastructure/Auth/FeatureAuthorization.cs`) derives from `AuthorizeAttribute($"Feature:{featureCode}")`; `FeatureCodes.TeacherManagement = "Teachers"`.
- `PermissionPolicyProvider` (`src/Centerix.Infrastructure/Auth/PermissionPolicyProvider.cs`) builds the `Feature:*` policy as `RequireAuthenticatedUser()` + `FeatureRequirement(code)`; non-Feature policies use `PermissionRequirement`.
- `FeatureAuthorizationHandler` (same file) is **fail-closed**: PlatformAdmin role bypass → resolve `ICurrentTenant` → `IFeatureAccessService.HasFeatureAsync`, which checks the tenant's ACTIVE (unexpired, non-suspended) subscription entitlement snapshot.
- Registered in `src/Centerix.Infrastructure/DependencyInjection.cs`.

### Pre-remediation baseline

Verified via `git show 1fc2e57` (the commit that introduced the salary module): at that commit, only the **Create** endpoints carried `[RequireFeature]`. The Update/Delete gates and the MarkPaid/Cancel gates were added by `1e02168` (diff confirmed for all four controllers).

**Status: PASS**

---

## 2. F-04 / H-04 — Shared soft-delete query filter

**Requirement:** The effective EF model query filter for soft-deletable tenant entities must preserve BOTH tenant isolation AND `DeletedAtUtc == null`.

### Verified current state (production code)

`src/Centerix.Infrastructure/Data/AppDbContext.cs`:

- `OnModelCreating` (line 141–146) runs `builder.ApplyConfigurationsFromAssembly(...)` **first**, then `ApplyTenantQueryFilter(builder)` (line 145). Because EF Core keeps **one** query filter per entity type and **last-`HasQueryFilter`-wins**, the composed filter applied here supersedes the per-configuration `HasQueryFilter(x => x.DeletedAtUtc == null)` still present in `TeacherConfiguration.cs` (line 61), `StudentConfiguration.cs` (line 74), and `BranchConfiguration.cs` (line 58).
- `ApplyTenantQueryFilter` (line 148–173) iterates all non-owned entity types implementing `IHasTenantId` and, via reflection, applies one of two generic methods based on whether the CLR type derives from `SoftDeletableEntity`:
  - `ApplyFilterFor<TEntity>` (line 175–179): `e => e.TenantId == _currentTenant.TenantId` (plain tenant entities, e.g. SalaryPayment, AcademicStage).
  - `ApplySoftDeleteFilterFor<TEntity>` (line 181–187), where `TEntity : SoftDeletableEntity, IHasTenantId`:
    ```csharp
    builder.Entity<TEntity>().HasQueryFilter(e =>
        e.TenantId == _currentTenant.TenantId &&
        e.DeletedAtUtc == null);
    ```
- The filter lambda reads `_currentTenant.TenantId` **live per request** (a C# lambda over a context member, not a baked `Expression.Constant`), so with the fail-closed `ICurrentTenant` (empty `TenantId` until tenant authorization) the filter matches nothing — tenant isolation is fail-closed, and it also means an unauthenticated/unresolved-tenant scope sees zero rows.
- Types receiving the **composed** (tenant + soft-delete) filter: **Teacher, Student, Branch** — all `SoftDeletableEntity<Guid>` + `IHasTenantId`. (AttendanceLog is `AuditableEntity<long>`, not soft-deletable; Subject is `AuditableEntity<int>` and hard-deleted by design — pre-existing behavior.)

### Critical question

> Does a normal query such as `context.Teachers.ToListAsync()` exclude soft-deleted Teachers?

**Yes.** The effective model filter for `Teacher` is `TenantId == _currentTenant.TenantId && DeletedAtUtc == null`. A soft-deleted Teacher (`DeletedAtUtc != null`) is invisible to every LINQ query against `context.Teachers` in a tenant-authenticated scope, and rows from other tenants are invisible as well. This is model-level (appended to `WHERE` by EF Core), not a test-side workaround; it is defined once in `AppDbContext` production code and applies identically to all providers (SQL Server and InMemory).

**Status: PASS**

---

## 3. F-02 / H-02 — Salary payment state machine

**Requirement:** Enforce Pending → Paid / Cancelled transitions; reject Paid → Paid; reject Cancelled → Paid; no caller-supplied initial status; Paid never with `PaidAt == null`.

### Verified current state (production code)

`src/Centerix.Domain/Teachers/SalaryPayments/SalaryPayment.cs`:

- `Create` (line 59–91) has **no status or paidAt parameter**. It validates `teacherId`, `periodMonth` (1–12), `periodYear` (2000–2100), `grossAmount > 0`, `netAmount > 0`, and returns a `SalaryPayment` constructed with `SalaryPaymentStatus.Pending, paidAt: null` (line 89–90). The constructor that accepts a status is `private` (line 34). A client **cannot** create a payment as Paid or Cancelled through any production path.
- `MarkPaid(DateTime paidAt)` (line 93–107):
  - `Status == Paid` → `SalaryPaymentErrors.DuplicatePayment` (**Paid → Paid rejected**).
  - `Status == Cancelled` → `SalaryPaymentErrors.InvalidStatus` (**Cancelled → Paid rejected** — the pre-remediation H-02 bypass is closed).
  - Otherwise `Status = Paid; PaidAt = paidAt` atomically in the same domain transition, persisted in one `SaveChangesAsync`.
- `Cancel()` (line 109–122):
  - `Status == Paid` → `SalaryPaymentErrors.InvalidStatus` (**Paid → Cancelled rejected**).
  - `Pending → Cancelled` allowed (existing intended behavior).
  - Repeated Cancel on an already-Cancelled payment is a **no-op success** — explicitly documented in-code (line 115–118) as preserved existing contract, not an invented rule. `PaidAt` is nulled.
- Invariant "Paid state must not exist with `PaidAt == null`": holds — the only path to `Paid` is `MarkPaid`, which always sets `PaidAt`; `Create` can only produce `Pending`/null.

### Handler layer (no bypass)

`src/Centerix.Application/Teachers/SalaryPayments/Commands/SalaryPaymentCommands.cs`:

- `CreateSalaryPaymentCommand` record has **no** `Status`/`PaidAt` fields (line 14–19); `CreateSalaryPaymentHandler` calls `SalaryPayment.Create(...)` (line 48).
- `MarkSalaryPaymentPaidHandler` calls `payment.MarkPaid(DateTime.UtcNow)` (line 103).
- `CancelSalaryPaymentHandler` calls `payment.Cancel()` (line 146).

No production path forwards a client-supplied status or mutates state outside the domain methods. Controller authorization for MarkPaid/Cancel is covered in section 1.

**Status: PASS**

---

## 4. F-03 / H-03 — Salary payment concurrency

**Requirement:** SalaryPayment needs a concurrency token, correct EF configuration, protection of MarkPaid/Cancel against concurrent updates, and the established 409 conflict response (not an unhandled 500), consistent with the Teacher RowVersion approach.

### Verified current state (production code)

- **Token:** `SalaryPayment` has `[Timestamp] public byte[]? RowVersion { get; internal set; }` (`SalaryPayment.cs` line 29–30), with a doc comment stating it guards the financial state machine against silent last-write-wins races. Identical pattern to `Teacher` and `Student` (which carry the same `[Timestamp] RowVersion` and `.IsRowVersion()` configuration).
- **EF configuration:** `src/Centerix.Infrastructure/Data/Configurations/SalaryPaymentConfiguration.cs` line 57–58: `builder.Property(p => p.RowVersion).IsRowVersion();` — the project's established rowversion pattern (same as `TeacherConfiguration.cs` line 58–59, `StudentConfiguration.cs` line 71–72).
- **Protection:** MarkPaid/Cancel read the entity within a tenant scope, mutate via the domain methods, and persist via `SaveChangesAsync`. EF includes the stale `rowversion` in the `WHERE` clause of the `UPDATE`; a concurrent writer invalidates it and EF throws `DbUpdateConcurrencyException`. The double-MarkPaid race is covered the same way (second writer's save fails).
- **Exception handling:** `src/Centerix.API/Infrastructure/GlobalExceptionHandler.cs` (line 52–64) has an explicit `DbUpdateConcurrencyException` branch returning **409 Conflict** with a localized `ProblemDetails`: `Type = "https://tools.ietf.org/html/rfc9110#section-15.5.10"`, `Title = localizer.Translate("Error:Concurrency")`, `Detail = "The record was modified by another request. Reload the record and try again."` Localization keys added to `src/Centerix.API/Localization/en.json` (`"Concurrency conflict"`) and `ar.json` (`"تعارض في التزامن"`). The handler is registered in `src/Centerix.API/DependencyInjection.cs` (line 73 `AddExceptionHandler<GlobalExceptionHandler>()`; line 124 `app.UseExceptionHandler()`). This is the project's established conflict response — **not** an unhandled 500.
- **Consistency with Teacher:** same `[Timestamp] byte[]? RowVersion` + `.IsRowVersion()` + same 409 mapping; the exception-handler comment explicitly names both Teacher and SalaryPayment RowVersion as covered.

**Status: PASS**

---

## 5. Database / EF consistency

| Artifact | State |
|---|---|
| Configuration | `SalaryPaymentConfiguration.cs` line 57–58: `RowVersion` `.IsRowVersion()` |
| Snapshot | `AppDbContextModelSnapshot.cs` line 2317–2320: `b.Property<byte[]>("RowVersion").IsConcurrencyToken().ValueGeneratedOnAddOrUpdate().HasColumnType("rowversion")` on the `SalaryPayment` entity |
| Migration | New latest migration `20260903094745_AddSalaryPaymentRowVersion.cs` (line 13–19): `AddColumn<byte[]>("RowVersion", schema: "Platform", table: "SalaryPayments", type: "rowversion", rowVersion: true)` + corresponding `Down` drop; Designer file present |
| Pending changes | `dotnet ef migrations has-pending-model-changes --context AppDbContext --project src/Centerix.Infrastructure --startup-project src/Centerix.API` → **"No changes have been made to the model since the last migration."** (read-only check; no migration was created or modified) |

Configuration, snapshot, and migration are in sync.

**Status: PASS**

---

## 6. Test result interpretation

The remediation report's results correspond exactly to the new suites in the working tree (counts verified by trait enumeration):

- **Phase5Http = 23/23** = 16 tests in `Phase5TeachersAuthorizationHttpTests.cs` + 7 tests in `Phase5SoftDeleteVisibilityHttpTests.cs` (InMemory `WebApplicationFactory`, full production pipeline: real controllers, real policy provider, real `AppDbContext` query filters, real `GlobalExceptionHandler`).
- **Phase5Sql = 4/4** = 4 tests in `Phase5TeachersConcurrencySqlServerTests.cs` (real SQL Server via `SqlServerIntegrationFactory`, exercising real `rowversion` columns).

### What these passes DO prove

- The suites execute cleanly against the **current production code** (no skips, no disabled tests, no source edits).
- The authorization tests genuinely exercise the production gate pipeline (403 without the `Teachers` feature grant; success with it), so the F-01 gating is validated end-to-end.
- The soft-delete visibility tests run through the production `AppDbContext` model filters, so F-04 is validated at the query level.
- The concurrency tests hit real SQL Server `rowversion` semantics, so the F-03 token is validated at the storage level.

### What they do NOT prove (caveats to record)

- **HTTP 409 concurrency assertion is loose:** the SQL concurrency test asserts the losing request returns one of `Conflict`/`BadRequest`/`InternalServerError` (in addition to the winner's `NoContent`). It therefore would still pass if the API regressed to a 500. The 409 mapping itself is verified here by **source inspection** of `GlobalExceptionHandler.cs`, not by the test.
- **Three "rejects deleted teacher" assertions are loose** (accept 404/400/500) in the soft-delete visibility suite. The underlying exclusion is still model-filter-driven (verified in `AppDbContext`), but the HTTP-status expectation is not pinned.
- **`IgnoreQueryFilters()` in the SQL tests is a test-side necessity, not a workaround for a production defect:** direct DI-scoped reads in those tests have no authorized tenant context (fail-closed `ICurrentTenant`), so they use `IgnoreQueryFilters()` to manipulate seed data out-of-band. No production code path uses `IgnoreQueryFilters()`.
- No SQL-Server-specific soft-delete visibility test exists; F-04 is proven at the EF model level (provider-agnostic) and via InMemory HTTP tests.
- Domain-level state-machine coverage (`Phase5TeachersDomainTests.cs`, 8 tests) exercises the real `SalaryPayment`/`Teacher` classes directly — it validates the F-02 invariants independently of HTTP.

**Conclusion:** the passing suites corroborate the production remediation, but the definitive evidence for each blocker is the production source inspected in sections 1–5. No test was found that "passes without exercising production behavior" in a way that would mask an unremediated blocker; the loose assertions above are quality observations, not remediation gaps.

---

## 7. Important distinction — test-only vs. production

### (A) Test infrastructure / test-seeding fixes (NOT production remediation)

Per the test-remediation report, and confirmed as test-side:

1. `IgnoreQueryFilters()` on direct DI-scope reads in `Phase5TeachersConcurrencySqlServerTests.cs` (no tenant context in those scopes).
2. Unique `AcademicStage` test-ID allocation to avoid tenant-scoped global-key collisions.
3. `StudentManagement` feature grant added to test seed data.
4. `SalaryPayments.Update` permission grant added to test seed data.
5. FK-correct Teacher seed (valid `BranchId`/branch reuse).
6. Test URL correction (`/api/teacher/rratings` → `/api/teacherratings`).

None of these touch `src/`. They make the test harness runnable; they fix no production defect.

### (B) Production remediation (in `src/`, commit `1e02168`)

- Four controllers: added `[RequireFeature(FeatureCodes.TeacherManagement)]` to 8 previously-ungated endpoints (Subjects Update/Delete, TeacherSalaryConfigs Update/Delete, SalaryPayments MarkPaid/Cancel, Teachers Update/Delete) — F-01.
- `AppDbContext.cs`: `ApplyTenantQueryFilter` now composes `TenantId` + `DeletedAtUtc == null` for all `SoftDeletableEntity`+`IHasTenantId` types (Teacher, Student, Branch), applied after `ApplyConfigurationsFromAssembly` so it wins — F-04.
- `SalaryPayment.cs`: private status-bearing constructor; `Create` only produces `Pending`/`PaidAt=null`; `MarkPaid` rejects `Paid` and `Cancelled`; `Cancel` rejects `Paid`; repeated Cancel documented no-op — F-02.
- `SalaryPayment.cs` + `SalaryPaymentCommands.cs`: no caller-supplied `Status`/`PaidAt` anywhere in the create path — F-02.
- `SalaryPayment.cs`: `[Timestamp] RowVersion`; `SalaryPaymentConfiguration.cs`: `.IsRowVersion()` — F-03.
- `GlobalExceptionHandler.cs`: `DbUpdateConcurrencyException` → 409 localized ProblemDetails; `en.json`/`ar.json`: `Error:Concurrency` keys — F-03.
- Migration `20260903094745_AddSalaryPaymentRowVersion` (+ Designer) and snapshot update — F-03 / DB consistency.
- Incidental (out of blocker scope): `CreateStudentValidator.cs` phone-number max length 20 → 30.

---

## 8. FINAL STATUS

## PHASE 5 PRODUCTION REMEDIATION STATUS

| Finding | Production Status | Evidence |
|---|---|---|
| F-01/H-01 Feature gating | **PASS** | All 11 mutation endpoints carry `[HasPermission(...)]` + `[RequireFeature(FeatureCodes.TeacherManagement)]` — `TeachersController.cs` L35/47/64, `SubjectsController.cs` L35/47/64, `TeacherSalaryConfigsController.cs` L35/47/64, `SalaryPaymentsController.cs` L35/47/59. Gate pipeline verified: `RequireFeatureAttribute` → `PermissionPolicyProvider` ("Feature:*" policy) → fail-closed `FeatureAuthorizationHandler` → `FeatureAccessService` ACTIVE-subscription check. At parent commit only Create endpoints were gated. |
| F-04/H-04 Soft-delete filter | **PASS** | `AppDbContext.ApplyTenantQueryFilter` (L148–187, invoked after `ApplyConfigurationsFromAssembly`, L145) applies composed `e.TenantId == _currentTenant.TenantId && e.DeletedAtUtc == null` for Teacher/Student/Branch (last-wins supersedes per-config filters). `context.Teachers.ToListAsync()` **excludes** soft-deleted rows. Fix is production code, fail-closed tenant, no test workaround. |
| F-02/H-02 State machine | **PASS** | `SalaryPayment.Create` (L59–91) has no status parameter — only `Pending`/`PaidAt=null` creatable. `MarkPaid` (L93–107) rejects `Paid` (DuplicatePayment) and `Cancelled` (InvalidStatus); `Cancel` (L109–122) rejects `Paid`; repeated Cancel = documented no-op success. Handlers (`SalaryPaymentCommands.cs` L48/103/146) only call domain methods; no status in any command. Paid-with-null-PaidAt unreachable. |
| F-03/H-03 Concurrency | **PASS** | `[Timestamp] RowVersion` on `SalaryPayment` (L29–30) + `.IsRowVersion()` in `SalaryPaymentConfiguration.cs` (L57–58) — same pattern as Teacher/Student. Migration `20260903094745_AddSalaryPaymentRowVersion` + snapshot in sync; `has-pending-model-changes` = none. `DbUpdateConcurrencyException` → 409 localized ProblemDetails via registered `GlobalExceptionHandler` (L52–64; registered `DependencyInjection.cs` L73/L124) — not a 500. |

**DB/EF consistency: PASS** (config, snapshot, latest migration, zero pending model changes).

### Production code changes detected

Actual current changes under `src/` (commit `1e02168`):

1. `src/Centerix.API/Controllers/TeachersController.cs` — `[RequireFeature]` added to Update, Delete.
2. `src/Centerix.API/Controllers/SubjectsController.cs` — `[RequireFeature]` added to Update, Delete.
3. `src/Centerix.API/Controllers/TeacherSalaryConfigsController.cs` — `[RequireFeature]` added to Update, Delete.
4. `src/Centerix.API/Controllers/SalaryPaymentsController.cs` — `[RequireFeature]` added to MarkPaid, Cancel.
5. `src/Centerix.Infrastructure/Data/AppDbContext.cs` — composed tenant + soft-delete query filter for `SoftDeletableEntity` types (F-04 fix).
6. `src/Centerix.Domain/Teachers/SalaryPayments/SalaryPayment.cs` — state machine hardening + `[Timestamp] RowVersion` (F-02/F-03 fix).
7. `src/Centerix.Application/Teachers/SalaryPayments/Commands/SalaryPaymentCommands.cs` — Create command/handler no longer accept or forward status/paidAt.
8. `src/Centerix.Infrastructure/Data/Configurations/SalaryPaymentConfiguration.cs` — `RowVersion` `.IsRowVersion()`.
9. `src/Centerix.API/Infrastructure/GlobalExceptionHandler.cs` — `DbUpdateConcurrencyException` → 409 ProblemDetails.
10. `src/Centerix.API/Localization/en.json`, `src/Centerix.API/Localization/ar.json` — `Error:Concurrency` keys.
11. `src/Centerix.Infrastructure/Data/Migrations/20260903094745_AddSalaryPaymentRowVersion.cs` (+ Designer) and `AppDbContextModelSnapshot.cs` — SalaryPayment rowversion column.
12. (Incidental) `src/Centerix.Application/Students/.../CreateStudentValidator.cs` — phone max length 20 → 30.

### Test-only changes detected

Per the test-remediation report (all under `tests/`, none in `src/`):

1. `IgnoreQueryFilters()` in direct DI-scope reads (SQL concurrency tests — no tenant context in those scopes).
2. Unique `AcademicStage` test-ID allocation.
3. `StudentManagement` feature grant in test seed.
4. `SalaryPayments.Update` permission grant in test seed.
5. FK-correct Teacher seed (valid branch linkage).
6. Test URL correction (`/api/teacher/rratings` → `/api/teacherratings`).

### Remaining blockers

**None of the four Phase 5 High blockers remain unresolved.** All are remediated in production code and cross-verified (source + model + migration + exception path).

Residual items **outside the four-blocker scope** (pre-existing audit Medium/Low findings, business decisions, not re-adjudicated here):

- M-02: unique-constraint violation on SalaryPayment **create** (duplicate teacher/period) surfaces as 500, not 409 (distinct from the now-handled `DbUpdateConcurrencyException` path).
- M-03–M-06: documented business decisions (SalaryConfig semantics, Teacher↔User linkage, cancelled-period slot behavior, tombstone re-hire) pending product sign-off.
- L-01–L-06: low-severity audit items.
- Test-quality observations: loose HTTP-status assertions in the SQL 409 concurrency test and three deleted-teacher tests (documented in section 6).

### Recommendation

**REMEDIATION COMPLETE — READY FOR FINAL RE-VERIFICATION**

All four production blockers (F-01/H-01, F-02/H-02, F-03/H-03, F-04/H-04) are remediated in the current working tree, the EF model/snapshot/migration are consistent, and the 23/23 + 4/4 test results are consistent with (but not the evidence for) the remediation. Recommend a final independent re-verification pass of the four areas above; this status check neither approves the module nor waives that re-verification.

---

*Method notes: working tree verified clean at HEAD `1e02168`; pre-remediation baseline diffed via `git show 1fc2e57`; `dotnet ef migrations has-pending-model-changes --context AppDbContext` run read-only. No files were modified, created, or deleted except this report.*
