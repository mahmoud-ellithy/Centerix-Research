# CENTERIX — PHASE 5 TEACHERS AUDIT REPORT

> **Mode:** AUDIT ONLY — no source code, migration, test, or configuration was modified.
> **Source of truth:** Current working tree at commit `1fc2e57` ("feat(teacher-salary): add salary management module"), branch `main`.
> **Method:** Evidence-based inspection of domain, application (CQRS), API, authorization infrastructure, EF Core configurations, migration `20260902081027_AddTeacherSalaryModule`, `AppDbContextModelSnapshot`, and existing tests. Where static analysis was insufficient, behavior was verified **empirically at runtime** with a throwaway harness built **outside the repository** (TEMP directory, since deleted) against the real `Centerix.Domain`/`Centerix.Infrastructure` assemblies.
> **Date:** 2026-09-03.

---

## 1. Executive Summary

The Teachers module (Teachers, Subjects, TeacherSalaryConfigs, SalaryPayments, TeacherRatings) is structurally faithful to the Centerix baseline: clean layering, `Result<T>` returns, thin controllers, per-request permission resolution, tenant stamping on writes, audit rows on every write, a migration that matches the model snapshot exactly, and **zero pending model changes** (`dotnet ef migrations has-pending-model-changes` → "No changes have been made to the model since the last migration"). The build succeeds with 0 errors.

However, the module is **not safe to approve today**. Runtime verification performed during this audit produced three High-severity findings:

1. **H-01 — The soft-delete query filter is silently lost.** `AppDbContext.ApplyTenantQueryFilter` runs *after* `ApplyConfigurationsFromAssembly` and calls `HasQueryFilter` again per entity type. EF Core keeps only **one** filter per type — the last one wins. The effective filter on `Teacher` (verified at runtime: `e.TenantId == ... _currentTenant.TenantId`) is the tenant filter **only**; the `DeletedAtUtc == null` filter configured at `TeacherConfiguration.cs:61` is overwritten. Consequence, proven at runtime: after soft-delete, the teacher is **still returned by `GET /api/teachers` and `GET /api/teachers/{id}`**. (The same replacement affects `Student` and `Branch` — noted for awareness, not re-audited per scope rule 16.)
2. **H-02 — The SalaryPayment state machine can be bypassed.** `SalaryPayment.MarkPaid` only blocks `Paid → Paid`; a **Cancelled** payment can be marked Paid (verified at runtime: `Cancelled -> MarkPaid: success=True`), and `CreateSalaryPaymentHandler` forwards a client-supplied `Status`, so a payment can be created **directly in `Paid` state with `PaidAt = null`** (verified at runtime). The `CreateSalaryPaymentValidator` contains no rule restricting `Status`.
3. **H-03 — SalaryPayment has no concurrency control.** `SalaryPayment` is a plain `AuditableEntity<Guid>` with **no RowVersion**; `MarkSalaryPaymentPaidHandler`/`CancelSalaryPaymentHandler` do read-then-write with no transaction or version predicate. Two concurrent requests (e.g., `MarkPaid` and `Cancel`) both succeed and the **last write wins**, allowing a paid payment to be silently flipped to Cancelled (or vice versa) — an incorrect financial state.

Additionally, there are **zero dedicated Teachers tests** (the claim in MODULE-INVENTORY-20260903.md is confirmed), and the feature gate `[RequireFeature(FeatureCodes.TeacherManagement)]` is applied **only to Create** on all five Teachers controllers, while the approved Students pattern gates **every** write.

**Verdict: FAIL — REMEDIATION REQUIRED** (3 unresolved High findings; financial state and soft-delete invariants cannot currently be trusted, and no test exists to detect regressions).

---

## 2. Scope

Audited aggregates and their layers:

| Aggregate | Domain | Errors | CQRS | Validators | Controller | EF Config | Migration |
|---|---|---|---|---|---|---|---|
| Teachers | `Teacher.cs` | ✅ | C/U/D + 2 queries | C+U | ✅ 5 endpoints | ✅ | ✅ |
| Subjects | `Subject.cs` | ✅ | C/U/D + 2 queries | C+U | ✅ 5 endpoints | ✅ | ✅ |
| TeacherSalaryConfigs | `TeacherSalaryConfig.cs` | ✅ | C/U/D + 2 queries | C+U | ✅ 5 endpoints | ✅ | ✅ |
| SalaryPayments | `SalaryPayment.cs` | ✅ | C + MarkPaid + Cancel + 2 queries | C | ✅ 5 endpoints | ✅ | ✅ |
| TeacherRatings | `TeacherRating.cs` | ✅ | C + 1 query | C | ✅ 2 endpoints | ✅ | ✅ |

Also inspected: `AppDbContext` (DbSets, tenant filter, `StampAddedTenantIds`), `TenantGuardMiddleware`, `PermissionPolicyProvider`, `PermissionAuthorizationHandler`, `FeatureAuthorizationHandler`, `PermissionCatalog`, `ValidationBehavior`, `GlobalExceptionHandler`, `LimitService`, `FeatureAccessService`, `AuditWriter`, `AuditableEntityInterceptor`, `TestWebApplicationFactory`, and all files under `tests/`.

Out of scope (per rules): re-audit of the approved Students module, Groups aggregate (documented future FK), and re-verification of platform-level gaps already recorded in ARCHITECTURE-BASELINE.md (e.g., TD-2 fallback policy `null`).

---

## 3. Architecture Compliance

| Baseline expectation | Evidence | Verdict |
|---|---|---|
| Domain has no infra references; factories return `Result<T>` | `Teacher.Create` (`Teacher.cs:52-80`), `Subject.Create` (`Subject.cs:20-29`), `TeacherSalaryConfig.Create` (`TeacherSalaryConfig.cs:43-64`), `SalaryPayment.Create` (`SalaryPayment.cs:45-74`), `TeacherRating.Create` (`TeacherRating.cs:51-88`) | PASS |
| `IHasTenantId` on all aggregates | All five inherit `AuditableEntity<TId>` / `SoftDeletableEntity<TId>` (`AuditableEntity.cs:16-29, 64-77`) which implement `IHasTenantId` | PASS |
| EF config per aggregate in `Infrastructure/Data/Configurations` | `TeacherConfiguration.cs`, `SubjectConfiguration.cs`, `TeacherSalaryConfigConfiguration.cs`, `SalaryPaymentConfiguration.cs`, `TeacherRatingConfiguration.cs` | PASS |
| `DbSet<T>` registered | `AppDbContext.cs:110-114` | PASS |
| CQRS handlers under `Application/<Area>/Commands|Queries` returning `Result<T>` | Confirmed for all commands/queries; no handler returns raw types | PASS |
| Thin controllers dispatching MediatR only | All five controllers; no business logic in controllers | PASS |
| `[HasPermission]` on protected actions | Every Teachers endpoint carries exactly one permission policy (see §7) | PASS |
| Validators executed via pipeline | `ValidationBehavior<,>` registered **first** in `Application/DependencyInjection.cs:17`; `GlobalExceptionHandler.cs:22-46` maps `ValidationException` → 400 | PASS |
| Audit on writes | `auditWriter.WriteAsync(...)` present in every handler with old/new payloads | PASS |
| Definition of Done #7 — model matches migration | `dotnet ef migrations has-pending-model-changes --context AppDbContext` → **no pending changes** (executed during audit) | PASS |

**Deviation found:** Students gates *all* writes with `[RequireFeature]` (`StudentsController.cs:37,49,66`); Teachers gates only Create (see §6, M-01). MODULE-INVENTORY-20260903.md §B line 46 claims "Feature-gated … on write endpoints", which does not match the current tree.

---

## 4. Module-by-Module Findings

### 4.1 Teachers

**Create** (`TeachersController.cs:35-45` → `CreateTeacherCommand.cs`)

- Limit reservation (`LimitTypeCodes.Teachers`) is atomic and released on every failure path (`CreateTeacherCommand.cs:63-106`) — matches `LimitService` Teachers mapping (`LimitService.cs:111-115`). PASS.
- Branch existence is checked under the tenant filter (`CreateTeacherCommand.cs:69-76`). PASS.
- Tenant stamping via `StampAddedTenantIds` (`CreateTeacherCommand.cs:96`) + `TenantInterceptor` on the relational path. PASS.
- **No duplicate check.** `TeacherErrors.DuplicateUser` (`TeacherErrors.cs:40-41`) is dead code — never referenced by any handler. A duplicate `(TenantId, UserId)` insert relies solely on `UX_Teachers_TenantId_UserId`; on SQL Server it surfaces as an unhandled `DbUpdateException` → HTTP 500 (handler rethrows at `CreateTeacherCommand.cs:102-105`; `GlobalExceptionHandler.cs:48` → 500). Runtime evidence: EF InMemory does **not** enforce the unique index (duplicate saved without error), so the failure is invisible in the current test environment. → M-02.
- **`UserId` is accepted without any validation** beyond `NotEmpty` + length 450 (`CreateTeacherCommand.cs:30-32`). There is no lookup in `AspNetUsers`, no membership check for the current tenant, and `TeacherConfiguration` declares **no FK** to `AspNetUsers` (`TeacherConfiguration.cs:20-22` — only a column mapping). A tenant can therefore link a teacher to a nonexistent user, a garbage string, or a user belonging to another tenant. → M-04 / BUSINESS DECISION REQUIRED.
- **Soft-deleted rows can pass the "new teacher" path?** No — creation is always a new row; but re-creating a teacher for a user whose previous teacher row was soft-deleted is **permanently blocked** by the unfiltered unique index (see M-06).

**Read** (`GetTeachers.cs`)

- List and ById use `AsNoTracking()` under the global tenant filter (`GetTeachers.cs:19-42, 56-75`). Tenant isolation verified at runtime (tenant-2 sees 0 rows for tenant-1 data). PASS for isolation.
- **Soft-deleted teachers are returned** (H-01): verified at runtime — after `DeletedAtUtc` was set, `db.Teachers.ToList()` returned the deleted row and the ById-style query found it. The effective model filter on `Teacher` is tenant-only.

**Update** (`TeachersController.cs:47-61` → `UpdateTeacherCommand.cs`)

- Tenant lookup `FirstOrDefaultAsync(t => t.Id == request.Id)` (`UpdateTeacherCommand.cs:46-49`) is tenant-filtered (foreign teacher → 404). Domain `Update` refuses soft-deleted rows (`Teacher.cs:90-91` → 409). PASS.
- Branch change is tenant-checked (`UpdateTeacherCommand.cs:51-55`). PASS.
- `RowVersion` is a real concurrency token (`TeacherConfiguration.cs:58-59`), so concurrent updates throw `DbUpdateConcurrencyException` → 500 (no graceful 409 anywhere). Data is not silently lost. PARTIAL.
- **`UpdateTeacherCommand.UserId` is accepted, validated, then ignored** — `Teacher.Update` (`Teacher.cs:82-105`) has no `userId` parameter, so the client-sent `UserId` never changes the linked user. Contract mismatch → L-01.

**Delete** (`TeachersController.cs:63-72` → `DeleteTeacherCommand.cs`)

- Soft delete via domain (`Teacher.SoftDelete`, `Teacher.cs:119-128`), tenant-filtered lookup (foreign/never-existing → 404; already-deleted → 409). Limit slot released after save (`DeleteTeacherCommand.cs:43-45`, non-transactional → L-07). Audit written with old value. PASS except soft-delete visibility (H-01) and re-creation block (M-06).

**Domain invariants** (`Teacher.cs:130-160`): fullName ≤200, phone ≤30, qualification ≤200, yearsExp ≤100, enum-defined status — all enforced; verified at runtime (`YearsExp=101` rejected). Note three inconsistent bounds: validator 0-60 (`CreateTeacherCommand.cs:46-48`), domain ≤100 (`Teacher.cs:153-154`), error text "between 0 and 255" (`TeacherErrors.cs:28-29`). → L-02.

**User association / uniqueness intent:** `UX_Teachers_TenantId_UserId` is tenant-scoped, so **the same Identity user can be a teacher in multiple tenants** — plausibly intentional for tutors working across centers, but nothing in the docs states it. → BUSINESS DECISION REQUIRED (M-04).

### 4.2 Subjects

- **Uniqueness** — `UX_Subjects_TenantId_StageId_Name` is tenant-scoped (`SubjectConfiguration.cs:36-38`; migration lines 193-198). App-level duplicate check uses `IgnoreQueryFilters()` with an **explicit** `s.TenantId == currentTenant.TenantId` predicate (`CreateSubjectCommand.cs:50-58`) — tenant-safe and returns a proper 409 `Subject.DuplicateName`. A concurrent duplicate still surfaces as 500 on SQL Server (no catch) — same pattern as M-02, LOW here because the check usually wins.
- **Stage ownership** — `AcademicStages` lookup is tenant-filtered (`CreateSubjectCommand.cs:40-44`, `UpdateSubjectCommand.cs:43-47`); a foreign stage id → 404 `AcademicStageErrors.NotFound`. PASS at API level.
- **No DB FK to AcademicStages.** `SubjectConfiguration` maps `StageId` as a plain int (`SubjectConfiguration.cs:23-25`) with no `HasOne(...)`; the migration creates no FK for it (migration lines 29-32: PK only). Referential integrity is enforced only by handler checks → L-04.
- **No Arabic/English dual name** — `Subject.Name` is a single nvarchar(100). Students uses `FullNameAr`/`FullNameEn`; no dual-name rule exists or is documented for Subjects. Not implemented (observation; not a defect on current evidence).
- **Hard delete** — `DeleteSubjectHandler` calls `Remove()` (`DeleteSubjectCommand.cs:32`); `Subject` is `AuditableEntity<int>` with no soft-delete columns. Consistent with the unfiltered unique index; inconsistent with Teacher's soft-delete treatment → L-04.
- **Update does not re-check duplicates** — renaming a subject into an existing name in the same stage passes the handler and is stopped only by the DB unique index → 500 on SQL Server (part of M-02 family).
- Query/update/delete paths are tenant-filtered; foreign subject → 404. PASS.

### 4.3 TeacherSalaryConfigs

- **Create** (`TeacherSalaryConfigCommands.cs:40-86`): teacher existence is tenant-checked (`:49-53`); domain rules enforce `Value ∈ (0, 999999.99]`, `Percentage ≤ 100` (`TeacherSalaryConfig.cs:57-61`), matching `decimal(8,2)` storage (`TeacherSalaryConfigConfiguration.cs:31-33`). Audit written. PASS except the items below.
- **No overlap/conflict prevention whatsoever.** Neither domain (`TeacherSalaryConfig.Create/Update`) nor handler checks for an existing config with the same or overlapping `EffectiveFrom` for the teacher. The only index touching dates is `IX_TeacherSalaryConfigs_TeacherId_EffectiveFrom` (`TeacherSalaryConfigConfiguration.cs:49`; migration lines 267-271) — **non-unique**, so it is purely an ordering/lookup index and enforces **no invariant**. Two configs for the same teacher with the same `EffectiveFrom` are legal.
- **No "active config" semantics exist anywhere.** There is no query, service, or domain method that selects the configuration in effect for a period, and `SalaryPayment` holds **no reference** to any salary config (`SalaryPayment.cs:10-21`). `GetTeacherSalaryConfigsQuery` merely orders by `EffectiveFrom DESC` (`GetTeacherSalaryConfigs.cs:24-25`). The model is therefore an unordered history with no defined resolution rule → M-03 / BUSINESS DECISION REQUIRED.
- **Financial history is mutable and destructible.** `UpdateTeacherSalaryConfigHandler` can change `EffectiveFrom`, `SalaryType`, and `Value` arbitrarily (no immutability, no effective-dating rules) and `DeleteTeacherSalaryConfigHandler` hard-deletes rows (`:188`). No RowVersion on the entity → silent last-write-wins. → M-03; immutability flagged as BUSINESS DECISION.
- `GroupId` is a plain Guid with no FK — documented intentionally in the entity XML doc (`TeacherSalaryConfig.cs:12-17`) → INFO.
- Tenant isolation: CRUD lookups tenant-filtered; foreign config → 404. PASS.

### 4.4 SalaryPayments

- **Create** (`SalaryPaymentCommands.cs:35-83`): teacher existence tenant-checked (`:44-48`); amounts validated > 0 by validator and domain; month 1-12, year 2000-2100. **But `Status` is a client-supplied enum** that the validator does not constrain (`:23-33` has no Status rule) and the domain `Create` only checks `Enum.IsDefined` (`SalaryPayment.cs:70-71`) — so `Paid` and `Cancelled` can be created directly, including `Paid` with `paidAt: null` (handler always passes `paidAt: null`, `:58`). Verified at runtime. → H-02.
- **Duplicate payment prevention:** the handler performs **no** duplicate check; the only guard is `UX_SalaryPayments_Teacher_Period` (`SalaryPaymentConfiguration.cs:57-59`; migration lines 180-185; snapshot lines 2334-2336). The index is:
  - **not tenant-scoped by columns** — it is `(TeacherId, PeriodYear, PeriodMonth)`. Acceptable because `TeacherId` is a globally unique GUID whose tenant is fixed; a cross-tenant teacher id is impossible through the API (tenant-filtered existence check) and the FK targets `Platform.Teachers`. Tenant scope is therefore *implicit* via the teacher — PASS with note.
  - **unfiltered** — Cancelled rows occupy the slot forever. A cancelled payment for a period can never be replaced, and there is **no update or delete endpoint** for payments to correct amounts (`SalaryPaymentsController.cs` has only GET/POST/mark-paid/cancel). → M-05 / BUSINESS DECISION REQUIRED.
  - **surfaces as HTTP 500** — `SalaryPaymentErrors.DuplicatePayment` (`SalaryPaymentErrors.cs:31-32`) is only used by `MarkPaid` on an already-Paid payment; the create path never maps a unique violation to 409 → part of M-02 family.
- **State machine** (`SalaryPayment.cs:76-94`), verified at runtime:
  - `Pending → Paid` ✅ intended; `Pending → Cancelled` ✅ intended.
  - `Paid → Cancelled` blocked (`Cancel` returns `InvalidStatus` when Paid). ✅
  - **`Cancelled → Paid` ALLOWED** (`MarkPaid` only blocks `Paid`). ❌ H-02.
  - `Cancelled → Cancelled` re-cancel allowed (idempotent; resets `PaidAt=null`). Cosmetic.
  - There is **no `CancelledAt` field**; the cancellation timestamp is not persisted on the row (only in the audit-log payload) → L-03.
- **MarkPaid** sets `PaidAt = DateTime.UtcNow` server-side (`SalaryPaymentCommands.cs:107`) — the client cannot forge it. ✅ (But a Cancelled→Paid resurrection also receives a fresh `PaidAt`, laundering the record.)
- **Concurrency:** no RowVersion, no transaction, read-then-write in both handlers → last-write-wins. Concurrent `MarkPaid` + `Cancel` on the same payment can both succeed and the final state depends on commit order (e.g., a Paid payment silently becoming Cancelled with `PaidAt=null`, bypassing the domain's Paid→Cancelled guard, because each request read the original Pending state). → H-03.
- **No relationship to SalaryConfig** — payments neither reference nor compute from any configuration; amounts are free-form. BUSINESS DECISION (no documented intent either way).
- Tenant isolation: lookups tenant-filtered; foreign payment → 404. PASS.

### 4.5 TeacherRatings

- **Create** (`CreateTeacherRatingCommand.cs:37-90`): teacher existence tenant-checked (`:46-50`), **student existence tenant-checked** (`:52-56` → `StudentErrors.NotFound`). A Tenant-B teacher id or Tenant-B student id fails the filtered `AnyAsync` → 404. Cross-tenant creation is **rejected at the API boundary** → PASS (scenario D). Runtime evidence supports the filter mechanism (tenant filter proven working).
- **FKs exist and are Restrict** (`TeacherRatingConfiguration.cs:59-67`; migration lines 118-135, 207-210): `FK_TeacherRatings_Teachers_TeacherId`, `FK_TeacherRatings_Students_StudentId`. However both FKs are single-column; the **database** alone would accept a cross-tenant `(TeacherId, StudentId)` pair. Isolation rests on the handler checks + tenant query filter — a defense-in-depth note, identical to the approved Students pattern → INFO.
- **Immutability:** no Update/Delete domain methods and no endpoints — historical ratings cannot be modified or removed via the API. Consistent, intentional design → INFO (positive).
- **Rating range** 1-5 enforced by validator (`:30`) and domain (`TeacherRating.cs:67-68`). Comment ≤500. PASS.
- **Period semantics:** month 1-12, year 2000-2100, but future periods are allowed and there is no "current period" rule → BUSINESS DECISION.
- **Duplicate ratings:** index `IX_TeacherRatings_TenantId_TeacherId_PeriodYear_PeriodMonth` is **non-unique** (`TeacherRatingConfiguration.cs:55`); the same student can rate the same teacher multiple times in the same period. No duplicate rule exists → BUSINESS DECISION (L-06).
- Read path: `GetTeacherRatingsQuery` filters by `teacherId`/`studentId`, tenant-isolated, ordered by period DESC (`GetTeacherRatings.cs:19-49`). PASS.
- Soft-deleted teacher/student still pass the existence checks (tenant-only filter — H-01 root cause), so a rating can be attached to a soft-deleted teacher or student. Noted as fallout of H-01.

---

## 5. Tenant Isolation Assessment

**Mechanism (3 layers, all verified in the tree):**
1. `TenantGuardMiddleware` (`TenantGuardMiddleware.cs:59-82`): 403 unless Finbuckle resolves a tenant **and** the user holds an `Active` `TenantMembership` in it; only then `currentTenant.AuthorizeTenant()`. Tenant selection input is never trusted (`CurrentTenant.cs:22-27` — `TenantId` is empty until authorized, fail-closed).
2. Global query filter for every `IHasTenantId` entity (`AppDbContext.cs:148-172`) reading the **authorized** tenant live per request.
3. Write stamping: `StampAddedTenantIds(currentTenant.TenantId!)` in every create handler + `TenantInterceptor` on the relational path. A caller can never write into another tenant: the stamped value comes from the authorized context, not the payload.

**Runtime verification (harness, this audit):** teacher saved under `tenant-1` visible to `tenant-1` (1 row) and invisible to `tenant-2` (0 rows). Tenant filter itself: **PASS**.

| Relationship | Write-boundary protection | Verdict |
|---|---|---|
| Teacher → Branch | Tenant-filtered `AnyAsync` (`CreateTeacherCommand.cs:69-76`) + FK `FK_Teachers_Branches_BranchId` Restrict (migration 60-66) | PASS |
| Teacher → User/Identity | **No check** that the user exists or belongs to the tenant; no FK (M-04). `UX_Teachers_TenantId_UserId` prevents duplicate *within* tenant only | PARTIAL |
| TeacherSalaryConfig → Teacher | Tenant-filtered check (`TeacherSalaryConfigCommands.cs:49-53`) + FK Restrict (migration 159-165) | PASS |
| SalaryPayment → Teacher | Tenant-filtered check (`SalaryPaymentCommands.cs:44-48`) + FK Restrict (migration 91-97) | PASS |
| TeacherRating → Teacher / Student | Tenant-filtered checks (`CreateTeacherRatingCommand.cs:46-56`) + FKs Restrict (migration 118-135). DB-level FK alone would not stop cross-tenant ids — API check does | PASS (defense-in-depth note) |
| Subject → AcademicStage | Tenant-filtered check (`CreateSubjectCommand.cs:40-44`); **no DB FK** (L-04) | PARTIAL |

**Read path:** all queries/`FirstOrDefaultAsync` lookups run under the tenant filter; by-id access to another tenant's row → 404 (filter proven at runtime). **PASS.**

**The one tenant-escape candidate is `Teacher.UserId`** (a reference to a *global* Identity user, not tenant-partitioned data): tenant A can store Tenant B's (or a fabricated) user id as its teacher. No tenant-B data is read or exposed by this, but the association is unvalidated — M-04. Precedent: `Branch.ManagerId` is likewise a logical, FK-less reference (`BranchConfiguration.cs:31-35`), so this follows an existing documented pattern; the missing piece in Teachers is the *existence/membership validation*.

---

## 6. Feature Authorization Assessment

`FeatureCodes.TeacherManagement = "Teachers"` (`FeatureAuthorization.cs:71`). `RequireFeatureAttribute` → policy `Feature:Teachers` → `FeatureRequirement` → `FeatureAuthorizationHandler` (`FeatureAuthorization.cs:24-58`): **fail-closed**, PlatformAdmin bypass, resolves against the `TenantPlanFeatures` snapshot via `FeatureAccessService.HasFeatureAsync` (`FeatureAccessService.cs:18-33`, requires an ACTIVE subscription). `PermissionPolicyProvider` builds `Feature:` policies (`PermissionPolicyProvider.cs:32-38`).

Endpoint-by-endpoint evidence:

| Controller | Action | Permission | RequireFeature | Lines |
|---|---|---|---|---|
| TeachersController | GET list / GET by id | Teachers.Read | — | 14 / 25 |
| TeachersController | POST | Teachers.Create | **TeacherManagement** | 36-37 |
| TeachersController | PUT | Teachers.Update | **MISSING** | 48 |
| TeachersController | DELETE | Teachers.Delete | **MISSING** | 64 |
| SubjectsController | POST | Subjects.Create | TeacherManagement | 36-37 |
| SubjectsController | PUT / DELETE | Subjects.Update / Delete | **MISSING** | 48 / 64 |
| TeacherSalaryConfigsController | POST | TeacherSalaryConfigs.Create | TeacherManagement | 36-37 |
| TeacherSalaryConfigsController | PUT / DELETE | …Update / Delete | **MISSING** | 48 / 64 |
| SalaryPaymentsController | POST | SalaryPayments.Create | TeacherManagement | 36-37 |
| SalaryPaymentsController | mark-paid / cancel | SalaryPayments.Update | **MISSING** | 48 / 59 |
| TeacherRatingsController | POST | TeacherRatings.Create | TeacherManagement | 25-26 |

Compare the approved Students pattern: `StudentsController.cs:37, 49, 66` — **Create, Update and Delete all carry `RequireFeature(FeatureCodes.StudentManagement)`**. Teachers gates only Create, so a tenant whose plan lacks TeacherManagement but whose roles hold Teachers.* permissions can still update/delete teachers and subjects, mutate/delete salary configurations, and mark payments paid/cancelled. The realistic exposure window is "feature granted earlier, then revoked / plan downgraded" while an active subscription persists; suspension and expiry are separately blocked by the guard (403/402, `TenantGuardMiddleware.cs:102-124`). → **M-01** (inconsistency with the established baseline; not a cross-tenant risk).

Platform-level operations are not incorrectly blocked: PlatformAdmin bypasses both handlers (`FeatureAuthorization.cs:34-38`, `PermissionAuthorizationHandler.cs:76-81`), and Teachers permissions are tenant-scoped (absent from `Permissions.PlatformScope`, `Permissions.cs:267-287`). Read vs write asymmetry (reads never feature-gated) matches Students — intentional.

---

## 7. Permission Authorization Assessment

Constants exist for every used code (`Permissions.cs:94-129`) and every code is registered in `PermissionCatalog` (`PermissionCatalog.cs:58-78`, including the description "Mark a salary payment as paid/cancelled" for `SalaryPayments.Update` — exactly how MarkPaid/Cancel use it). **No missing, wrong, or mismatched permission was found; no endpoint is authentication-only** (every action has a `HasPermission` policy; the known platform gap TD-2 — `GetFallbackPolicyAsync()` returning null — is irrelevant here because explicit policies are always present).

Actual enforcement chain (not just declaration): `HasPermissionAttribute` = `AuthorizeAttribute(permission)` (`HasPermissionAttribute.cs:6`) → `PermissionPolicyProvider.GetPolicyAsync` → `PermissionRequirement` → `PermissionAuthorizationHandler` (`PermissionPolicyProvider.cs:67-160`), which reads the `HttpContext.Items["TenantPermissions"]` snapshot loaded by `TenantGuardMiddleware` (`TenantGuardMiddleware.cs:84-98`) with a DB fallback and **fail-closed** exception handling (`:150-159`). Delete endpoints use the correct `*.Delete` permissions; no command/endpoint permission mismatch was found.

---

## 8. Validation Assessment

Validators found (all `AbstractValidator<TCommand>`, co-located with commands): `CreateTeacherValidator` / `UpdateTeacherValidator` (`CreateTeacherCommand.cs:26-50`, `UpdateTeacherCommand.cs:25-36`), `CreateSubjectValidator` / `UpdateSubjectValidator` (`CreateSubjectCommand.cs:18-29`, `UpdateSubjectCommand.cs:19-27`), `CreateTeacherSalaryConfigValidator` / `UpdateTeacherSalaryConfigValidator` (`TeacherSalaryConfigCommands.cs:22-38, 95-111`), `CreateSalaryPaymentValidator` (`SalaryPaymentCommands.cs:23-33`), `CreateTeacherRatingValidator` (`CreateTeacherRatingCommand.cs:24-35`).

**Execution is verified, not assumed:** `AddValidatorsFromAssembly` (`Application/DependencyInjection.cs:24`) registers them; `ValidationBehavior<,>` is the **first** MediatR behavior (`:17`) and throws `ValidationException` on failure; `GlobalExceptionHandler.cs:22-46` converts it to HTTP 400. Every Teachers command that carries client input passes through it (route-bound ids in Delete/MarkPaid/Cancel need no validator).

Gaps found:

1. **`CreateSalaryPaymentValidator` does not restrict `Status`** — combined with the domain check being only `Enum.IsDefined`, a client can create a payment directly in `Paid` (with `PaidAt = null`) or `Cancelled`. Part of H-02.
2. **`CreateTeacherSalaryConfigValidator` does not validate `EffectiveFrom`** — a `default(DateOnly)` (0001-01-01) passes the validator; the error `TeacherSalaryConfigErrors.EffectiveFromRequired` (`TeacherSalaryConfigErrors.cs:22-23`) is unused. Covered under M-03.
3. **YearsExp bound mismatch**: validator 0-60 (`CreateTeacherCommand.cs:46-48`), domain ≤100 (`Teacher.cs:153-154`), error text "between 0 and 255" (`TeacherErrors.cs:28-29`). → L-02.
4. No business rule is mistakenly placed only in a validator; duplicate-subject and state-transition rules live in handlers/domain as intended.

---

## 9. Database / EF Assessment

| Config item | Teacher | Subject | TeacherSalaryConfig | SalaryPayment | TeacherRating |
|---|---|---|---|---|---|
| Table / schema | Platform.Teachers | Platform.Subjects | Platform.TeacherSalaryConfigs | Platform.SalaryPayments | Platform.TeacherRatings |
| PK | TeacherId (client-generated) | SubjectId identity | ConfigId identity | PaymentId (client-generated) | RatingId (client-generated) |
| Soft-delete columns + filter | Yes — filter **overwritten** (H-01) | None (hard delete) | None (hard delete) | None | None |
| RowVersion | Yes (`TeacherConfiguration.cs:58-59`) | No | No | **No** (H-03) | No |
| Tenant column | nvarchar(450) required | ✅ | ✅ | ✅ | ✅ |
| Unique indexes | `UX_Teachers_TenantId_UserId` (unfiltered) | `UX_Subjects_TenantId_StageId_Name` (unfiltered) | — | `UX_SalaryPayments_Teacher_Period` (unfiltered) | — |
| Other indexes | TenantId; (TenantId,BranchId); (TenantId,Status) | (TenantId,StageId) | TenantId; (TeacherId,EffectiveFrom) non-unique; GroupId | (TenantId,PeriodYear,PeriodMonth); (TenantId,Status) | (TenantId,TeacherId,PeriodYear,PeriodMonth); (TenantId,StudentId); GroupId; StudentId; TeacherId |
| FKs | Branch (Restrict) | **None to AcademicStages** | Teacher (Restrict) | Teacher (Restrict) | Teacher + Student (Restrict) |
| Decimal precision | — | — | Value decimal(8,2) — matches 999999.99 cap | Gross/Net decimal(10,2) — no app-level cap | — |

Observations:
- `UX_SalaryPayments_Teacher_Period` and `UX_Teachers_TenantId_UserId` carry **no filter predicate** — tombstones block re-creation (M-05, M-06). No filtered index exists anywhere in the Teachers module.
- `Teacher.UserId` has **no FK** to `AspNetUsers` (nvarchar(450) vs uniqueidentifier type mismatch; same rationale documented for `Branch.ManagerId`) → M-04.
- `GroupId` on TeacherRating and TeacherSalaryConfig is intentionally FK-less (documented in code) → INFO.
- `PaidAt` is `datetime2` while audit stamps are `datetimeoffset` (L-03).
- Missing RowVersion on SalaryPayment is the financially significant gap (H-03); on Subject/Config/Rating it is a lower-severity last-write-wins issue.

---

## 10. Migration / ModelSnapshot Assessment

Migration introducing the module: **`20260902081027_AddTeacherSalaryModule.cs`**.

Verified table-by-table against `AppDbContextModelSnapshot.cs` (Teachers entities at lines 2280-2560) and the EF configurations:

| Element | Migration lines | Snapshot lines | Config lines | Match |
|---|---|---|---|---|
| Subjects table + PK + identity | 14-32 | 2343-2390 | SubjectConfiguration | ✅ |
| `UX_Subjects_TenantId_StageId_Name` unique | 193-198 | 2385-2387 | 36-38 | ✅ |
| Teachers table (incl. rowversion RowVersion, DeletedAt/DeletedBy) | 34-67 | 2514-2560+ | TeacherConfiguration | ✅ |
| `UX_Teachers_TenantId_UserId` unique | 254-259 | index section | 64-66 | ✅ |
| SalaryPayments table + `UX_SalaryPayments_Teacher_Period` unique (TeacherId, PeriodYear, PeriodMonth), **no filter** | 69-98, 180-185 | 2280-2341 (index 2334-2336) | 57-59 | ✅ |
| TeacherSalaryConfigs table + `IX_TeacherSalaryConfigs_TeacherId_EffectiveFrom` non-unique | 138-166, 267-271 | 2456-2512 | 48-50 | ✅ |
| TeacherRatings table + FKs (Restrict) + `IX_TeacherRatings_*` | 100-136, 200-228 | 2392-2454 | TeacherRatingConfiguration | ✅ |
| FK delete behaviors (all Restrict) | 60-66, 91-97, 118-135, 159-165 | matching | matching | ✅ |

Additional tooling evidence (executed during audit): `dotnet ef migrations has-pending-model-changes --context AppDbContext` → **"No changes have been made to the model since the last migration."** → **No model/migration drift.** No missing columns, no wrong FK behavior, no incorrect uniqueness scope across the three artifacts. The migration was not executed against a live SQL Server in this audit (no Teachers test exists that would do so; see §13).

---

## 11. Concurrency / Transactions

| Entity | Concurrency token | Handling in handlers | Risk |
|---|---|---|---|
| Teacher | `RowVersion` rowversion, real token (`TeacherConfiguration.cs:58-59`) | None — `DbUpdateConcurrencyException` would propagate → 500 (no graceful 409) | Lost updates **prevented** by token; poor UX only |
| SalaryPayment | **None** | Read-then-write in MarkPaid/Cancel; no transaction | **H-03** — state flips via last-write-wins |
| TeacherSalaryConfig | **None** | Read-then-write Update/Delete | Silent lost updates on financial config (part of M-03) |
| Subject / TeacherRating | **None** | n/a (single-writer semantics; ratings immutable) | LOW |

Duplicate-payment race: two concurrent `CreateSalaryPayment` for the same teacher/period — both pass the handler checks; on SQL Server the second insert is rejected by `UX_SalaryPayments_Teacher_Period` (data protected), but the response is an unhandled 500 (no `DbUpdateException` → `DuplicatePayment` mapping). On the InMemory test provider **the unique index is not enforced at all** (verified at runtime — duplicate saved silently), so no test on the current stack can prove this invariant.

Transaction boundaries: no multi-entity write in any Teachers handler requires a transaction *except* the implicit pairing of business save + limit counter + audit row:
- `CreateTeacherHandler`: reserve limit (atomic `ExecuteUpdate`) → save teacher → (on failure) release limit → audit. The reserve and the teacher insert are **not** in one transaction; the compensating `ReleaseAsync` on failure paths is correct, but a crash between save and release drifts the counter. Same pattern in `DeleteTeacherHandler` (save → release → audit). Logged-and-swallowed failures in `LimitService.ReleaseAsync` (`LimitService.cs:173-176`) and `AuditWriter` (`AuditWriter.cs:81-86`) keep the business operation alive by design.
- `MarkPaid`/`Cancel`: single-row updates; no transaction needed **once** a concurrency token exists (H-03).

`AppDbContext.BeginTransactionAsync` exists (`IAppDbContext.cs:120-124`) but is not used by any Teachers handler.

---

## 12. Auditing

All Teachers write operations write tenant-scoped `AuditLog` rows via `AuditWriter.WriteAsync` (`AuditWriter.cs:24-86`), which stamps the **authorized tenant** and the **authenticated user**, stores old/new JSON payloads, and is deliberately non-blocking on failure:

| Operation | Handler evidence | Old value | New value |
|---|---|---|---|
| Teacher.Create | `CreateTeacherCommand.cs:108-119` | — | FullName/UserId/BranchId/Status |
| Teacher.Update | `UpdateTeacherCommand.cs:57-92` | ✅ | ✅ |
| Teacher.Delete (soft) | `DeleteTeacherCommand.cs:29-52` | ✅ | — |
| Subject C/U/D | `CreateSubjectCommand.cs:64-73`, `UpdateSubjectCommand.cs:61-71`, `DeleteSubjectCommand.cs:35-40` | ✅ (U/D) | ✅ (C/U) |
| SalaryConfig C/U/D | `TeacherSalaryConfigCommands.cs:70-82, 146-159, 191-196` | ✅ (U/D) | ✅ (C/U) — includes Value/EffectiveFrom |
| SalaryPayment.Create / MarkPaid / Cancel | `SalaryPaymentCommands.cs:67-80, 113-123, 156-166` | ✅ (status transitions) | ✅ |
| TeacherRating.Create | `CreateTeacherRatingCommand.cs:75-87` | — | ✅ |

Additional automatic stamping: `AuditableEntityInterceptor` (`AuditableEntityInterceptor.cs:31-80`) sets `CreatedAt/CreatedBy`, `ModifiedAt/ModifiedBy`, and `DeletedBy` on soft-delete for all five aggregates.

Gaps: no audit row is written when limit reservation/release compensates (counters are auditable only indirectly); the payment `Cancel` timestamp is not persisted on the row (L-03) though the Cancel action is audited; audit failures are swallowed by design (documented in `AuditWriter.cs:14-15`). Overall: **adequate coverage; sensitive salary/payment changes are auditable.**

---

## 13. Test Coverage

Dedicated Teachers tests in the current working tree: **ZERO** (confirmed).

Evidence: `tests/Centerix.SecurityTests/` contains 20 test-source files (23 entries including 3 `obj/` generated files); a full-text search for `Teacher|Subject|SalaryPayment|TeacherRating|TeacherSalaryConfig` finds only `maxTeachers = 10` / `teachersCount: 0` plan-limit seeds (Phase2AuthorizationHttpTests.cs:177, Phase2ClosurePlanCatalogTests.cs:164, Phase3AuthorizationHttpTests.cs:180, :258). No test instantiates any Teachers entity, calls any Teachers endpoint, or touches any Teachers table. MODULE-INVENTORY-20260903.md §B line 55 itself flags "⚠️ NO TEST COVERAGE".

What exists at the *generic* level (benefits Teachers incidentally, but does not exercise Teachers behavior): TenantGuard/authorization/limit/expiry HTTP suites (Phase2/Phase3) run against Students and lookups only. Note the Phase-4 remediation tests referenced in `docs/PHASE-4-*.md` are **not present** in the current tree.

Highest-value missing tests (priority order):
1. **HTTP + SQL Server: soft-deleted teacher is NOT returned** by `GET /api/teachers` and `GET /api/teachers/{id}` — would have caught H-01.
2. **SQL Server: unique-index tests** — duplicate `(TenantId, UserId)` teacher → expect 409 (currently 500); duplicate subject per (tenant, stage, name) → 409; duplicate payment per teacher/period → 409; **cancel-then-recreate payment for same period** (documents M-05 behavior).
3. **Domain: SalaryPayment state machine** — Paid→Cancelled rejected; **Cancelled→Paid rejected** (currently allowed, H-02); create-as-Paid rejected.
4. **HTTP: feature-gate matrix** — tenant without TeacherManagement → 403 on **every** Teachers write (currently only Create would pass).
5. **HTTP cross-tenant**: Tenant-A token + Tenant-B teacher/subject/student ids → 404 on create/update/delete/rating.
6. **Concurrency**: parallel MarkPaid vs Cancel against SQL Server (Testcontainers) → exactly one wins, no invalid state (H-03).
7. **Migration/schema test** asserting the four unique/FK/index definitions exist in the deployed schema.
8. **Permission matrix**: user without `Teachers.Delete` → 403, etc.

---

### 13.1 SQL Server vs InMemory

No Teachers test exists at all, so nothing *currently* claims SQL Server correctness via InMemory — but the shared test factory (`TestWebApplicationFactory`, InMemory) would mask the following if Teachers tests were added naively:

| Behavior | InMemory reality | Verified during audit |
|---|---|---|
| Unique indexes (`UX_Teachers_TenantId_UserId`, `UX_Subjects_*`, `UX_SalaryPayments_Teacher_Period`) | **Not enforced** — duplicate teacher with same (TenantId, UserId) saved without error | ✅ Runtime duplicate saved silently |
| Filtered unique indexes | Not supported (no Teachers index is filtered today) | n/a |
| FK constraints | Not enforced | By provider design |
| RowVersion / optimistic concurrency | Simulated differently | — |
| `ExecuteUpdateAsync` (limit reservation) | Unsupported → read-only fallback path in `LimitService` (`LimitService.cs:64-76, 141-142`) | Documented in code |
| Transactions | No-op / isolated | By provider design |
| Decimal precision truncation | Not applied | By provider design |

`LimitService` explicitly documents the InMemory divergence ("On the EF InMemory test provider ExecuteUpdate is unsupported… true multi-writer behavior is proven against SQL Server by the integration suite", `LimitService.cs:64-67`) — however, **no SQL Server integration test exercises the Teachers counter**, so that proof does not yet exist for Teachers.

---

### 13.2 Subscription / Limits / Expiry Enforcement

- **Suspension/deactivation:** `TenantGuardMiddleware.cs:102-108` → 403 before any controller runs. Cannot be bypassed by calling handlers directly through HTTP.
- **Expiry:** `TenantGuardMiddleware.cs:110-124` → 402 when `ValidUpTo` is past. Direct handler invocation outside HTTP is not a reachable surface (MediatR handlers are only dispatched by controllers; there is no public background/API surface invoking Teachers handlers).
- **Subscription state:** `LimitService.ReserveAsync` fails closed without an active subscription (`LimitService.cs:54-62`); `FeatureAccessService.HasFeatureAsync` requires an active subscription (`FeatureAccessService.cs:23-25`).
- **Teacher limit:** `LimitTypeCodes.Teachers` exists (`LimitTypeCodes.cs:12`), `TenantUsageCounter.TeachersCount` mapping exists (`LimitService.cs:111-115, 166-170`), snapshot limit `MaxTeachers` is part of the plan snapshot (`TenantPlan.cs:173-177` area). Create reserves and delete releases; no other Teachers operation consumes limits (subjects/configs/payments/ratings are unlimited — no documented limit types exist for them).
- **Bypass check:** none of the five Teachers commands performs its own tenant resolution; all read `ICurrentTenant` (authorized context). No command handler can be invoked through HTTP without passing the guard + authorization metadata first.

---

## 14. Security Abuse Cases

| # | Scenario | Verdict | Evidence |
|---|---|---|---|
| A | Tenant A user sends Tenant B tenant header/subdomain | **PASS** | `TenantGuardMiddleware.cs:66-80` — 403 unless an `Active` `TenantMembership` exists for the resolved tenant; the resolved tenant is selection input only, membership is DB-checked |
| B | Tenant A submits Tenant B TeacherId (update/delete/config/payment/rating) | **PASS** | All lookups are `FirstOrDefault/Any` under the tenant filter (`UpdateTeacherCommand.cs:46-49`, `TeacherSalaryConfigCommands.cs:49-53`, `SalaryPaymentCommands.cs:44-48`, `CreateTeacherRatingCommand.cs:46-50`) → 404; filter mechanism runtime-proven |
| C | Tenant A submits Tenant B SubjectId | **PASS** | `UpdateSubjectCommand.cs:38-41`, `DeleteSubjectCommand.cs:21-24`, `GetSubjectByIdHandler` — tenant-filtered → 404 |
| D | Tenant A submits Tenant B StudentId when creating TeacherRating | **PASS** | `CreateTeacherRatingCommand.cs:52-56` — student existence checked under tenant filter → 404; FK is Restrict but single-column (DB alone would not prevent; the API check does) |
| E | Tenant without TeacherManagement calls Teacher endpoints | **PARTIAL** | Create → 403 (`RequireFeature` on the 5 Create endpoints). Update/Delete/mark-paid/cancel → allowed if permissions held (`TeachersController.cs:48,64`; `TeacherSalaryConfigsController.cs:48,64`; `SalaryPaymentsController.cs:48,59`) → M-01. Reads allowed (matches Students) |
| F | User without required permission calls Teacher endpoints | **PASS** | Every endpoint carries `[HasPermission]`; `PermissionAuthorizationHandler` fail-closed (`PermissionPolicyProvider.cs:150-159`); permissions resolved per-request, not from JWT |
| G | User attempts to update/delete soft-deleted or foreign records | **PARTIAL** | Foreign → 404 (filters). Soft-deleted → domain guards return `AlreadyDeleted` 409 (`Teacher.cs:90-91,121-122`). **But soft-deleted teachers remain readable** (H-01) and ratings can be created for soft-deleted teacher/student |
| H | Duplicate salary payment for same teacher/period | **PARTIAL** | SQL Server: blocked by `UX_SalaryPayments_Teacher_Period` (data safe) but returns 500, not 409 (`DuplicatePayment` error unused on create path); InMemory: **not enforced** (runtime-verified); no app-level pre-check |
| I | Concurrent MarkPaid / Cancel | **FAIL** | No RowVersion on SalaryPayment; read-then-write, no transaction → last-write-wins can flip Paid→Cancelled/Cancelled→Paid (H-03) |
| J | User manipulates SalaryConfig effective dates | **FAIL (no invariant exists to break)** | Unlimited overlapping/same-date configs; history freely editable and deletable; non-unique `(TeacherId, EffectiveFrom)` index enforces nothing (M-03, BUSINESS DECISION) |
| K | Duplicate subject within same tenant/stage | **PASS** | App check → 409 `Subject.DuplicateName` (`CreateSubjectCommand.cs:50-58`); DB unique index backstop (500 on race) |
| L | Duplicate Teacher/User association | **PARTIAL** | SQL Server: blocked by `UX_Teachers_TenantId_UserId` → but 500 not 409; `TeacherErrors.DuplicateUser` dead code; soft-deleted tombstone permanently blocks re-creation; InMemory silent (M-02, M-06) |

---

## 15. Findings Register

| ID | Severity | Area | Finding | Evidence | Impact | Recommendation |
|---|---|---|---|---|---|---|
| H-01 | HIGH | EF / Soft delete | Global tenant filter **replaces** the configuration-level soft-delete filter (`HasQueryFilter` called twice per entity; last wins). Effective `Teacher` filter is tenant-only; soft-deleted teachers are returned by list and by-id reads | `AppDbContext.cs:144-172` (configurations at :144, filter overwritten at :145→:168-172); `TeacherConfiguration.cs:61`; runtime: deleted teacher returned (list count 2, ById true) | Deleted teachers' PII (name/phone) remains API-visible; delete contract broken; downstream existence checks treat tombstones as live | Combine filters per soft-deletable type (`TenantId == x && DeletedAtUtc == null`) or use EF Core 10 named query filters; add read-after-delete regression test |
| H-02 | HIGH | SalaryPayments domain | State machine allows `Cancelled → Paid` and direct creation in `Paid`/`Cancelled` (with `PaidAt = null`); validator does not constrain `Status` | `SalaryPayment.cs:76-84` (MarkPaid only blocks Paid), `:45-74` (Create accepts any defined status); `SalaryPaymentCommands.cs:50-58` (forwards client status), validator `:23-33`; runtime: `Cancelled -> MarkPaid: success=True`, create-as-Paid allowed | Incorrect financial state; a cancelled payment can be "resurrected" as paid with a fresh server timestamp | Forbid `Cancelled → Paid` in `MarkPaid`; restrict `Create` to `Pending` (validator + domain) |
| H-03 | HIGH | SalaryPayments concurrency | No RowVersion/concurrency token on `SalaryPayment`; MarkPaid/Cancel are read-then-write without a transaction → concurrent transitions both succeed, last write wins | `SalaryPaymentConfiguration.cs` (no `IsRowVersion`); `SalaryPaymentCommands.cs:96-111, 139-154` | A Paid payment can be silently flipped to Cancelled (and vice versa), bypassing the domain guard evaluated on stale state | Add `RowVersion` token; map `DbUpdateConcurrencyException` → 409; or use conditional `ExecuteUpdate` on status |

| M-01 | MEDIUM | Feature authorization | `[RequireFeature(TeacherManagement)]` only on Create across all 5 controllers; Update/Delete/MarkPaid/Cancel ungated — inconsistent with Students which gates all writes | Controller table in §6; `StudentsController.cs:37,49,66` | Commercial entitlement gap after downgrade (active subscription, feature revoked) on financially sensitive mutations | Add `[RequireFeature(FeatureCodes.TeacherManagement)]` to all Teachers write endpoints |
| M-02 | MEDIUM | Duplicate handling UX | Duplicate-teacher/duplicate-payment conflicts rely on DB unique indexes only and surface as HTTP 500; `TeacherErrors.DuplicateUser` is dead code | `CreateTeacherCommand.cs` (no dup check); `TeacherErrors.cs:40-41` (unused); `GlobalExceptionHandler.cs:48`; `SalaryPaymentErrors.cs:31-32` unused on create path | Clients receive 500 instead of 409; inconsistent with Subject create which pre-checks properly | Pre-check duplicates tenant-scoped (like Subjects) and/or map SQL errors 2601/2627 to 409 |
| M-03 | MEDIUM | SalaryConfig business rules | No overlap prevention; no active-config resolution logic; `(TeacherId, EffectiveFrom)` index non-unique (ordering only); config history mutable and deletable; `EffectiveFromRequired` error unused | `TeacherSalaryConfigConfiguration.cs:48-50`; `GetTeacherSalaryConfigs.cs:24-25`; `TeacherSalaryConfigErrors.cs:22-23`; no consumer code exists | Ambiguous "current salary" once multiple configs exist; silent financial-history edits | **BUSINESS DECISION REQUIRED**: define active-config semantics; enforce via unique/overlap checks + immutability policy |
| M-04 | MEDIUM | Teacher ↔ User | `UserId` accepted with no existence/membership validation and no FK; a teacher can be linked to a nonexistent or foreign-tenant user; same user may be teacher in multiple tenants | `CreateTeacherCommand.cs:30-32`; `TeacherConfiguration.cs:20-22` (no FK); no user lookup in handler | Teacher records not tied to verified users; future portal features would bind to wrong accounts | **BUSINESS DECISION REQUIRED**: require existing Identity user (with tenant membership, or document global-teacher intent); validate existence at minimum |
| M-05 | MEDIUM | SalaryPayments lifecycle | `UX_SalaryPayments_Teacher_Period` is unfiltered: Cancelled payments permanently occupy the teacher/period slot; no payment update/delete path exists to correct mistakes | `SalaryPaymentConfiguration.cs:57-59`; controller surface in §6 | A mistyped or cancelled payment can never be replaced for that period | **BUSINESS DECISION REQUIRED**: filter the index on non-cancelled status (new migration) or document cancel-as-terminal + correction path |
| M-06 | MEDIUM | Teacher soft-delete coherence | Soft-deleted teacher row keeps `UX_Teachers_TenantId_UserId` occupied → the same user can never be re-added as a teacher in that tenant, while the tombstone is still visible (H-01) and the limit slot was released | `TeacherConfiguration.cs:64-66` (unfiltered unique); `DeleteTeacherCommand.cs:43-45` | Delete permanently blocks re-hiring the same user; counter permits replacement but index forbids it | Decide tombstone policy: filter the unique index on `DeletedAt` (migration) or intentionally block re-use |

| L-01 | LOW | API contract | `UpdateTeacherCommand.UserId` accepted/validated but silently ignored by domain `Update` | `UpdateTeacherCommand.cs:17`; `Teacher.cs:82-105` (no userId param) | Client confusion; false impression the linked user can change | Remove `UserId` from the update contract or implement validated re-linking |
| L-02 | LOW | Validation consistency | YearsExp bounds differ: validator 0-60, domain ≤100, error text "0 to 255" | `CreateTeacherCommand.cs:46-48`; `Teacher.cs:153-154`; `TeacherErrors.cs:28-29` | Confusing validation messages | Align the three bounds |
| L-03 | LOW | SalaryPayments auditability | No `CancelledAt` column; `PaidAt` datetime2 vs datetimeoffset audit stamps | `SalaryPayment.cs:19`; `SalaryPaymentConfiguration.cs:45-46` | Cancellation time only in audit log, not on the financial row | Add `CancelledAt` (or document omission) |
| L-04 | LOW | Subjects referential integrity | No DB FK from Subjects to AcademicStages; hard delete vs Teacher's soft delete | `SubjectConfiguration.cs:23-25` (no relationship); migration 29-32 (PK only) | Orphaned StageId possible via non-API writes; lifecycle inconsistency | Add FK Restrict or document; align delete semantics |
| L-05 | LOW | Amount bounds | Payment amounts have no upper bound; `decimal(10,2)` overflow → 500; no Net ≤ Gross rule | `SalaryPaymentCommands.cs:30-31` (only >0); `SalaryPaymentConfiguration.cs:32-38` | 500 on extreme values; economically invalid payments accepted | Bound amounts to column precision; **BUSINESS DECISION**: enforce Net ≤ Gross |
| L-06 | LOW | TeacherRatings duplicates | No duplicate-rating rule; composite period index non-unique (performance only) | `TeacherRatingConfiguration.cs:55` | Same student/teacher/period can accumulate unlimited ratings | **BUSINESS DECISION**: one-per-period or allow multiples |
| L-07 | LOW | Transactions | Limit release not transactional with soft-delete save (crash window drifts TeachersCount) | `DeleteTeacherCommand.cs:41-45` | Usage counter drift (commercial only) | Wrap save+release in a transaction or reconcile counters |
| L-08 | LOW | Concurrency (non-financial) | Subject/TeacherSalaryConfig/TeacherRating lack concurrency tokens → silent last-write-wins on concurrent updates | Configurations (§9 table) | Rare lost updates on non-financial rows | Add RowVersion when concurrent editing becomes real |
| I-01 | INFO | Tests | Zero dedicated Teachers tests (domain, HTTP, SQL Server, migration) | §13 | Invariants unverifiable; regressions undetectable | Implement the priority list in §13 |
| I-02 | INFO | Feature catalog | No seeded `Feature` row with Code "Teachers"; features are operator-created at runtime | No static seed in `Program.cs`/`Extensions.cs`; tests seed via `EnsureFeatureOnPlanAsync` (Phase3AuthorizationHttpTests.cs:275-288) | Fresh environments lack TeacherManagement until provisioned | Document provisioning requirement |
| I-03 | INFO | Groups placeholder | `GroupId` intentionally FK-less pending the Groups aggregate | `TeacherSalaryConfig.cs:12-17`; `TeacherRating.cs:13-18` | Documented tech debt | Keep; add FK with M-03 Schedule |
| I-04 | INFO | Docs drift | MODULE-INVENTORY claims feature gating "on write endpoints" — the tree gates only Create | `MODULE-INVENTORY-20260903.md:46` vs §6 table | Documentation overstates enforcement | Update inventory (or code per M-01) |
| I-05 | INFO | Test provider | InMemory does not enforce unique indexes — proven by runtime duplicate insert | §13.1 table; runtime output | InMemory-green ≠ SQL Server-correct | Prefer Testcontainers SQL Server for invariant tests |
| I-06 | INFO | Ratings design | Ratings are immutable (no update/delete endpoints or domain mutators) — intentional yes to "should ratings be immutable" | `TeacherRatingsController` (GET/POST only); `TeacherRating.cs` (no mutators) | Positive design property | None |

---

## 16. Required Remediation

### Blocking

Must be fixed before formal approval (each is a High finding on a core invariant):

1. **H-01** — Restore the soft-delete filter for `Teacher` (and, at the platform's discretion, every `SoftDeletableEntity`): combine the tenant filter with `DeletedAtUtc == null` in `AppDbContext.ApplyTenantQueryFilter` (or use EF Core 10 named query filters). Add a read-after-delete regression test.
2. **H-02** — Close the SalaryPayment state machine: reject `Cancelled → Paid` in `SalaryPayment.MarkPaid`; restrict creation to `Pending` (validator rule + domain check rejecting `Status != Pending`); ensure `PaidAt` is only ever set by `MarkPaid`.
3. **H-03** — Add optimistic concurrency to `SalaryPayment` (RowVersion) and handle `DbUpdateConcurrencyException` with 409; alternatively make MarkPaid/Cancel conditional atomic updates. Prove with a Testcontainers SQL Server concurrency test.

### Non-blocking

Should be fixed before or shortly after approval; none creates immediate critical exposure:

- M-01 — Apply `[RequireFeature(TeacherManagement)]` to all Teachers write endpoints (align with Students).
- M-02 — Map duplicate/unique violations to 409 (`DuplicateUser`/`DuplicatePayment` errors exist but are unused); pre-check duplicates like Subjects does.
- M-06 — Decide and implement teacher tombstone policy (filtered unique index vs intentional block).
- L-01 … L-08 — Contract and consistency cleanups listed in the register.

### Business Decisions

Intent is genuinely unclear from the current documentation/implementation — **not** classified as bugs:

1. **M-03** — Salary-configuration semantics: is validity `EffectiveFrom → next config`? Must two configs share a date? Must history be immutable? Is the payment expected to reference the config in effect?
2. **M-04** — May the same Identity user be a teacher in multiple tenants (the tenant-scoped unique index permits it)? Must `Teacher.UserId` reference an existing user with an active membership in the tenant? (Precedent: `Branch.ManagerId` is also an FK-less logical reference.)
3. **M-05** — Is a cancelled salary payment a permanent tombstone for that teacher/period (current DB behavior), or should the period be re-payable (requires a filtered unique index / void semantics)?
4. **L-05** (partial), **L-06** — Net ≤ Gross rule; one-rating-per-student-teacher-period vs multiple ratings.

### Out of Scope

- Re-audit or re-approval of the Students module (rule 16) — although H-01's root cause also affects `Student`/`Branch` filters, this is reported once here as a platform observation; the fix location is shared infrastructure.
- Groups aggregate FK for `GroupId` (I-03) and the "effective-from ordering index" naming in MODULE-INVENTORY.
- Platform-level gaps already recorded in ARCHITECTURE-BASELINE.md (TD-2 fallback policy, no access-token revocation, no CORS/CI).
- Pagination of list endpoints (consistent with Students; no requirement documented).
- Executing the migration against a live SQL Server instance in this audit (no Teachers test exists; static + tooling comparison used instead).

---

## 17. Approval Decision

**FAIL — REMEDIATION REQUIRED.**

Rationale per the final-verdict rule: Teachers may PASS only if there are no unresolved Critical/High issues and all required architecture/security/database invariants are verified. This audit found **three unresolved High findings** — a broken soft-delete read path (H-01), a bypassable financial state machine (H-02), and absent concurrency control on the financially sensitive `SalaryPayment` aggregate (H-03) — and **zero dedicated tests** exist to demonstrate any Teachers invariant against SQL Server. Tenant isolation, permission authorization, validation plumbing, auditing, and migration/model fidelity were all verified as sound; the failures are concentrated in soft-delete semantics, payment lifecycle, and concurrency, plus a feature-gating inconsistency with the approved Students pattern.

---

## FINAL DECISION

- **Critical findings:** 0
- **High findings:** 3 (H-01 soft-delete filter loss; H-02 payment state-machine bypass; H-03 no concurrency control on SalaryPayment)
- **Medium findings:** 6 (M-01 … M-06)
- **Low findings:** 8 (L-01 … L-08)
- **Info findings:** 6 (I-01 … I-06)
- **Business decisions required:** 4 clusters (salary-config semantics; Teacher↔User policy; cancelled-payment period policy; rating/amount rules)
- **Dedicated Teachers tests:** 0 (domain: 0, HTTP authorization: 0, cross-tenant: 0, SQL Server: 0, migration/schema: 0, feature/permission: 0, state-machine: 0, unique-index: 0, concurrency: 0)
- **SQL Server verification:** None performed for Teachers (no test exercises the Teachers schema against SQL Server; verified statically against configuration + snapshot, and `dotnet ef migrations has-pending-model-changes` reports no drift). Runtime behavioral verification in this audit used a temporary harness against the real assemblies on the InMemory provider — sufficient to prove H-01/H-02 and the InMemory unique-index gap, not sufficient to prove SQL Server correctness.
- **Build status:** SUCCESS — 0 errors (5,283 pre-existing StyleCop warnings, unrelated to Teachers)
- **Overall verdict:** **FAIL — REMEDIATION REQUIRED**

*End of report. No source code, migrations, tests, or fixes were produced or modified during this audit.*
















