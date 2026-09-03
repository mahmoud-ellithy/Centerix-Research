# CENTERIX — TEACHERS MODULE AUDIT REPORT

> **Mode:** AUDIT ONLY — no source code, migration, test, or configuration was modified by this audit.
> **Date:** 2026-09-03
> **Method:** Evidence-based audit of the CURRENT working tree. Source code is the sole source of truth. Prior reports (including `PHASE-5-TEACHERS-AUDIT-REPORT.md` and the module inventory) were treated as unverified claims; every claim below was re-derived from source and, where possible, from live execution (build, tests, a runtime EF-model probe).
> **Out of scope (per rules):** re-audit of the approved Students module (cross-module regressions on shared infrastructure ARE reported), Billing, RBAC, and the Groups aggregate (documented future FK).

---

## 1. Executive Verdict

**FINAL VERDICT: FAIL**

The Teachers module is structurally complete (entities, EF configurations, migration, CQRS, validators, controllers, permissions all present and internally consistent with the architecture baseline), tenant isolation is intact, and the build + SQL Server integration suite pass. However, the audit found **4 HIGH-severity production-blocking defects** and a cluster of MEDIUM defects concentrated in financial state handling:

1. **H-01 — Feature gating is broken for most Teachers mutations.** `[RequireFeature(FeatureCodes.TeacherManagement)]` is applied to POST endpoints only. Every PUT, DELETE, `mark-paid`, and `cancel` endpoint (Teachers, Subjects, TeacherSalaryConfigs, SalaryPayments) is gated by permission alone. A tenant whose subscription lacks/expired the TeacherManagement feature can still update/delete teachers, delete salary configs, and **mark salary payments as paid or cancel them**.
2. **H-02 — The SalaryPayment state machine can be bypassed.** `MarkPaid` only blocks `Paid → Paid`; a **Cancelled** payment can be marked Paid (verified at runtime: `Cancelled -> MarkPaid: success=True`). `CreateSalaryPaymentHandler` forwards a client-supplied `Status`, so a payment can be created **directly in `Paid` state with `PaidAt = null`** (runtime-verified).
3. **H-03 — SalaryPayment has no concurrency control.** No RowVersion; `MarkPaid`/`Cancel` do read-then-write with no version predicate or transaction. Two concurrent operations (e.g. MarkPaid + Cancel) both succeed and the last write wins — a paid payment can be silently flipped to Cancelled or vice versa. The code proves the race is unprotected (no token, no guard, no transaction).
4. **H-04 — Soft-delete is ineffective on the read path (shared infrastructure defect that Teachers relies on).** `AppDbContext.ApplyTenantQueryFilter` runs after entity configurations and **replaces** (not combines with) each entity's `HasQueryFilter(DeletedAtUtc == null)` — EF Core 10 supports one filter per entity. Runtime inspection of the compiled model shows `Teacher` (and also `Student`, `Branch`) carries ONLY the tenant filter. Consequences: soft-deleted teachers are returned by list/get queries, and the existence checks in `CreateSalaryPaymentHandler`, `CreateTeacherSalaryConfigHandler`, and `CreateTeacherRatingHandler` **pass for soft-deleted teachers**, allowing financial records to be attached to deleted teachers. The same mechanism silently disables soft-delete reads for the approved Students module.

Because HIGH findings exist, per the task's verdict rules the module is **not production-ready** and is **not approved**.

Build: **0 errors**. Tests: **224 total, 222 passed, 2 failed** (both failures are shared test-state contamination inside the pre-existing Students test class — classification D, unrelated to Teachers; both pass individually). All 24 SQL Server (Testcontainers) tests pass, including the real-DB "no pending migrations" check. `dotnet ef migrations has-pending-model-changes` → "No changes have been made to the model since the last migration."

---

## 2. Scope

Audited aggregates (Section B of `docs/MODULE-INVENTORY-20260903.md`, verified against source):

| Aggregate | Domain | Errors | EF Config | DbSet | Migration | Commands | Queries | Validators | Controller |
|---|---|---|---|---|---|---|---|---|---|
| Teacher | `Teacher.cs` | `TeacherErrors.cs` | `TeacherConfiguration.cs` | ✓ | `20260902081027_AddTeacherSalaryModule` | C/U/D (soft) | ById + List | C+U | `TeachersController` |
| Subject | `Subject.cs` | `SubjectErrors.cs` | `SubjectConfiguration.cs` | ✓ | same | C/U/D (hard) | ById + List | C+U | `SubjectsController` |
| TeacherSalaryConfig | `TeacherSalaryConfig.cs` | `TeacherSalaryConfigErrors.cs` | `TeacherSalaryConfigConfiguration.cs` | ✓ | same | C/U/D (hard) | ById + List | C+U | `TeacherSalaryConfigsController` |
| SalaryPayment | `SalaryPayment.cs` | `SalaryPaymentErrors.cs` | `SalaryPaymentConfiguration.cs` | ✓ | same | C + MarkPaid + Cancel | ById + List | C | `SalaryPaymentsController` |
| TeacherRating | `TeacherRating.cs` | `TeacherRatingErrors.cs` | `TeacherRatingConfiguration.cs` | ✓ | same | C (create-only) | List | C | `TeacherRatingsController` |

Shared infrastructure inspected for Teachers-relevant guarantees: `AppDbContext` (filters, stamping), `TenantInterceptor`, `AuditableEntityInterceptor`, `TenantGuardMiddleware`, `PermissionPolicyProvider`/handlers, `FeatureAuthorizationHandler`, `LimitService`, `AuditWriter`, `GlobalExceptionHandler`, `ApiController`, `ValidationBehavior`, `PermissionCatalog`, `LimitTypeCodes`, migration `20260902081027_AddTeacherSalaryModule` + `AppDbContextModelSnapshot`.

## 3. Implementation Inventory (verified against current source)

Inventory claims **confirmed**:
- All 5 entities, 5 error classes, 5 EF configurations, 5 DbSets (`AppDbContext.cs:110-114`, `IAppDbContext.cs:97-101`), one migration, full CQRS surface, validators for all write commands with payloads, 5 controllers.
- Permission codes for all 5 aggregates (`Permissions.cs:102-129`), present in `PermissionCatalog.All` (lines 58-78); tenant-admin has full set, tenant-user read-only (`Permissions.cs:228-251`); idempotent runtime seeding (`ApplicationDbContextInitialiser.cs:65-94`).
- `LimitTypeCodes.Teachers` exists; `CreateTeacherHandler` reserves/releases it atomically (`CreateTeacherCommand.cs:62-106`, `LimitService.cs:111-115, 166-170`).

Inventory claims **contradicted** by source:
- "Feature-gated by `[RequireFeature(FeatureCodes.TeacherManagement)]` on write endpoints" — **FALSE for PUT/DELETE/mark-paid/cancel** (see H-01/F-01). Only the five POST endpoints carry the attribute.
- `CreateSalaryPaymentValidator` contains **no rule restricting `Status`** (see F-06).
- Inventory does not disclose that Subject and TeacherSalaryConfig deletes are **hard deletes** (`Remove`), while Teacher is soft delete.

---

## 4. Architecture Compliance

### Domain — PASS (with noted invariant gaps)
- No infrastructure dependencies in any of the 5 entities; factories return `Result<T>`; private constructors + `Create` factories; `Teacher` exposes `Update`/`ChangeStatus`/`SoftDelete` with `IsDeleted()` guards (`Teacher.cs:82-128`).
- Domain invariants enforced: Teacher (branchId, fullName ≤200, phone ≤30, qualification ≤200, yearsExp ≤100, defined status); Subject (name required, stageId > 0); TeacherSalaryConfig (teacherId, salaryType, value (0, 999999.99], percentage ≤ 100); SalaryPayment (teacherId, month 1-12, year 2000-2100, amounts > 0, defined status); TeacherRating (ids, stars 1-5, comment ≤500, period).
- **Gaps:** no `Net ≤ Gross` rule (F-14); `TeacherSalaryConfig` has no effective-from lower bound and its `EffectiveFromRequired` error is referenced by nothing (F-10; runtime-verified: `EffectiveFrom = 0001-01-01` accepted).

### Application — PASS (pattern), with defense-in-depth asymmetry
- CQRS correct; `Result<T>` everywhere; handlers use only `IAppDbContext`, `ICurrentTenant`, `ILimitService`, `IAuditWriter` — no direct Infrastructure dependency.
- `ValidationBehavior<,>` registered **first** in the MediatR pipeline (`Application/DependencyInjection.cs:17`); validators auto-discovered from the Application assembly (line 24); all Teachers commands flow through it (controllers only `mediator.Send` — no bypass path). `ValidationException` → HTTP 400 (`GlobalExceptionHandler.cs:22-46`).
- **Asymmetry vs approved Students:** `UpdateStudentHandler`/`DeleteStudentHandler` carry explicit `student.TenantId != currentTenant.TenantId` assertions. **No Teachers handler has this assertion** — they rely on the global query filter alone (F-08).

### Infrastructure — PASS (structure), FAIL (soft-delete read path → H-04)
- EF configurations complete; `DeleteBehavior.Restrict` on all five FKs; `Teacher.RowVersion` is `IsRowVersion()` + `[Timestamp]` (`TeacherConfiguration.cs:58-59`; snapshot lines 2564-2567).
- **Only `Teacher` has optimistic concurrency.** Subject, TeacherSalaryConfig, SalaryPayment, TeacherRating have none (F-11, H-03).
- `TenantInterceptor` stamps the verified tenant on Added entities (`TenantInterceptor.cs:34-56`); every Teachers create-handler additionally calls `StampAddedTenantIds`.
- `AuditableEntityInterceptor` stamps Created/Modified; **`DeletedBy` stamping is dead code** — it requires `entry.Property(DeletedBy).IsModified`, which nothing sets (F-12; runtime-verified `DeletedBy=<null>` after `Teacher.SoftDelete()`).

### API — PASS (verbs/codes/thinness), FAIL (feature gating → H-01)
- Controllers are thin; route-id/command-id mismatch → 400; `ApiController.Problem` maps Validation→400, Conflict→409, NotFound→404, Unauthorized→401, Forbidden→403.
- Verbs/routes consistent; MarkPaid/Cancel are RPC-style `POST {id}/mark-paid|cancel` returning 204.
- DTOs expose only presentation fields; no audit/tenant internals leak.

## 5. Security / Tenant Isolation (3-layer model)

### Layer 1 — Request level (`TenantGuardMiddleware`) — PASS
- Teacher endpoints are tenant-scoped (their permission codes are not in `Permissions.PlatformScope.PermissionCodes`, `Permissions.cs:267-301`). The guard requires: resolved tenant, active `TenantMembership` for the resolved tenant, then `currentTenant.AuthorizeTenant()` (`TenantGuardMiddleware.cs:59-82`). Unauthorized tenant switching → 403 problem details; unresolved tenant → 403 (fail-closed).

### Layer 2 — Query level (global tenant filter) — PASS (tenant dimension)
- Runtime probe of the compiled EF model (`IEntityType.GetQueryFilter()`) shows **exactly one filter** on `Teacher`, `Subject`, `TeacherSalaryConfig`, `SalaryPayment`, `TeacherRating`, `Student`: `e => (e.TenantId == value(AppDbContext)._currentTenant.TenantId)`. The filter reads the **verified** tenant live per request; before `AuthorizeTenant()` the value is empty → filter matches nothing (fail-closed).
- Cross-tenant GET/UPDATE/DELETE of any Teachers aggregate → 404 (entity invisible). Consistent across all five controllers.
- **Critical caveat:** for `Teacher` (and `Student`, `Branch`) the tenant filter is the ONLY filter — the soft-delete filter configured in the entity configuration was **replaced**, not merged (H-04). Tenant isolation holds; soft-delete isolation does not.

### Layer 3 — Write level (`TenantInterceptor` + `StampAddedTenantIds`) — PASS
- `TenantId` cannot be client-assigned: every Teachers create-handler stamps it from the verified context (`CreateTeacherCommand.cs:96`, `CreateSubjectCommand.cs:61`, `TeacherSalaryConfigCommands.cs:67`, `SalaryPaymentCommands.cs:64`, `CreateTeacherRatingCommand.cs:72`), and the interceptor re-stamps at save. No Teachers handler writes into a foreign tenant.

### Application-level ownership checks — GAP (F-08)
- Unlike the approved Students handlers, no Teachers handler asserts `entity.TenantId == currentTenant.TenantId` after materialization. Today the query filter compensates; any future `IgnoreQueryFilters()` or filter change would silently expose cross-tenant mutation.

### Cross-tenant FK security (client-supplied IDs)

| FK | Target tenant-scoped? | Ownership verified? | Mechanism | Attacker supplies foreign ID → |
|---|---|---|---|---|
| Teacher → Identity User (`UserId`) | Link is tenant-scoped via `UX_Teachers_TenantId_UserId` | **No** — no membership/ownership check on the user | Unique index only | Create/update succeeds and links ANY user id into the tenant (F-05) |
| Teacher → Branch (`BranchId`) | Yes | Yes — tenant-filtered `AnyAsync` (`CreateTeacherCommand.cs:69-76`, `UpdateTeacherCommand.cs:51-55`) | Global query filter | 404 `Branch.NotFound` |
| Subject → AcademicStage (`StageId`) | Yes | Yes — tenant-filtered `AnyAsync` (`CreateSubjectCommand.cs:40-44`, `UpdateSubjectCommand.cs:43-47`) | Global query filter | 404 `AcademicStage.NotFound` |
| TeacherSalaryConfig → Teacher | Yes (soft-deleted teachers pass — H-04) | Yes, tenant-filtered (`TeacherSalaryConfigCommands.cs:49-53`) | Global query filter | 404 `Teacher.NotFound` |
| SalaryPayment → Teacher | Yes (same H-04 caveat) | Yes, tenant-filtered (`SalaryPaymentCommands.cs:44-48`) | Global query filter | 404 |
| TeacherRating → Teacher / → Student | Yes | Yes — both checked tenant-filtered (`CreateTeacherRatingCommand.cs:46-56`) | Global query filters | 404 (Teacher or Student) |

- **TeacherRating both directions covered:** Tenant A teacher + Tenant B student → student check fails (404); Tenant A student + Tenant B teacher → teacher check fails (404). Cross-tenant rating creation is blocked at the application layer. DB-level FKs are single-column (no composite `(TenantId, Id)` FK), so SQL Server alone would accept a cross-tenant pairing — the query-filtered checks are the only guard (currently effective; a TOCTOU race would surface as 500, never 409).
- `GroupId` (SalaryConfig, Rating): documented placeholder — plain `Guid?`, **no FK, no validation**; any value accepted (INFO; M-03 will add the FK).

## 6. Authorization & Feature Gating

### Endpoint matrix (verified against controllers)

| Endpoint | Route | Permission | Feature | Missing-permission result |
|---|---|---|---|---|
| GET | `api/teachers`, `/{id}` | `Teachers.Read` | none | 403 |
| POST | `api/teachers` | `Teachers.Create` | **TeacherManagement ✓** | 403 |
| PUT | `api/teachers/{id}` | `Teachers.Update` | **MISSING (H-01)** | 403 |
| DELETE | `api/teachers/{id}` | `Teachers.Delete` | **MISSING (H-01)** | 403 |
| GET | `api/subjects`, `/{id}` | `Subjects.Read` | none | 403 |
| POST | `api/subjects` | `Subjects.Create` | **TeacherManagement ✓** | 403 |
| PUT/DELETE | `api/subjects/{id}` | Update / Delete | **MISSING (H-01)** | 403 |
| GET | `api/teachersalaryconfigs`, `/{id}` | `TeacherSalaryConfigs.Read` | none | 403 |
| POST | `api/teachersalaryconfigs` | Create | **TeacherManagement ✓** | 403 |
| PUT/DELETE | `api/teachersalaryconfigs/{id}` | Update / Delete | **MISSING (H-01)** | 403 |
| GET | `api/salarypayments`, `/{id}` | `SalaryPayments.Read` | none | 403 |
| POST | `api/salarypayments` | Create | **TeacherManagement ✓** | 403 |
| POST | `api/salarypayments/{id}/mark-paid` | `SalaryPayments.Update` | **MISSING (H-01)** | 403 |
| POST | `api/salarypayments/{id}/cancel` | `SalaryPayments.Update` | **MISSING (H-01)** | 403 |
| GET | `api/teacherratings` | `TeacherRatings.Read` | none | 403 |
| POST | `api/teacherratings` | Create | **TeacherManagement ✓** | 403 |

- Policy plumbing verified: `[RequireFeature]` → policy `Feature:Teachers` (`FeatureAuthorization.cs:74-79`), resolved by `PermissionPolicyProvider.GetPolicyAsync` (lines 32-38) into `FeatureRequirement`, handled **fail-closed** by `FeatureAuthorizationHandler` (expired/suspended/missing feature → deny; PlatformAdmin bypass). `[HasPermission]` → per-request DB resolution via `PermissionAuthorizationHandler`, fail-closed (exception → deny).
- **Enforcement order** matches the baseline: authentication → TenantGuard (membership + `AuthorizeTenant`) → permission policy → feature policy → in-handler subscription/limit reservation (`LimitService.ReserveAsync` re-checks active subscription and effective max atomically, `LimitService.cs:49-137`). Subscription expired/feature missing → 403; feature present + limit exhausted → 409 (`Limit.Exceeded`).
- **H-01 (blocking):** a tenant with the TeacherManagement feature removed or expired can still update/delete teachers and subjects, delete salary configs, and **mark salary payments paid / cancel them** — the exact defect class previously remediated for Students was not applied to Teachers.
- **Limits:** only `Teachers` is limited (`LimitTypeCodes.All` = Students, Users, Branches, Teachers). Salary records, subjects and ratings have **no** limit type — no limit is enforced or expected by current configuration (INFO, F-16). `DeleteTeacherHandler` releases the Teachers slot after a successful soft delete (`DeleteTeacherCommand.cs:43-45`).

## 7. Validation

| Command | Validator | Rules | Verdict |
|---|---|---|---|
| CreateTeacherCommand | `CreateTeacherValidator` | UserId ≤450, FullName ≤200, Phone ≤30, Qualification ≤200, YearsExp 0-60 | Invoked via pipeline ✓ |
| UpdateTeacherCommand | `UpdateTeacherValidator` | same + Id | Invoked ✓ |
| CreateSubjectCommand / UpdateSubjectCommand | validators | Name ≤100 required, StageId > 0 | Invoked ✓ |
| CreateTeacherSalaryConfigCommand / Update | validators | TeacherId, Value (0, 999999.99], Percentage ≤ 100 | Invoked ✓; **no EffectiveFrom rule** (F-10) |
| CreateSalaryPaymentCommand | validator | TeacherId, Month 1-12, Year 2000-2100, Amounts > 0 | Invoked ✓; **no Status rule** (F-06); no Net ≤ Gross (F-14) |
| CreateTeacherRatingCommand | validator | Ids, Stars 1-5, Comment ≤500, Month/Year | Invoked ✓ |
| Delete/MarkPaid/Cancel commands | none | Id-only payloads | Acceptable (no payload to validate) |

- **Validators ARE executed.** `ValidationBehavior<,>` is the first registered `IPipelineBehavior` (`Application/DependencyInjection.cs:14-22`); `AddValidatorsFromAssembly(assembly)` (line 24) discovers all 8 Teachers validators; no code path dispatches a Teachers command without MediatR. `ValidationException` → 400 (`GlobalExceptionHandler.cs:22-46`).
- Consistency: YearsExp validator allows 0-60 while the domain allows ≤100 and its error message claims "0 and 255" (F-13). Lengths/types match EF enforcement elsewhere.

---

## 8. Domain Invariants (encoded — no invented rules)

| # | Invariant | Enforcement | Evidence |
|---|---|---|---|
| T1 | Teacher belongs to a Branch within the tenant | Application (tenant-filtered check) + DB FK | `CreateTeacherCommand.cs:69-76` |
| T2 | One Teacher per (TenantId, UserId) | **Database only** (`UX_Teachers_TenantId_UserId`); app-level `DuplicateUser` error referenced by nothing; violation → 500 | `TeacherConfiguration.cs:64-66`; migration lines 254-259 |
| T3 | Soft-deleted Teacher cannot be updated/deleted again | Domain (`IsDeleted` guards) | `Teacher.cs:90-91, 120-122` |
| T4 | "One Identity user per tenant" business rule | **Not enforced** — no membership check on `UserId`; any string ≤450 accepted | F-05 |
| S1 | Subject name unique within (TenantId, StageId) | Create: application check (`CreateSubjectCommand.cs:50-58`) + DB `UX_Subjects_TenantId_StageId_Name`. Update: **no check** → 500 (F-09). Ordinal app check vs case-insensitive DB collation → CI collisions yield 500 | F-09 |
| S2 | Stage exists in tenant | Application check only — **no DB FK** from Subjects to stages | `CreateSubjectCommand.cs:40-44` |
| C1 | Salary value (0, 999999.99]; Percentage ≤ 100 | Domain + validator | `TeacherSalaryConfig.cs:57-61` |
| C2 | Effective dates: overlap / duplicate-date / future rules | **Not enforced anywhere** (no app check; `IX_TeacherId_EffectiveFrom` non-unique; `0001-01-01` accepted — runtime-verified). **Business rule ambiguity — requires product decision** | F-10 |
| C3 | "Current configuration" selection / historical behavior | **Not implemented** — queries only `OrderByDescending(EffectiveFrom)` (`GetTeacherSalaryConfigs.cs:25`); no consumer exists (Groups/salary calc = M-03). Ambiguity | F-10 |
| P1 | One payment per (TeacherId, Year, Month) | **Database only** (`UX_SalaryPayments_Teacher_Period`), unfiltered → includes Cancelled; no app pre-check; `DuplicatePayment` never returned from Create; collision → 500 | F-07 |
| P2 | State transitions (see §9) | Domain only — no DB constraint, no concurrency token | H-02/H-03 |
| P3 | Amounts > 0 | Domain + validator | — |
| P4 | Net ≤ Gross | **Not enforced** | F-14 |
| R1 | Stars 1-5, comment ≤500, valid period | Domain + validator | — |
| R2 | Teacher & Student same tenant | Application only (tenant-filtered checks); DB FKs single-column | §5 |
| R3 | No duplicate rating per (teacher, student, period) | **Not enforced** (no unique index, no app check) — **Business rule ambiguity — requires product decision** | F-18 |
| R4 | Rating immutability | Create-only surface | — |

## 9. Salary State Machine (runtime-verified)

Actual transitions implemented in `SalaryPayment.cs:76-94` and proven by the runtime probe:

| Transition | Result | Evidence |
|---|---|---|
| Pending → MarkPaid | **SUCCESS** | probe |
| Paid → MarkPaid | FAILED (`SalaryPayment.Duplicate`) | probe |
| Paid → Cancel | FAILED (`SalaryPayment.InvalidStatus`) | probe |
| Pending → Cancel | **SUCCESS** | probe |
| Cancelled → Cancel (repeat) | **SUCCESS** (silent idempotency — inconsistent with strict `Paid → Paid` rejection) | probe |
| **Cancelled → MarkPaid** | **SUCCESS — cancelled payment resurrected to Paid with a new PaidAt** | probe: `Final p2 state: Status=Paid` |
| Create with `Status=Paid` | **ACCEPTED with `PaidAt = null`** (inconsistent financial record) | probe |
| Create with `Status=Cancelled` | ACCEPTED (any defined enum passes the factory) | code `SalaryPayment.cs:45-74` |

- Intended machine is evidently `Pending → Paid`, `Pending → Cancelled` with terminal states. Enforcement layers: **Domain — partial** (blocks only Paid→Paid and Paid→Cancel); **Application — none** (Create forwards client `Status`; no validator rule); **Database/concurrency — none** (no token, no constraint, no trigger).
- **Concurrent MarkPaid + Cancel:** both handlers do `FirstOrDefaultAsync` → domain check → `SaveChangesAsync`. With no RowVersion and no version predicate, **both succeed; final state = last writer**. The period unique index does not protect state. The implementation does **not** prevent an invalid final state — proven from code structure; no test exists that could disprove it.

---

## 10. Database / EF / Migration Verification

Compared source configuration ↔ `AppDbContextModelSnapshot.cs` ↔ `20260902081027_AddTeacherSalaryModule.cs`:

| Item | Config | Snapshot | Migration | Match |
|---|---|---|---|---|
| Tables/schema | `Platform.{Teachers,Subjects,TeacherSalaryConfigs,SalaryPayments,TeacherRatings}` | same | same | ✓ |
| Teacher columns | TeacherId uniqueidentifier, UserId/FullName/Phone/Qualification/Status/TenantId nvarchar lengths, JoinedAt date, YearsExp tinyint, RowVersion rowversion | snapshot 2514-2602 | migration 34-67 | ✓ |
| Subject columns | SubjectId identity, Name nvarchar(100), StageId int | snapshot 2343-2390 | migration 14-32 | ✓ |
| SalaryPayment columns | decimal(10,2) amounts, tinyint/smallint period, Status nvarchar(10), PaidAt datetime2 | snapshot 2280-2341 | migration 69-98 | ✓ |
| TeacherSalaryConfig columns | decimal(8,2) Value, date EffectiveFrom | snapshot 2456-2512 | migration 138-166 | ✓ |
| TeacherRating columns | tinyint Stars, nvarchar(500) Comment | snapshot 2392-2454 | migration 100-136 | ✓ |
| `UX_Teachers_TenantId_UserId` | `TeacherConfiguration.cs:64-66` | snapshot 2597-2599 | migration 254-259 | ✓ **VERIFIED** |
| `UX_Subjects_TenantId_StageId_Name` | `SubjectConfiguration.cs:36-38` | snapshot 2385-2387 | migration 193-198 | ✓ **VERIFIED** |
| `UX_SalaryPayments_Teacher_Period` | `SalaryPaymentConfiguration.cs:57-59` | snapshot 2334-2336 | migration 180-185 | ✓ **VERIFIED** |
| FKs (all `Restrict`) | Teacher→Branches; SalaryPayment→Teachers; SalaryConfig→Teachers; Rating→Teachers,→Students | snapshot | migration 60-66, 91-97, 159-165, 121-135 | ✓ |
| Query filters | in-memory/runtime only — **not captured in the migration snapshot** (normal EF Core behavior; verified empirically instead, §5/H-04) | n/a | n/a | n/a |

- `dotnet ef migrations has-pending-model-changes --context AppDbContext` → **"No changes have been made to the model since the last migration"** (executed during this audit).
- Real SQL Server check: `Phase2SqlServerTests.Migrations_Phase2Schema_NoPendingMigrations_CommercialColumnsExist` asserts `GetPendingMigrationsAsync()` is empty on a Testcontainers database migrated to the latest chain (includes `AddTeacherSalaryModule`) — **PASSED** in this audit's fresh run.
- Uniqueness semantics notes: `UX_Teachers_TenantId_UserId` is **not filtered** on `DeletedAtUtc` (soft-deleted teachers permanently occupy the user link, F-05). `UX_SalaryPayments_Teacher_Period` is **not filtered** on Status (cancelled payments permanently occupy the period, F-07). `UX_Subjects_TenantId_StageId_Name` scope matches the tenant/stage business scope — but with default (case-insensitive) collation, no `DeleteBehavior` dependency; hard deletes free the name.

## 11. Concurrency

| Entity | Concurrency token | Risk |
|---|---|---|
| Teacher | RowVersion (`[Timestamp]` + `IsRowVersion()`) | Present. Conflicting updates → `DbUpdateConcurrencyException` → **unhandled** (GlobalExceptionHandler maps only `ValidationException`) → **HTTP 500** instead of 409/412 (F-11). |
| Subject | none | Lost updates possible; unique index collisions surface as 500 (F-09). |
| TeacherSalaryConfig | none | Concurrent edits → last write wins on financial parameters. |
| SalaryPayment | **none** | See §9 — MarkPaid/Cancel race unprotected (H-03). |
| TeacherRating | none | Create-only surface; low risk. |

- No code catches `DbUpdateConcurrencyException`; there is no 409/412 mapping anywhere.
- `LimitService` reservations for Teachers ARE concurrency-safe (`ExecuteUpdateAsync` conditional increment, `LimitService.cs:111-115`) — proven on real SQL Server by `LimitReservation_ConcurrentCallers_ExactlyOneClaimsLastSlot_OnRealSql`.

## 12. Soft Delete

| Entity | Delete behavior | DeletedAtUtc | Global filter | Read-after-delete | Update-after-delete | New references to deleted row |
|---|---|---|---|---|---|---|
| Teacher | Soft (`Teacher.SoftDelete`) + Status→Inactive | set | **BROKEN — replaced by tenant filter (H-04)** | **Deleted rows ARE returned** (probe-proven model) | Domain guard blocks (`AlreadyDeleted`) | **Allowed** — existence checks pass for deleted teachers → payments/configs/ratings can be attached (H-04) |
| Subject | Hard (`Remove`) | n/a | n/a | n/a | n/a | n/a |
| TeacherSalaryConfig | Hard (`Remove`) | n/a | n/a | n/a | n/a | n/a |
| SalaryPayment | none (no delete) | n/a | n/a | n/a | n/a | n/a |
| TeacherRating | none (no delete) | n/a | n/a | n/a | n/a | n/a |

- Teacher soft delete sets `DeletedAtUtc` + `Status=Inactive` (`Teacher.cs:119-128`) and is audited; `DeletedBy` is never stamped (F-12).
- Recreating a teacher for the same user after soft delete → `UX_Teachers_TenantId_UserId` violation → 500 (F-05). The limit slot is released on delete, so a tenant can consume a slot for a save that then fails — counter drift on failure paths.
- Student behavior does not automatically protect Teachers: the same broken dual-filter mechanism applies to both, and Teachers' existence checks actively depend on the missing soft-delete filter.

## 13. Auditing

All Teachers mutation paths write tenant-scoped `AuditLog` rows via `IAuditWriter` (dual-audit: tenant → `AuditLog`, platform → `PlatformAuditLog`) with action name, entity type/id, actor, tenant, old/new JSON, UTC timestamp: `Teacher.Create/Update/Delete`, `Subject.Create/Update/Delete`, `TeacherSalaryConfig.Create/Update/Delete`, `SalaryPayment.Create/MarkPaid/Cancel`, `TeacherRating.Create` — verified in every command file (e.g. `CreateTeacherCommand.cs:108-119`, `SalaryPaymentCommands.cs:113-123, 156-166`).

- Audit failures are logged and swallowed (`AuditWriter.cs:81-85`) — consistent with baseline; business ops never fail due to audit.
- **Gap:** `DeletedBy` on the soft-deleted row is never populated (F-12) — the audit row records the actor, the tombstone does not. Mechanism shared with Students (also affected there).
- Limit release on delete is not audited (counter change only) — consistent with Students.

## 14. Tests & Coverage

**Verified: ZERO dedicated Teachers tests.** Grep across `tests/Centerix.SecurityTests/**` for `Teacher|SalaryPayment|TeacherRating|TeacherSalaryConfig|Subject` returns no test-code hits. The inventory claim is confirmed.

Unproven (no test exists): domain invariants of all five entities; salary state machine; effective-from edge cases; Validators + ValidationBehavior for Teachers commands; the entire Teachers HTTP authorization matrix (permission 403s, feature 403s, 400/404/409 mapping); cross-tenant read/update/delete per aggregate; cross-tenant TeacherRating pairing; the three unique indexes on real SQL Server; Teacher rowversion conflict behavior; MarkPaid/Cancel concurrency. Indirect coverage exists only for shared machinery via Students-flavored tests (`TenantGuardMiddlewareTests`, `C1CrossTenantIsolationTests`, `TenantExpiryGuardTests`, Phase2/3 suites).

## 15. Test Execution Results

| Run | Result |
|---|---|
| `dotnet build Centerix.slnx` | **0 errors** (5283 StyleCop warnings — pre-existing, project-wide style noise) |
| `dotnet test` (full suite) | **224 total, 222 passed, 2 failed, 0 skipped** (46 s) |
| SQL Server suites (`Phase2SqlServerTests`, `SqlServerInvitationFlowTests`), fresh run | **24/24 passed** (16 s, Testcontainers) |
| `Students_TenantAdmin_CanCreateReadUpdateSoftDelete` individually | **PASSED** |
| `Students_CrossTenantBranch_IsInvisibleAndReturnsNotFound` individually | **PASSED** |
| `Phase3AuthorizationHttpTests` class alone | **16/18 — same 2 failures** |
| `dotnet ef migrations has-pending-model-changes --context AppDbContext` | "No changes have been made to the model since the last migration" |

**Failed test classification:**

1. `Centerix.SecurityTests.Phase3AuthorizationHttpTests.Students_TenantAdmin_CanCreateReadUpdateSoftDelete` — `System.InvalidOperationException: Sequence contains more than one element`, stack at `Phase3AuthorizationHttpTests.cs:628` (`db.Students.IgnoreQueryFilters().SingleAsync()`). Passes individually; deterministic in class-alone and full-suite orderings. **Classification D — shared test-state contamination** (`Students_CrossTenantUpdateBranch_IsRejected` line 1245-1248 leaves a student row in the shared per-class InMemory DB). **Not Teachers-related.**
2. `Centerix.SecurityTests.Phase3AuthorizationHttpTests.Students_CrossTenantBranch_IsInvisibleAndReturnsNotFound` — same exception at `Phase3AuthorizationHttpTests.cs:667` (`.Single(b => b.Name == "A-Branch")`). Same root cause: line 1198 creates a second "A-Branch". **Classification D.** Not Teachers-related.

No classification-A or -B failures (Teachers application defect / Teachers-caused regression) were observed. One MSB4018 file-lock occurred when two `dotnet test` invocations ran concurrently (auditor operator error — classification C, not reproducible sequentially).

## 16. Findings

| ID | Severity | Finding | Evidence |
|---|---|---|---|
| **F-01 (H-01)** | **HIGH** | `[RequireFeature(FeatureCodes.TeacherManagement)]` missing on PUT/DELETE (Teachers, Subjects, TeacherSalaryConfigs) and on `mark-paid`/`cancel` (SalaryPayments). Feature-expired tenants retain full mutation rights on all Teachers data including financial state. | `TeachersController.cs:47-72`; `SubjectsController.cs:47-72`; `TeacherSalaryConfigsController.cs:47-72`; `SalaryPaymentsController.cs:47-67` |
| **F-02 (H-02)** | **HIGH** | State machine bypass: `Cancelled → MarkPaid` succeeds (resurrection); repeat `Cancel` on Cancelled succeeds; Create accepts client `Status=Paid` with `PaidAt=null` (and `Cancelled`). | `SalaryPayment.cs:76-94` + runtime probe |
| **F-03 (H-03)** | **HIGH** | No concurrency control on SalaryPayment (no RowVersion, read-then-write, no transaction): concurrent MarkPaid+Cancel → last write wins; invalid final state possible. | `SalaryPayment.cs` (no token); `SalaryPaymentConfiguration.cs`; `SalaryPaymentCommands.cs:92-126` |
| **F-04 (H-04)** | **HIGH** | Soft-delete read-path broken on shared infra: `ApplyTenantQueryFilter` (`AppDbContext.cs:148-172`) replaces per-entity `HasQueryFilter(DeletedAtUtc == null)` (single-filter semantics in EF Core 10.0.9 — no multi-filter API exists). Compiled model: Teacher/Student/Branch carry ONLY the tenant filter. Deleted teachers visible; existence checks pass for deleted teachers → financial records attachable to deleted teachers. Also disables soft-delete reads for approved Students/Branches (shared-infrastructure regression). | runtime probe (§5); `TeacherConfiguration.cs:61`; `StudentConfiguration.cs:74` |
| F-05 | MEDIUM | Duplicate-teacher guard absent at app layer (`TeacherErrors.DuplicateUser` unused); unique index unfiltered on DeletedAt → soft-deleted teacher permanently blocks its user; recreate → 500 not 409; `UserId` not validated against tenant membership (any user id linkable). | `TeacherErrors.cs:40-41`; `CreateTeacherCommand.cs` / `UpdateTeacherCommand.cs` (no check); `TeacherConfiguration.cs:64-66` |
| F-06 | MEDIUM | `CreateSalaryPaymentCommand.Status` is client-supplied with no validator rule and no handler coercion to `Pending`. | `SalaryPaymentCommands.cs:15-33` |
| F-07 | MEDIUM | `UX_SalaryPayments_Teacher_Period` includes Cancelled rows: cancelled period permanently blocked for re-creation → 500; no reopen transition; `DuplicatePayment` never returned from Create. | `SalaryPaymentConfiguration.cs:57-59`; migration lines 180-185 |
| F-08 | MEDIUM | Defense-in-depth asymmetry: no explicit `TenantId == currentTenant` assertions in any Teachers handler (Students has them). | `UpdateStudentCommand.cs:46-49` vs all Teachers handlers |
| F-09 | MEDIUM | Update-path uniqueness asymmetry: `UpdateSubjectHandler` has no duplicate-name check (Create does); Teacher update has no DuplicateUser check; ordinal-vs-CI collation mismatch turns legitimate updates into 500s. | `UpdateSubjectCommand.cs:38-59`; `UpdateTeacherCommand.cs` |
| F-10 | MEDIUM | TeacherSalaryConfig effective-from semantics undefined: no overlap/duplicate-date/future rules at any layer; `0001-01-01` accepted; `EffectiveFromRequired` error unused; Update can freely move dates; no "current config" selection exists. **Business rule ambiguity — requires product decision.** | `TeacherSalaryConfig.cs`; `GetTeacherSalaryConfigs.cs:25`; probe |
| F-11 | LOW | `DbUpdateConcurrencyException` unhandled → Teacher rowversion conflicts return 500 (no 409/412 mapping). | `GlobalExceptionHandler.cs` |
| F-12 | LOW | `DeletedBy` never stamped on soft delete (interceptor requires `IsModified`, never true); affects Teacher and Student. | `AuditableEntityInterceptor.cs:57-64`; probe (`DeletedBy=<null>`) |
| F-13 | LOW | YearsExp bounds inconsistent: validator 0-60, domain ≤100, error message "0 and 255". | `CreateTeacherCommand.cs:46-48`; `Teacher.cs:153-154`; `TeacherErrors.cs:28-29` |
| F-14 | LOW | No `Net ≤ Gross` invariant on SalaryPayment. | `SalaryPayment.cs:64-68` |
| F-15 | INFO | Zero dedicated Teachers tests; module behavior unproven by automated tests. | §14 |
| F-16 | INFO | No limit types exist for subjects/salary records/ratings (only the Teachers limit, correctly enforced). | `LimitTypeCodes.cs` |
| F-17 | INFO | `GroupId` placeholder without FK or validation (documented; M-03 adds FK). | `TeacherSalaryConfig.cs:12-17`; `TeacherRating.cs:13-18` |
| F-18 | INFO | No duplicate-rating rule per (teacher, student, period) — **Business rule ambiguity — requires product decision.** | `TeacherRatingConfiguration.cs:55` (non-unique index) |
| F-19 | INFO | Pre-existing Students test-suite contamination (2 failures, class D); root cause identified in §15. | §15 |
| F-20 | INFO | Subject/TeacherSalaryConfig deletes are hard deletes (no tombstone); acceptable for reference data, but historical salary configs vanish from the DB (audit JSON remains). | `DeleteSubjectCommand.cs:32`; `TeacherSalaryConfigCommands.cs:188` |

**Regression check:** TenantGuard, cross-tenant isolation, permission auth, subscription expiry, limits, soft delete, audit, tenant stamping, and query filters were all re-verified. Tenant isolation and authorization layers show **no regression**. The soft-delete read path (F-04) is a latent shared-infrastructure defect surfaced by this audit — not introduced by Teachers, but actively relied upon by Teachers.

## 17. Recommended Remediation Order

1. **F-01** — Add `[RequireFeature(FeatureCodes.TeacherManagement)]` to all Teachers PUT/DELETE endpoints and to `mark-paid`/`cancel` (mechanical; mirrors the Students remediation).
2. **F-04** — Merge soft-delete into the tenant filter for soft-deletable entities in one place (e.g. `HasQueryFilter(e => e.TenantId == _currentTenant.TenantId && e.DeletedAtUtc == null)`); re-verify Students/Branches (shared infra).
3. **F-02 + F-06** — Restrict the SalaryPayment machine: force `Status=Pending` on create; reject `Cancelled → MarkPaid`; make repeat `Cancel` an explicit product decision (idempotent success vs Conflict); set `PaidAt` atomically with `MarkPaid`.
4. **F-03** — Add RowVersion to SalaryPayment (or a version predicate on the UPDATE) and map `DbUpdateConcurrencyException` → 409/412 globally (also fixes F-11 for Teacher).
5. **F-05 / F-09 / F-13** — App-level duplicate checks (create + update) for Teacher↔User and Subject name → 409; align YearsExp bounds and error text.
6. **F-07** — Product decision: filtered unique index excluding Cancelled payments, or a reopen transition; return 409 (`DuplicatePayment`) from Create on pre-check.
7. **F-08** — Add explicit tenant assertions to Teachers handlers (mirrors Students).
8. **F-10 / F-18** — Product decisions required for salary-config effective-dating semantics and rating duplication before M-03 builds on them.
9. **F-15** — Add the missing test layers (domain state machine, HTTP auth matrix, SQL Server index/concurrency tests) as remediation verification.
10. **F-19** — Fix the two contaminating Students tests (scope `.Single()` assertions by tenant id).

## 18. Final Decision

**FAIL — not approved for production.**

Blocking findings: **F-01, F-02, F-03, F-04** (all HIGH). The module is structurally complete and no cross-tenant exposure was found, but feature gating of financial mutations is absent, the salary payment state machine and its concurrency handling cannot be trusted, and the soft-delete guarantee that Teachers (and Students) rely on does not hold on the read path. Re-submit for audit after remediation items 1-4 at minimum.

## 19. Files Inspected

**Domain:** `Teacher.cs`, `TeacherErrors.cs`, `Subject.cs`, `SubjectErrors.cs`, `TeacherSalaryConfig.cs`, `TeacherSalaryConfigErrors.cs`, `SalaryPayment.cs`, `SalaryPaymentErrors.cs`, `TeacherRating.cs`, `TeacherRatingErrors.cs`, `Enums/{SalaryPaymentStatus,SalaryType,TeacherStatus}.cs`, `Common/{Entity,AuditableEntity,IHasTenantId}.cs`
**Application:** `Teachers/{Teachers,Subjects,TeacherSalaryConfigs,SalaryPayments,TeacherRatings}/**` (all commands, queries, DTOs, validators, handlers), `Common/Interfaces/{IAppDbContext,ICurrentTenant,ILimitService,IAuditWriter}.cs`, `Common/Behaviours/{ValidationBehavior,CachingBehaviour}.cs`, `DependencyInjection.cs`
**Infrastructure:** `Data/AppDbContext.cs`, `Data/Configurations/{Teacher,Subject,TeacherSalaryConfig,SalaryPayment,TeacherRating,Student}Configuration.cs`, `Data/Interceptors/{TenantInterceptor,AuditableEntityInterceptor}.cs`, `Data/Migrations/20260902081027_AddTeacherSalaryModule.cs`, `Data/Migrations/AppDbContextModelSnapshot.cs`, `Data/ApplicationDbContextInitialiser.cs`, `DependencyInjection.cs`, `Auth/{Permissions,PermissionCatalog,FeatureAuthorization,PermissionPolicyProvider}.cs`, `Platform/LimitService.cs`, `Auditing/AuditWriter.cs`
**API:** `Controllers/{Teachers,Subjects,TeacherSalaryConfigs,SalaryPayments,TeacherRatings}Controller.cs`, `Controllers/ApiController.cs`, `Infrastructure/{TenantGuardMiddleware,GlobalExceptionHandler}.cs`
**Tests:** all files under `tests/Centerix.SecurityTests/` (coverage grep + execution); `Phase3AuthorizationHttpTests.cs`, `Phase2SqlServerTests.cs`, `TestWebApplicationFactory.cs`, `ScratchDiag2Tests.cs` in detail
**Docs:** `ARCHITECTURE-BASELINE.md`, `PHASE-3-VERIFICATION-REPORT.md`, `MODULE-INVENTORY-20260903.md` (cross-checked, not trusted)

## 20. Evidence Appendix

1. **Build:** `dotnet build Centerix.slnx` → `0 Error(s)` (30.0 s).
2. **Full test suite:** `Passed: 222, Failed: 2, Total: 224` — failures detailed in §15 with exact test names, exception, and stack line.
3. **Isolation runs:** both failing tests pass individually; the class alone fails 2/18 — deterministic contamination, not flakiness.
4. **SQL Server (Testcontainers):** 24/24 passed fresh, including `Migrations_Phase2Schema_NoPendingMigrations_CommercialColumnsExist` (`Assert.Empty(await db.Database.GetPendingMigrationsAsync())` on a DB migrated through `AddTeacherSalaryModule`).
5. **EF CLI:** `dotnet ef migrations has-pending-model-changes --context AppDbContext` → "No changes have been made to the model since the last migration."
6. **Runtime EF-model probe** (standalone console app under `%TEMP%\centerix-audit-probe`, referencing the built projects — repository untouched): printed `IEntityType.GetQueryFilter()` for every Centerix entity — all `IHasTenantId` entities carry exactly one filter (`TenantId == _currentTenant.TenantId`); no soft-delete predicate remains on Teacher/Student/Branch.
7. **Runtime domain probe** (same app): the transition table in §9; `Teacher.SoftDelete` → `DeletedBy=null`; `TeacherSalaryConfig.Create(EffectiveFrom: default)` accepted; `Percentage > 100` rejected.
8. **Static verification:** config ↔ snapshot ↔ migration line references for all three named unique indexes and all FKs are cited in §10.

*Audit performed in AUDIT-ONLY mode. No source files, migrations, or tests were created or modified inside the repository; the only artifact produced is this report.*

---

**FINAL VERDICT: FAIL**

Blocking findings: F-01 (feature gating missing on mutations, incl. MarkPaid/Cancel), F-02 (salary state machine bypass), F-03 (SalaryPayment concurrency unprotected), F-04 (soft-delete read path broken on shared infrastructure, affects Teachers and Students).

<!-- END -->
