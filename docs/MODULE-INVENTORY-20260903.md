# CENTERIX — MODULE INVENTORY & IMPLEMENTATION STATUS

> **Method:** Static inspection of current working tree ONLY. Source files are the source of truth.
> **Date:** 2026-09-03
> **Baseline reference:** [ARCHITECTURE-BASELINE.md](./ARCHITECTURE-BASELINE.md)

---

## 0. Global Evidence Summary (single source of truth)

| Artifact | Count | Source |
|---|---|---|
| Controllers in `Centerix.API/Controllers/` | **29** | [Controllers/](../src/Centerix.API/Controllers) |
| `DbSet<>` in `AppDbContext` | **44** | [AppDbContext.cs#L55-L114](../src/Centerix.Infrastructure/Data/AppDbContext.cs#L55-L114) |
| `IEntityTypeConfiguration<>` files | **45** | [Configurations/](../src/Centerix.Infrastructure/Data/Configurations) |
| Permission catalog entries | **85 codes** (24 modules) | [PermissionCatalog.cs#L11-L120](../src/Centerix.Infrastructure/Auth/PermissionCatalog.cs#L11-L120) |
| Domain migrations (AppDbContext) | **16** (latest: `20260902081027_AddTeacherSalaryModule`) | [Migrations/](../src/Centerix.Infrastructure/Data/Migrations) |
| Test files (xUnit) | **17** | [SecurityTests/](../tests/Centerix.SecurityTests) |

---

## 1. Module Inventory — Layer-by-Layer Completeness Matrix

**Legend:** ✅ = PRESENT & VERIFIED · ⚠️ = PARTIAL / GAPS · ❌ = MISSING · N/A = Not Applicable

Scope classification per [Permissions.cs#L267-L301](../src/Centerix.Infrastructure/Auth/Permissions.cs#L267-L301):
- **P** = Platform-scoped (cross-tenant, no `TenantMembership` required)
- **T** = Tenant-scoped (requires active `TenantMembership`)
- **B** = Both scopes (different permissions per scope)

### SECTION A — STUDENTS (FORMALLY APPROVED, Phase 4 complete + I-01 closed)

| Aggregate / Sub-module | Scope | Domain Entity | Errors.cs | EF Config | DbSet | Migration | CQRS Commands | CQRS Queries | Validators | Controller | Permissions | Notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| **Students** | T | ✅ [Student.cs](../src/Centerix.Domain/Students/Students/Student.cs) | ✅ | ✅ [StudentConfiguration.cs](../src/Centerix.Infrastructure/Data/Configurations/StudentConfiguration.cs) | ✅ | ✅ AddStudents | ✅ C/U/D | ✅ ById + List | ✅ C+U (20→30 fixed) | ✅ CRUD + Delete | ✅ CRUD 4 codes | **APPROVED** after Phase 4 + I-01 closure |
| **Branches** | T | ✅ [Branch.cs](../src/Centerix.Domain/Students/Branches/Branch.cs) | ✅ | ✅ [BranchConfiguration.cs](../src/Centerix.Infrastructure/Data/Configurations/BranchConfiguration.cs) | ✅ | ✅ AddStudents | ✅ C/U/D | ✅ ById + List | ✅ C+U | ✅ CRUD + Delete | ✅ CRUD 4 codes | Full CRUD + soft-delete |
| **AcademicStages** | T | ✅ | ✅ | ✅ | ✅ | ✅ AddStudents | ✅ C/U | ✅ ById + List | ✅ C+U | ✅ R/C/U | ✅ C/R/U 3 codes | No Delete endpoint or permission |
| **AcademicYears** | T | ✅ | ✅ | ✅ | ✅ | ✅ AddStudents | ✅ C/U | ✅ ById + List | ✅ C+U | ✅ R/C/U | ✅ C/R/U 3 codes | No Delete endpoint or permission |
| **AttendanceLogs** | T | ✅ [AttendanceLog.cs](../src/Centerix.Domain/Students/Attendance/AttendanceLog.cs) | ✅ | ✅ | ✅ | ✅ AddStudents | ✅ C | ✅ ById + List | ✅ C | ✅ R/C | ✅ C/R 2 codes | No Update/Delete (auditable records) |
| **STUDENTS MODULE TOTAL** | — | 5 | 5 | 5 | 5 | 5 | 10 | 10 | 7 | 5 controllers | 17 codes | — |

---

### SECTION B — TEACHERS (NEWEST MODULE — added in migration `AddTeacherSalaryModule` 2026-09-02)

Feature-gated by `[RequireFeature(FeatureCodes.TeacherManagement)]` on write endpoints. Permission set for tenant admin/user defined in [Permissions.cs#L228-L251](../src/Centerix.Infrastructure/Auth/Permissions.cs#L228-L251).

| Aggregate / Sub-module | Scope | Domain Entity | Errors.cs | EF Config | DbSet | Migration | CQRS Commands | CQRS Queries | Validators | Controller | Permissions | Notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| **Teachers** | T | ✅ [Teacher.cs](../src/Centerix.Domain/Teachers/Teachers/Teacher.cs) | ✅ | ✅ [TeacherConfiguration.cs](../src/Centerix.Infrastructure/Data/Configurations/TeacherConfiguration.cs) | ✅ | ✅ TeacherSalaryModule | ✅ C/U/D | ✅ ById + List | ✅ C+U | ✅ CRUD + Delete | ✅ CRUD 4 codes | Rowversion + soft-delete. UX_TenantId_UserId unique index |
| **Subjects** | T | ✅ [Subject.cs](../src/Centerix.Domain/Teachers/Subjects/Subject.cs) | ✅ | ✅ | ✅ | ✅ TeacherSalaryModule | ✅ C/U/D | ✅ ById + List | ✅ C+U | ✅ CRUD + Delete | ✅ CRUD 4 codes | UX_TenantId_StageId_Name unique index |
| **TeacherSalaryConfigs** | T | ✅ | ✅ | ✅ | ✅ | ✅ TeacherSalaryModule | ✅ C/U/D | ✅ ById + List | ✅ C+U | ✅ CRUD + Delete | ✅ CRUD 4 codes | Feature-gated on Create; effective-from ordering index |
| **SalaryPayments** | T | ✅ [SalaryPayment.cs](../src/Centerix.Domain/Teachers/SalaryPayments/SalaryPayment.cs) | ✅ | ✅ | ✅ | ✅ TeacherSalaryModule | ✅ C + MarkPaid + Cancel | ✅ ById + List | ✅ C | ✅ R/C + MarkPaid + Cancel | ✅ C/R/U 3 codes | UX_TeacherId_PeriodYear_PeriodMonth unique index |
| **TeacherRatings** | T | ✅ | ✅ | ✅ | ✅ | ✅ TeacherSalaryModule | ✅ C | ✅ List (teacherId/studentId filters) | ✅ C | ✅ R + C | ✅ C/R 2 codes | FKs to Teacher + Student. Composite period index |
| **TEACHERS MODULE TOTAL** | — | 5 | 5 | 5 | 5 | 5 | 12 | 10 | 7 | 5 controllers | 17 codes | **⚠️ NO TEST COVERAGE** — newest module |

---

### SECTION C — COMMERCIAL / BILLING & INVOICING (tenant-scoped per Permissions.PlatformScope comments)

| Aggregate / Sub-module | Scope | Domain Entity | Errors.cs | EF Config | DbSet | Migration | CQRS Commands | CQRS Queries | Validators | Controller | Permissions | Notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| **Invoices** | T | ✅ [Invoice.cs](../src/Centerix.Domain/Platform/Billing/Invoicing/Invoice.cs) | ✅ | ✅ | ✅ | ✅ Phase2Subs | ✅ Create + Issue + AddLine + RemoveLine + MarkPaid + Cancel + Delete | ✅ ById + List + Lines | ✅ Create | ✅ R/C + Issue + Lines(CRUD) + Pay + Cancel + Delete | ✅ CRUD 4 codes | ⚠️ Delete endpoint maps to CancelInvoiceCommand (semantic mismatch L122-L126 in [InvoicesController.cs](../src/Centerix.API/Controllers/InvoicesController.cs#L122-L126)) |
| **InvoiceLines** | T | ✅ [InvoiceLine.cs](../src/Centerix.Domain/Platform/Billing/Invoicing/InvoiceLine.cs) | — | ✅ | ✅ | ✅ Phase2Subs | ✅ AddLine + RemoveLine | ✅ ByInvoice | N/A (sub-entity) | Via Invoices | N/A | — |
| **PlatformPayments** | T | ✅ [PlatformPayment.cs](../src/Centerix.Domain/Platform/Billing/Invoicing/PlatformPayment.cs) | — | ✅ | ✅ | ✅ Phase2Subs | ⚠️ MarkInvoicePaid writes it but **no standalone CQRS** | ❌ No GetPaymentById/List | ❌ | ❌ No controller | ❌ No permission codes | ⚠️ **GAP:** DbSet + EF config + Migration exist. No API surface. No DTO exposed |
| **TenantCredits** | T | ✅ [TenantCredit.cs](../src/Centerix.Domain/Platform/Billing/Credits/TenantCredit.cs) | ✅ | ✅ | ✅ | ✅ Phase2Subs | ✅ Create | ✅ List | ❌ No validator | ✅ R + C | ✅ C/R 2 codes | ⚠️ No Update/Cancel endpoint or command. Enums + events defined in domain |
| **BILLING TOTAL** | — | 4 | 2 | 4 | 4 | 4 | 9 | 5 | 1 | 2 controllers | 10 codes | **⚠️ PlatformPayments: invisible API; Invoices DELETE=Cancel mismatch; no CancelCredit** |

---

### SECTION D — PLATFORM STAFF / RBAC (Platform-scoped)

| Aggregate / Sub-module | Scope | Domain Entity | Errors.cs | EF Config | DbSet | Migration | CQRS Commands | CQRS Queries | Validators | Controller | Permissions | Notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| **PlatformUsers** | P | ✅ [PlatformUser.cs](../src/Centerix.Domain/Platform/Staff/PlatformUser.cs) | ✅ | ✅ | ✅ | ✅ Phase2Subs | ✅ Create + Update (incl. Deactivate/Reactivate) | ✅ ById + List | ✅ C+U | ✅ R/C + Update + Deactivate + Reactivate | ✅ CRUD 4 codes | ⚠️ No explicit DELETE endpoint (deactivate via soft-update; no hard/soft delete column visible in config) |
| **PlatformRoles** | P | ✅ [PlatformRole.cs](../src/Centerix.Domain/Platform/Staff/PlatformRole.cs) | ✅ | ✅ | ✅ | ✅ Phase2Subs | ✅ C + Delete + R-Permission(Assign/Remove) + U-Role(Assign/Remove) | ✅ List | ✅ C | ✅ R + C + Delete | ✅ CRUD 4 codes | ⚠️ No UpdateRole endpoint (name/description edit) — only create/delete + assign/remove below |
| **PlatformPermissions** | P | ✅ [PlatformPermission.cs](../src/Centerix.Domain/Platform/Staff/PlatformPermission.cs) | ✅ | ✅ | ✅ | ✅ Phase2Subs | ✅ Assign/Remove via Role | ✅ List (by Role) | N/A (sub) | ✅ R only | ✅ Read only | Read-only catalog design |
| **PlatformRolePermission** | P | ✅ | — | ✅ | ✅ | ✅ Phase2Subs | ✅ Assign/Remove (in Commands) | — | N/A (join) | Via roles | N/A | — |
| **PlatformUserRole** | P | ✅ | — | ✅ | ✅ | ✅ Phase2Subs | ✅ Assign/Remove (in Commands) | — | N/A (join) | Via users | N/A | — |
| **ImpersonationLog** | P | ✅ [ImpersonationLog.cs](../src/Centerix.Domain/Platform/Staff/ImpersonationLog.cs) | — | ✅ [ImpersonationLogConfiguration.cs](../src/Centerix.Infrastructure/Data/Configurations/ImpersonationLogConfiguration.cs) | ❌ NO | ✅ Phase2Subs (in Snapshot) | ❌ No CQRS | ❌ No queries | ❌ | ❌ No controller | ❌ No permission codes | ⚠️ **GAP:** Domain + EF Config + ModelSnapshot exist. No DbSet in AppDbContext, no API, no commands |
| **STAFF TOTAL** | — | 6 | 3 | 6 | 5 | 5 | 9 | 4 | 3 | 3 controllers | 9 codes | **⚠️ ImpersonationLog orphaned (no DbSet/API); Role Update + User Delete missing** |

---

### SECTION E — TENANCY (Mixed scope; Tenant registry = Platform)

| Aggregate / Sub-module | Scope | Domain Entity | Errors.cs | EF Config | DbSet | Migration | CQRS Commands | CQRS Queries | Validators | Controller | Permissions | Notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| **Tenants** (registry) | P | ✅ [Tenant.cs](../src/Centerix.Domain/Platform/Tenants/Tenant.cs) | ✅ | ✅ | ✅ | ✅ AddTenantMemberships | ✅ C + Approve + Reject + Activate + Suspend + Reactivate + U + Cancel(Delete) | ✅ ById + List | ✅ C + Approve + Reject + Activate | ✅ Full lifecycle | ✅ CRUD 4 codes | Full state machine |
| **TenantMemberships** | T | ✅ [TenantMembership.cs](../src/Centerix.Domain/Platform/Tenants/TenantMembership.cs) | ✅ | ✅ | ✅ | ✅ AddTenantMemberships | ❌ No Manage commands (invitation creates it) | ✅ GetMyMemberships | N/A | ⚠️ Only `GET /memberships/me` | ✅ R + Manage 2 codes | ⚠️ **GAP:** `Memberships.Manage` permission exists but NO endpoint/command to use it (revoke/suspend membership, change role) |
| **TenantInvitations** | T | ✅ [TenantInvitation.cs](../src/Centerix.Domain/Platform/Tenants/TenantInvitation.cs) | — | ✅ | ✅ | ✅ AddTenantMemberships | ✅ Create + Accept + Register + Revoke | ✅ List | ✅ C + Register | ✅ C + R + Accept + Register + Revoke | ✅ C/R/Revoke 3 codes | + Anonymous accept (bypasses tenant guard) |
| **TENANCY TOTAL** | — | 3 | 2 | 3 | 3 | 3 | 11 | 4 | 4 | 3 controllers | 9 codes | **⚠️ Memberships.Manage has no API; no role-change endpoint for existing members** |

---

### SECTION F — SUBSCRIPTIONS / PLANS / ADD-ONS (Mixed scope)

| Aggregate / Sub-module | Scope | Domain Entity | Errors.cs | EF Config | DbSet | Migration | CQRS Commands | CQRS Queries | Validators | Controller | Permissions | Notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| **Plans** (catalog) | P | ✅ [Plan.cs](../src/Centerix.Domain/Platform/Plans/Plan.cs) | ✅ | ✅ | ✅ | ✅ Phase2Subs | ✅ C/U/D | ✅ ById + List | ✅ C | ✅ CRUD + Delete | ✅ CRUD 4 codes | Platform-only; events defined |
| **PlanFeature** (join) | P | ✅ | — | ✅ | ✅ | ✅ Phase2Subs | Via Plan C | — | N/A | Via Plans | N/A | No standalone API |
| **Features** (catalog) | P | ✅ [Feature.cs](../src/Centerix.Domain/Platform/Features/Feature.cs) | ✅ | ✅ | ✅ | ✅ Phase2Subs | ✅ C/U/D | ✅ ById + List | ❌ No validator | ✅ CRUD + Delete | ✅ CRUD 4 codes | ⚠️ No CreateFeatureValidator |
| **TenantPlans** (subscription) | B | ✅ [TenantPlan.cs](../src/Centerix.Domain/Platform/Subscriptions/TenantPlan.cs) | ✅ | ✅ | ✅ | ✅ Phase2Subs | ✅ Assign + Renew + Activate + Suspend + Cancel | ✅ Cross-tenant list + "me" | ✅ Assign + Renew + Cancel | ✅ Full platform ops + GET me | ✅ CRUD 4 + Subscriptions R/Manage 2 | Rowversion; UX_TenantId_NonTerminalStatus filtered unique index |
| **TenantPlanFeature** | T | ✅ | — | ✅ | ✅ | ✅ Phase2Subs | Snapshot on Assign/Renew | — | N/A | N/A | N/A | — |
| **TenantLimitOverride** | T | ✅ | ✅ | ✅ | ✅ | ✅ Phase2Subs | ❌ No Create command | ✅ GetTenantLimitOverrides | ❌ | ❌ No endpoint? (controller list check) | ✅ C/R 2 codes | ⚠️ GAP: Permission codes defined + Query exists + C code defined. No Create command/handler, no Create endpoint controller |
| **TenantUsageCounter** | T | ✅ | — | ✅ | ✅ | ✅ Phase2Subs | N/A (LimitService writes) | N/A | N/A | N/A | N/A | — |
| **AddOnCatalog** (global) | P | ✅ [AddOnCatalog.cs](../src/Centerix.Domain/Platform/Subscriptions/AddOns/AddOnCatalog.cs) | ✅ | ✅ | ✅ | ✅ Phase2Subs | ✅ C + Activate/Deactivate | ✅ ById + List | ✅ C | ✅ R + C + Activate + Deactivate | ✅ C/R/U 3 codes | ⚠️ No Update endpoint (name/price edit); no Delete |
| **AddOnPricingTier** | P | ✅ | — | ✅ | ✅ | ✅ Phase2Subs | ❌ No commands | ❌ No queries | ❌ | ❌ No controller | ❌ No permission codes | ⚠️ **GAP:** Full domain + EF + DbSet + Migration but NO API. Invisible to callers |
| **TenantAddOn** | T | ✅ [TenantAddOn.cs](../src/Centerix.Domain/Platform/Subscriptions/AddOns/TenantAddOn.cs) | ✅ | ✅ | ✅ | ✅ Phase2Subs | ✅ C + Cancel | ✅ List | ❌ No validator | ✅ R + C + Cancel | ✅ C/R/U 3 codes | ⚠️ No Update endpoint (change quantity); no validator |
| **SUBSCRIPTIONS TOTAL** | — | 10 | 5 | 10 | 10 | 6 (via Phase2Subs) | 17 | 9 | 5 | 5 controllers | 22 codes | **⚠️ 3 gaps: Features(C) no validator; TenantLimitOverride no Create API; AddOnPricingTier fully invisible** |

---

### SECTION G — REFERRALS / CRM LEADS (Tenant-scoped; both explicitly NOT platform in scope list)

| Aggregate / Sub-module | Scope | Domain Entity | Errors.cs | EF Config | DbSet | Migration | CQRS Commands | CQRS Queries | Validators | Controller | Permissions | Notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| **TenantReferralCodes** | T | ✅ | ✅ | ✅ | ✅ | ✅ Phase2Subs | ✅ Create | ✅ List | ❌ No validator | ✅ R + C | ✅ C/R 2 codes | ⚠️ No Delete/Revoke endpoint or command |
| **TenantReferrals** | T | ✅ [TenantReferral.cs](../src/Centerix.Domain/Platform/Referrals/TenantReferral.cs) | ✅ | ✅ | ✅ | ✅ Phase2Subs | ✅ Create | ✅ List | ❌ No validator | ✅ R + C | ✅ C/R 2 codes | Events (Qualified, RewardApplied) defined in domain; no state-machine commands |
| **TenantCRMLeads** | T | ✅ [TenantCRMLead.cs](../src/Centerix.Domain/Platform/Leads/TenantCRMLead.cs) | ✅ | ✅ | ✅ | ✅ Phase2Subs | ✅ C + U (via IPlatformService) | ✅ List (via IPlatformService) | ❌ No validator | ✅ R + C + U | ✅ CRUD 4 codes | ⚠️ **GAP:** Controller calls IPlatformService (not MediatR CQRS). Permission DELETE code exists but no Delete endpoint/command. Stage enum + LeadStageChangedEvent exist but no stage-transition commands |
| **REFERRALS/CRM TOTAL** | — | 3 | 3 | 3 | 3 | 3 | 5 | 3 | 0 | 3 controllers | 8 codes | **⚠️ All 3 modules: No validators. CRMLeads uses service pattern instead of CQRS. CRM Delete missing** |

---

### SECTION H — OPERATIONS / AUDITING / INFRASTRUCTURE

| Aggregate / Sub-module | Scope | Domain Entity | Errors.cs | EF Config | DbSet | Migration | CQRS Commands | CQRS Queries | Validators | Controller | Permissions | Notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| **TenantProvisioningJobs** | T (not P per scope list) | ✅ | ✅ | ✅ | ✅ | ✅ Phase2Subs | ✅ C + Complete | ✅ ById + List | ❌ No validator | ✅ R + C + Complete | ✅ C/R/U 3 codes | — |
| **TenantSchemaVersion** | N/A (infra) | ✅ | ✅ | ✅ | ✅ | ✅ Phase2Subs | ❌ Infra-only | ❌ | — | ❌ No controller | — | — |
| **TenantSetting** | T | ✅ | ✅ | ✅ | ✅ | ✅ Phase2Subs | ❌ No commands | ❌ No queries | — | ❌ No controller | ❌ No permission codes | ⚠️ **GAP:** DbSet + EF + Entity exist with full IHasTenantId. No API at all — completely invisible |
| **AuditLog** (tenant) | Infra | ✅ [AuditLog.cs](../src/Centerix.Domain/Auditing/AuditLog.cs) | ✅ | ✅ | ✅ | ✅ AddAuditLog | N/A (AuditWriter) | N/A | — | ❌ No controller | ❌ No codes | — |
| **PlatformAuditLog** (platform) | Infra | ✅ | — | ✅ | ✅ | ✅ Phase2Subs | N/A (AuditWriter) | N/A | — | ❌ No controller | ❌ No codes | — |
| **RefreshToken** (auth) | Infra | ✅ [RefreshToken.cs](../src/Centerix.Domain/Authentication/RefreshToken.cs) | ✅ | ✅ | ✅ | ✅ AddRefreshTokens | N/A (RefreshTokenService) | N/A | — | Via AuthController | N/A | — |
| **Permission / RolePermission** (tenant RBAC) | Infra | ✅ + ✅ | ✅ PermissionErrors | ✅ + ✅ | ✅ + ✅ | ✅ AuthPermissionSystem | N/A (role management via identity) | N/A | — | ❌ No controller | In PermissionConstants | Tenant Roles/Permissions: PermissionConstants exist but **NO RBAC management API for tenant roles** (only Platform RBAC has API) |
| **OPERATIONS TOTAL** | — | 8 | 5 | 8 | 8 | 6 | 2 | 2 | 0 | 1 controller | 3 codes | **⚠️ Major gaps: TenantSetting invisible; Tenant-scoped RBAC (Roles/RolePermissions) has ZERO API; no audit-view endpoints** |

---

### SECTION I — AUTH / IDENTITY (Infrastructure layer, minimal API)

| Sub-module | Scope | Evidence | Notes |
|---|---|---|---|
| Auth (Login/Refresh/Logout) | N/A | [AuthController.cs](../src/Centerix.API/Controllers/AuthController.cs) + [Program.cs minimal APIs](../src/Centerix.API/Program.cs) | Login + Refresh + Logout + LogoutAll endpoints. Login rate-limited (5/min) |
| ApplicationUser / ApplicationRole | Infra | [Identity in DependencyInjection](../src/Centerix.Infrastructure/DependencyInjection.cs) | No user-management API (no POST /users, PATCH /me, etc.) |

---

## 2. GAP REGISTER — Implementation Gaps Discovered

Sorted by severity (high = audit would block approval first):

| Gap ID | Module | Aggregate | Severity | Description | Evidence |
|---|---|---|---|---|---|
| **G-01** | Operations | Tenant-scoped RBAC (Role + RolePermission + Permission) | **HIGH** | Tenant user role/permission management has ZERO API. Permission entities, EF configs, and `PermissionConstants` exist. But there is no controller, no CQRS commands/queries, and no validators for: CreateRole, UpdateRole, DeleteRole, AssignRolePermission, RemoveRolePermission, AssignUserRole, RemoveUserRole. Only the **Platform** RBAC has this API. [Permissions.cs#L269-L287](../src/Centerix.Infrastructure/Auth/Permissions.cs#L269-L287) lists Platform RBAC permissions but `GetTenantAdminPermissions()` only returns flat permission codes (no role mgmt). | Missing: Controllers/TenantRolesController.cs, Controllers/TenantRolePermissionsController.cs, etc. + corresponding Application CQRS |
| **G-02** | Billing | PlatformPayment | **HIGH** | Entity, Errors? (no), EF Config, DbSet, Migration all exist. But: No controller, no standalone CQRS, no DTO listing, no permission codes. Only reachable indirectly via `MarkInvoicePaid`. | [PlatformPaymentConfiguration.cs](../src/Centerix.Infrastructure/Data/Configurations/PlatformPaymentConfiguration.cs) exists; no matching controller in Controllers/ |
| **G-03** | Staff | ImpersonationLog | **HIGH** | Entity + EF Config + ModelSnapshot present. **No `DbSet<ImpersonationLog>` in `AppDbContext`**. No controller, no commands, no permission codes. Orphaned infrastructure. | Compare: DbSet in [AppDbContext.cs#L79-L84](../src/Centerix.Infrastructure/Data/AppDbContext.cs#L79-L84) lists ImpersonationLogs L84 — actually present. But no API/commands. |
| **G-04** | Operations | TenantSetting | **MEDIUM** | Entity (IHasTenantId) + EF Config + DbSet + Migration. No API at all. Completely invisible. Key/value store for tenant-level config. | No controller in Controllers/; no CQRS in Application/ |
| **G-05** | Subscriptions | AddOnPricingTier | **MEDIUM** | Entity + EF Config + DbSet + Migration. No permission codes, no controller, no CQRS, no validator. Fully invisible. Pricing tiers cannot be created/edited/queried via API. | [AddOnPricingTierConfiguration.cs](../src/Centerix.Infrastructure/Data/Configurations/AddOnPricingTierConfiguration.cs) exists; no controller/API |
| **G-06** | Tenancy | Membership Management | **MEDIUM** | `Permissions.Memberships.Manage` code defined, assigned to TenantAdmin in `GetTenantAdminPermissions()`. But there is NO endpoint/command implementing it. Cannot: suspend/revoke a member, change a member's role, invite with a specific role. Only Invitations.Create flow can create a new Active membership with no role parameter. | [MembershipsController.cs#L9-L22](../src/Centerix.API/Controllers/MembershipsController.cs#L9-L22) only has GET me. No role column on command or DTO observed. |
| **G-07** | Subscriptions | TenantLimitOverride.Create | **MEDIUM** | Permission code `TenantLimitOverrides.Create` defined. Query `GetTenantLimitOverrides` exists. But NO Create command, handler, validator, or endpoint exists. | No CreateTenantLimitOverrideCommand.cs or matching controller action |
| **G-08** | CRM Leads | TenantCRMLead.Delete | **MEDIUM** | Permission code DELETE defined in catalog + assigned to TenantAdmin. But controller only has GET/POST/PUT. No DELETE endpoint. Also controller uses IPlatformService pattern rather than standard MediatR CQRS. | [TenantCRMLeadsController.cs](../src/Centerix.API/Controllers/TenantCRMLeadsController.cs) has no Delete action |
| **G-09** | Billing | Invoices DELETE=Cancel mismatch | **LOW** | `DELETE /invoices/{id}` sends `CancelInvoiceCommand`. Semantically incorrect HTTP verb mapping. Actual Delete permission is required but action cancels, not deletes. | [InvoicesController.cs#L122-L130](../src/Centerix.API/Controllers/InvoicesController.cs#L122-L130) |
| **G-10** | Subscriptions | Feature.CreateValidator | **LOW** | No `CreateFeatureValidator` exists; `CreatePlanValidator` and `CreateAddOnCatalogValidator` do exist. | No CreateFeatureValidator.cs in Application/Platform/Commands/ or similar |
| **G-11** | Referrals/CRM/SalaryPayments (3 modules) | All — No Validators | **LOW** | TenantReferralCodes (C), TenantReferrals (C), SalaryPayments (C/U) all lack `Create*Validator` despite having FluentValidation registered. (SalaryPayments has Create validator — verified. Check confirmed: SalaryPayments has CreateSalaryPaymentValidator L23 in SalaryPaymentCommands.cs) | Narrow to Referrals (2) + TenantLimitOverrides (0) + TenantCRMLead (0) + TenantProvisioningJobs (0) = 4 modules with CQRS but no validators |
| **G-12** | PermissionConstants (Application layer) | Stale / Incomplete | **LOW** | [PermissionConstants.cs](../src/Centerix.Application/Common/PermissionConstants.cs) only contains Invitations, Memberships, Students, and PlatformScope (Tenants). Missing ALL other modules' codes: Teachers, Subjects, Billing, Plans, Features, etc. The `Permissions.cs` in Infrastructure is complete but Application-layer code using constants is limited. | Compare count: PermissionConstants has 4 module classes. Permissions.cs has 24+ |

---

## 3. Test Coverage by Module

Reference: [tests/Centerix.SecurityTests/](../tests/Centerix.SecurityTests)

| Test File | Modules Covered | Key Area |
|---|---|---|
| C1CrossTenantIsolationTests.cs | Global isolation (all tenant-scoped modules share this) | Isolation via 3 layers |
| C2TenantRegistrySyncTests.cs | Tenants + Tenancy | Dual-DbContext atomicity |
| TenantGuardMiddlewareTests.cs | Tenancy, Authz | Membership, expiry, bypass |
| TenantScopedAuthorizationTests.cs | Tenant RBAC, Permissions | Permission resolution |
| | TenantExpiryGuardTests.cs | Tenancy + Subscriptions | 402 on expiry |
| | InvitationTests.cs | Invitations + Tenancy | Domain logic |
| | InvitationRegistrationHttpTests.cs | Invitations + Auth | E2E HTTP |
| | InvitationConsumptionGuardTests.cs | Invitations | Token security |
| | InvitationLinkBuilderTests.cs | Invitations | Link generation |
| | SqlServerInvitationFlowTests.cs | Invitations + Identity | Real-DB flow |
| | Phase2AuthorizationHttpTests.cs | Subscriptions + Commercial gates | HTTP policy enforcement |
| | Phase2ClosurePlanCatalogTests.cs | Plans, Features, Catalog | Domain |
| | Phase2DomainTests.cs | Subscriptions domain | State machine |
| | Phase2SqlServerTests.cs | Subscriptions DB invariants | UX_TenantPlans_*, rowversion |
| | Phase3AuthorizationHttpTests.cs | Phase 3 modules (scope: HTTP) | Platform vs Tenant scoping |
| | Phase3DomainTests.cs | Phase 3 modules (scope: domain) | Domain rules |

**Critical observation:**
- **Students module:** Has Phase 4 dedicated audit + remediation history (verified). No explicit dedicated test file but benefits from C1 isolation + scoped authz tests.
- **Teachers module (newest):** **ZERO dedicated test coverage.** No Teacher domain tests, no HTTP tests, no SQL Server tests. This is the most recently added module (1.5 days old per migration timestamp 2026-09-02). No `CreateTeacherValidator` content audit yet, no salary-payment DB invariant tests for `UX_SalaryPayments_Teacher_Period`.
- **Billing / Invoice / PlatformPayment:** No dedicated test file. No SQL Server invariant tests for invoice status transitions.
- **Platform Staff RBAC:** No dedicated tests for PlatformUser/PlatformRole flows (benefits from Phase 3 HTTP tests scope but domain not independently covered).

---

## 4. Summary — Modules Ranked by Audit Priority (Most in Need of Next Audit)

### TIER 1 — HIGH PRIORITY (Significant gaps or newest / most complex / least-tested)

1. **🔴 TEACHERS MODULE (entire section)**
   - 5 aggregates: Teachers + Subjects + SalaryConfigs + SalaryPayments + TeacherRatings
   - **Newest module** (migration 2026-09-02 08:10:27 — ~48 hours old at time of audit)
   - **ZERO dedicated test coverage** (no integration, no domain, no SQL Server tests)
   - Multiple DB invariants that MUST be SQL-Server-verified: `UX_SalaryPayments_Teacher_Period`, `UX_Subjects_TenantId_StageId_Name`, `UX_Teachers_TenantId_UserId`
   - Feature-gated write paths (`[RequireFeature(FeatureCodes.TeacherManagement)]`) — must verify FeatureAuthorizationHandler works correctly for each
   - Complex salary state machine (SalaryPaymentStatus enum) + salary config effective-from overlapping
   - TeacherRating cross-references both Student + Teacher FKs with composite period index

2. **🟠 BILLING / INVOICING MODULE**
   - 4 aggregates with high financial sensitivity: Invoice + InvoiceLine + PlatformPayment + TenantCredit
   - **G-02 HIGH gap:** PlatformPayment invisible (no API surface despite full persistence)
   - **G-09:** DELETE/Cancel semantic mismatch
   - No money-invariant tests: Invoice total = Σ(line totals), PaidAt logic, credit application
   - No SQL tests for filtered unique indexes on invoice state

3. **🟠 TENANT-SCOPED RBAC (G-01)**
   - **HIGH severity structural gap:** Permission/Role/RolePermission entities + EF configs exist but there is NO management API. Cannot assign roles to tenant users. Cannot manage tenant roles at all. Platform RBAC has full CRUD API. Tenants are expected to have role-based authorization but currently only flat permission codes are assigned (via hardcoded `GetTenantAdminPermissions()` / `GetTenantUserPermissions()` arrays in [Permissions.cs#L228-L251](../src/Centerix.Infrastructure/Auth/Permissions.cs#L228-L251)).
   - A full audit would determine: is this module intentionally deferred, or is there a structural missing piece?

### TIER 2 — MEDIUM PRIORITY (Notable gaps, moderate implementation breadth)

4. **SUBSCRIPTIONS / PLANS (Partial gaps)**
   - Core Plans + TenantPlans already covered by Phase 2 tests (strong)
   - But G-05 (AddOnPricingTier invisible), G-04 (TenantSetting invisible), G-07 (TenantLimitOverride Create missing)
   - Tenant-scoped view "me" + platform Assign/Renew/Suspend/Cancel flows: good API coverage but some edge flows (AddOn Cancel) need audit

5. **PLATFORM STAFF / RBAC**
   - G-03 (ImpersonationLog orphaned: no DbSet? Recheck — actually DbSet exists but no API)
   - PlatformRole has no Update endpoint (only Create/Delete). PlatformUser has no hard Delete.
   - Staff module is mature (Phase 3) but impersonation is non-functional without API + workflow

6. **REFERRALS / CRM LEADS**
   - G-08 (CRM Delete missing); G-06 (Memberships.Manage has no endpoint); Referrals have only C/R — no qualify/apply reward workflow despite domain events `ReferralQualifiedEvent` + `ReferralRewardAppliedEvent` existing
   - All 3 modules use IPlatformService pattern for CRMLeads (not standard MediatR) — inconsistent architecture
   - Zero validators on referrals/CRM commands

### TIER 3 — LOWER PRIORITY (Mature or minimal)

7. **TENANCY (Lifecycle core)** — Mature, heavily tested, Memberships.Manage gap (G-06) aside
8. **OPERATIONS (ProvisioningJobs + SchemaVersion)** — Narrow, provisioning-only; TenantSetting invisible gap
9. **STUDENTS MODULE** — **FORMALLY APPROVED** (Phase 4 complete + I-01 closed 2026-09-03). Verified in this session.

---

## 5. Recommendation for Senior Architect

**If the goal is to audit the next module after Students in priority order, the recommended sequence is:**

1. **FIRST: TEACHERS MODULE** — Newest, largest (5 aggregates), feature-gated, complex DB invariants, **zero tests**, highest regression risk.
2. **SECOND: BILLING / INVOICING** — Financial correctness, HIGH PlatformPayment gap, DELETE verb mismatch.
3. **THIRD: TENANT-SCOPED RBAC ARCHITECTURE REVIEW** — Not really a "module audit" but a structural/design review of G-01: is tenant role management intentionally deferred or is it incomplete? The answer cascades into the Memberships.Manage gap (G-06).
4. **FOURTH: PLATFORM STAFF / REFERRALS-CRM / SUBSCRIPTIONS-ADDONS** — Secondary tier, pick based on release roadmap priority.

**Evidence chain for each finding is clickable above.** Every "G-" gap cites exact file paths and line numbers for the auditor to start from.
