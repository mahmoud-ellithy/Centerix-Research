# TASK 22 — Authorization Redesign & Enforcement Integrity

Implementation/verification record. Not a generic audit: every problem listed below was
either fixed in this task and proven by a test, or is reported as an explicit ambiguity in §14.

---

## 1. Existing Authorization Architecture (baseline, preserved)

| Layer | Component | Role |
|---|---|---|
| Tenant resolution | Finbuckle `WithHeaderStrategy("tenant")` + `WithHostStrategy` + `EFCoreStore` | Client SELECTION only; never from JWT |
| Tenant authorization | `TenantGuardMiddleware` | Active `TenantMembership` check per request; loads tenant permissions into `HttpContext.Items`; tenant active/expiry gates |
| Verified tenant context | `CurrentTenant.AuthorizeTenant()` | `TenantId` is EMPTY until authorized → query filters fail closed |
| Data isolation | EF global query filters (`IHasTenantId`) + `TenantInterceptor` | Filter reads live `ICurrentTenant.TenantId` |
| Endpoint authorization | `[HasPermission]` → `PermissionPolicyProvider` → `PermissionAuthorizationHandler` | Permissions resolved per request from DB (Membership → Role → RolePermission → Permission) |
| Feature gating | `[RequireFeature]` → `FeatureAuthorizationHandler` | Subscription entitlement, independent of permission |
| Platform boundary | `IPlatformAdminGuard` in ~24 sensitive handlers | Handler-level defense in depth |
| Scope classification | `Permissions.PlatformScope.PermissionCodes` | Platform vs tenant endpoint classification driving the middleware |

## 2. Problems Actually Found

1. **P1 — PlatformAdmin identity trusted the JWT claim in three independent places.**
   `PermissionAuthorizationHandler`, `FeatureAuthorizationHandler` and `PlatformAdminGuard`
   (via `ICurrentUser.IsPlatformAdmin`) all short-circuited on `IsInRole("PlatformAdmin")`.
   A stale access token kept full platform privileges until expiry even after the role was
   revoked in the identity store; and the decision logic was duplicated in three places.
2. **P2 — Two non-authoritative "answers" to "is this user a PlatformAdmin".**
   `ICurrentUser.IsPlatformAdmin` (claim echo) and the handlers' inline claim checks were
   separate decisions with no DB backing.
3. **P3 — Production TenantAdmin/TenantUser role matrices were missing entire tenant modules.**
   `GetTenantAdminPermissions()` omitted `Students.*`, `AttendanceLogs.*`, `Branches.*`,
   `AcademicStages.*`, `AcademicYears.*`, `TenantAddOns.*`, `TenantReferralCodes.*`,
   `TenantReferrals.*`. The gap was **masked by the test factory** (`SeedPermissionsAsync`
   granted ALL permissions to TenantAdmin), so Phase 3 HTTP tests passed while production
   seeding would have 403'd TenantAdmin on `POST /api/students`.
4. **P4 — Test infrastructure proved nothing about the real role matrix.** Same root cause
   as P3: `TestWebApplicationFactory` and `TenantScopedAuthorizationTests` duplicated the
   "grant everything" convenience instead of the production lists.
5. **P5 — Two HTTP tests minted PlatformAdmin tokens with no identity-store backing.**
   (`C1CrossTenantIsolationTests.Test13`, `TenantScopedAuthorizationTests.Test4`.) Under a
   claim-only model they passed; they were exactly the forged-claim scenario T22 must deny.

Not found as defects (verified, no change needed): tenant filter fail-closed behavior,
middleware membership enforcement, scope classification list, feature-vs-permission
separation, fail-closed exception handling in the permission handler.

## 3. Decisions Made

1. **Single authoritative PlatformAdmin decision**: new `IPlatformAdminVerifier`
   (Application interface) + `PlatformAdminVerifier` (Infrastructure). All three former
   claim checks (P1) and the guard now delegate to it. The claim is a *necessary but not
   sufficient* hint; the Identity store is re-validated per request (scoped cache).
2. **No second authorization system**: the verifier only answers "is this principal a
   PlatformAdmin". Permission/feature/tenant mechanics are untouched.
3. **Guard became async**: `IPlatformAdminGuard.EnsurePlatformAdminAsync(CancellationToken)`
   — required for DB verification without sync-over-async. All 24 handler call sites updated.
4. **`ICurrentUser.IsPlatformAdmin` removed** (P2): no property may echo the claim as if it
   were an authorization decision.
5. **Role matrix**: only evidence-backed grants were added (§7). Ambiguous permissions were
   left unassigned and reported (§14), per the task's stop rules.
6. **Test factory mirrors production**: `SeedPermissionsAsync` now uses
   `Permissions.GetTenantAdminPermissions()/GetTenantUserPermissions()` verbatim (P3/P4).
7. **No migration**: the redesign reuses the existing Identity schema
   (`AspNetUsers`/`AspNetRoles`/`AspNetUserRoles` + lockout columns). `dotnet ef migrations
   has-pending-model-changes` = NO for both `AppDbContext` and `TenantDbContext`.

## 4. Files Changed

**New (production)**
- `src/Centerix.Application/Common/Interfaces/IPlatformAdminVerifier.cs`
- `src/Centerix.Infrastructure/Auth/PlatformAdminVerifier.cs`

**Modified (production)**
- `src/Centerix.Infrastructure/Auth/PermissionPolicyProvider.cs` — `PermissionAuthorizationHandler` bypass now DB-verified via the verifier
- `src/Centerix.Infrastructure/Auth/FeatureAuthorization.cs` — feature-gate PlatformAdmin bypass now DB-verified
- `src/Centerix.Application/Common/Interfaces/IPlatformAdminGuard.cs` — async contract
- `src/Centerix.Infrastructure/Common/PlatformAdminGuard.cs` — delegates to the verifier
- `src/Centerix.Application/Common/Interfaces/ICurrentUser.cs` — `IsPlatformAdmin` removed
- `src/Centerix.Infrastructure/Common/CurrentUser.cs` — `IsPlatformAdmin` removed
- `src/Centerix.Infrastructure/Auth/Permissions.cs` — TenantAdmin/TenantUser matrices (§7)
- `src/Centerix.Infrastructure/Auth/JwtTokenService.cs` — comment: role claim is a hint
- `src/Centerix.Infrastructure/DependencyInjection.cs` — verifier registration (scoped)
- 24 handler files under `src/Centerix.Application/Platform/**` — `await guard.EnsurePlatformAdminAsync(cancellationToken)`

**Modified (tests)**
- `tests/Centerix.SecurityTests/TestWebApplicationFactory.cs` — production-matrix seeding
- `tests/Centerix.SecurityTests/TenantScopedAuthorizationTests.cs` — production-matrix seeding + real PlatformAdmin provisioning (P5)
- `tests/Centerix.SecurityTests/C1CrossTenantIsolationTests.cs` — real PlatformAdmin provisioning (P5)
- `tests/Centerix.SecurityTests/Task201_PlatformAdminGuardTests.cs` — rewritten for the verifier-backed async guard
- ~28 test files — mechanical `EnsurePlatformAdminAsync(Arg.Any<CancellationToken>())` substitution updates

**New (tests)**
- `tests/Centerix.SecurityTests/Task22_PlatformAdminVerifierTests.cs` (10 tests)
- `tests/Centerix.SecurityTests/Task22_AuthorizationHttpTests.cs` (24 tests)
- `tests/Centerix.SecurityTests/Task22_PlatformAdminSqlServerTests.cs` (5 SQL tests)
- `tests/Centerix.SecurityTests/Task22_HandlerBypassTests.cs` (3 tests)

## 5. PlatformAdmin Authorization Design

```
JWT (PlatformAdmin role claim)          <- hint only, necessary but NOT sufficient
        |
        v
IPlatformAdminVerifier.IsPlatformAdminAsync(principal)
        |  1. principal authenticated?            else DENY
        |  2. PlatformAdmin role claim present?   else DENY
        |  3. NameIdentifier present?             else DENY
        |  4. user exists in AspNetUsers?         else DENY
        |  5. NOT locked out?                     else DENY   (revocation path 1)
        |  6. IsInRoleAsync(PlatformAdmin)?       else DENY   (revocation path 2)
        |  exception at any step -> DENY (fail-closed, logged, not cached)
        v
   ALLOW  (cached once per request scope)
```

Consumers (all of them): `PermissionAuthorizationHandler`, `FeatureAuthorizationHandler`,
`PlatformAdminGuard` (→ all 24 guarded commercial handlers).

Revocation semantics: `RemoveFromRoleAsync` or lockout takes effect on the **next request**,
not at token expiry (previously up to 60 minutes). A forged/tampered claim is never
sufficient because the store must independently confirm the role. Tenant membership
(`TenantMembership.RoleName`) is never consulted, so tenant-side grants cannot escalate.

Note: the `PlatformUser`/`PlatformRole`/`PlatformPermission` domain tables (BCrypt-based)
are a separate staff-record model that is **not** connected to authentication/login; the
authoritative identity store for the actual system is ASP.NET Core Identity. No change was
made here beyond this documented determination.

## 6. Permission Scope Matrix

Scope is classified by `Permissions.PlatformScope.PermissionCodes` (single source of truth;
unknown codes default to tenant-scoped = fail-closed in the middleware). Verified against
every `[HasPermission]` endpoint — no endpoint/scope mismatches found.

| Scope | Permissions |
|---|---|
| **Platform** (36) | `PlatformUsers.*` (4), `PlatformRoles.*` (4), `PlatformPermissions.Read`, `Tenants.*` (4), `Subscriptions.Read/Manage`, `Plans.*` (4), `Features.*` (4), `AddOnCatalogs.*` (3), `Promotions.*` (5) |
| **Tenant** (all others, 73) | `TenantPlans.*`, `TenantCRMLeads.*`, `Students.*`, `AttendanceLogs.*`, `Branches.*`, `AcademicStages.*`, `AcademicYears.*`, `Subjects.*`, `Teachers.*`, `TeacherSalaryConfigs.*`, `SalaryPayments.*`, `TeacherRatings.*`, `TenantAddOns.*`, `TenantLimitOverrides.*`, `TenantReferralCodes.*`, `TenantReferrals.*`, `TenantProvisioningJobs.*`, `Invoices.*`, `Payments.*`, `Refunds.*`, `Receipts.Read`, `Ledger.Read`, `TenantCredits.*`, `Invitations.*`, `Memberships.*`, `Contracts.*`, `Benefits.*`, `Installments.*`, `Offers.*` |
| **Both** | none — every permission is exactly one scope |

Structural scope integrity (proven by test
`PlatformPermission_GrantedToTenantRole_StillCannotAuthorizePlatformEndpoint`): even when a
tenant role row contains a platform-scoped permission, a tenant member cannot reach a
platform endpoint — the middleware never establishes an authorized tenant context for
platform-scoped requests, so the membership/permission fallback has no tenant to resolve
against and denies. Conversely, tenant permissions are never consulted for platform
endpoints.

## 7. Standard-Role Permission Matrix (changes marked †)

Roles are exactly `PlatformAdmin`, `TenantAdmin`, `TenantUser` — no new roles invented.

| Permission group | PlatformAdmin | TenantAdmin | TenantUser | Scope |
|---|:-:|:-:|:-:|---|
| All 109 catalog permissions | ALLOW | — | — | — |
| `TenantPlans.Read` | ALLOW | ALLOW | ALLOW | Tenant |
| `TenantCRMLeads.*` (4) | ALLOW | ALLOW | Read only | Tenant |
| `Invitations.*` (3) | ALLOW | ALLOW | DENY | Tenant |
| `Memberships.Read/Manage` | ALLOW | ALLOW | Read only | Tenant |
| `Subjects.*` (4) | ALLOW | ALLOW | Read only | Tenant |
| `Teachers.*` (4) | ALLOW | ALLOW | Read only | Tenant |
| `TeacherSalaryConfigs.*` (4) | ALLOW | ALLOW | Read only | Tenant |
| `SalaryPayments.*` (3) | ALLOW | ALLOW | Read only | Tenant |
| `TeacherRatings.Create/Read` | ALLOW | ALLOW | Read only | Tenant |
| **`Branches.*` (4)** † | ALLOW | **ALLOW (added)** | **Read (added)** | Tenant |
| **`AcademicStages.*` (3)** † | ALLOW | **ALLOW (added)** | **Read (added)** | Tenant |
| **`AcademicYears.*` (3)** † | ALLOW | **ALLOW (added)** | **Read (added)** | Tenant |
| **`Students.*` (4)** † | ALLOW | **ALLOW (added)** | **Read (added)** | Tenant |
| **`AttendanceLogs.Create/Read`** † | ALLOW | **ALLOW (added)** | **Read (added)** | Tenant |
| **`TenantAddOns.*` (3)** † | ALLOW | **ALLOW (added)** | DENY | Tenant |
| **`TenantReferralCodes.*` (2)** † | ALLOW | **ALLOW (added)** | DENY | Tenant |
| **`TenantReferrals.*` (2)** † | ALLOW | **ALLOW (added)** | DENY | Tenant |
| `Benefits.View` | ALLOW | ALLOW | DENY | Tenant |
| `Offers.*` (3) | ALLOW | ALLOW | DENY | Tenant |
| `Contracts.Read` | ALLOW | ALLOW | DENY | Tenant |
| `Installments.Read` | ALLOW | ALLOW | DENY | Tenant |
| `Invoices.Read` / `Payments.Read` | ALLOW | ALLOW (D-01) | DENY | Tenant |
| `TenantCredits.Read/Apply` | ALLOW | ALLOW (D-01) | DENY | Tenant |
| `Refunds.Create/Read` | ALLOW | ALLOW (D-01) | DENY | Tenant |
| `Invoices.Create/Update/Delete`, `Payments.Create/Complete/Allocate`, `Refunds.Approve/Execute`, `Contracts.Create`, `TenantPlans.Create/Update/Delete` | ALLOW | DENY (deliberate) | DENY | Tenant |
| `TenantCredits.Create`, `Benefits.Manage/Deliver`, `Installments.Create/Update/Cancel`, `TenantProvisioningJobs.*`, `TenantLimitOverrides.*`, `Receipts.Read`, `Ledger.Read` | ALLOW | DENY (unassigned — see §14) | DENY | Tenant |
| All platform-scoped permissions (36) | ALLOW | DENY | DENY | Platform |

Evidence for the † grants: Phase 3 HTTP tests (`Branches_TenantAdmin_CanCreateReadUpdateAndDelete`,
`AcademicStages_TenantAdmin_…`, `AcademicYears_…`, `Students_TenantAdmin_CanCreateReadUpdateSoftDelete`)
exercise these modules as TenantAdmin; `FeatureCodes.StudentManagement`/`TeacherManagement`
gate *tenant* writes (presupposing tenant actors); `docs/MODULE-INVENTORY-20260903.md`
classifies these modules "T" (tenant-scoped); `docs/TASK-16-…` matrix marks them ALLOW for
TenantAdmin. TenantUser receives only `Read` of the academic modules, consistent with its
existing read-only profile (Subjects/Teachers/…). Deliberate platform-only rows are backed
by `TenantPlansController` docs, the Phase 2 migration that strips `TenantPlans.*` mutations
from tenant roles, Task 18 catalog assertions, and `IPlatformAdminGuard` usage in the
corresponding handlers.

## 8. Endpoint Enforcement Matrix (all 34 controllers audited)

- Every controller action is protected by: `[HasPermission]` (most), `[AllowAnonymous]`
  (auth/login/refresh + invitation register), or explicit `[Authorize]`
  (`AuthController.Logout/LogoutAll`, `InvitationsController.AcceptInvitation` — both
  intentional; see middleware comments).
- Global fallback policy `RequireAuthenticatedUser` (API `DependencyInjection.cs:84-87`)
  covers anything missed — no anonymous-by-accident endpoint exists.
- No verb/permission mismatches: all mutations use Create/Update/Delete/Manage/etc.; all
  GETs use Read/View.
- No tenant endpoint uses a platform-scoped permission and vice versa.
- Known intentional cross-module bindings (documented, unchanged): `TenantsController.ApproveTenant`
  → `Subscriptions.Manage`; `TenantPlansController` list → `Subscriptions.Read`, `me` →
  `TenantPlans.Read`; `ContractsController` free-months actions → `Benefits.Manage`.
- Semantic anomaly (documented, unchanged): `InvoicesController.DeleteInvoice` is guarded by
  `Invoices.Delete` but dispatches `CancelInvoiceCommand`; both permissions are
  platform-only so the effective boundary is unchanged.

## 9. Handler-Level Security Decisions

Guarded by `IPlatformAdminGuard` (all DB-verified after T22): tenant lifecycle
(approve/reject/activate/suspend/reactivate/cancel), plan CRUD, subscription
assign/renew/renew-commercial/change-plan/activate/cancel (both cancel paths),
create-subscription-from-contract, payment allocation, refund approve/execute, promotion
create/update/activate/disable.

Handler bypass tests (new, `Task22_HandlerBypassTests`): direct invocation with a denied
guard proves (a) denial, (b) zero state mutation: no `PaymentAllocations`/ledger rows, refund
stays `Pending`, tenant stays `PendingApproval`, registry sync and audit never called.

Unguarded financial handlers (`CreateInvoiceCommand`, `IssueInvoiceCommand`,
`MarkInvoicePaidCommand`, `CreateTenantCreditCommand`, `CreatePaymentCommand`,
`CompletePaymentCommand`, installment commands, `CreateContractFromOfferCommand`) are
controller-bound to permissions that are **deliberately unassigned to tenant roles**
(platform-only in the matrix) or have documented tenant-scoped trust boundaries. This is the
existing Task 18/19/20 business decision and is preserved; see §14 for the open items.

## 10. Tenant Isolation Verification

Invariant re-verified: resolved tenant ≠ authorized tenant can never yield data access.
- `CurrentTenant.TenantId` is empty until `AuthorizeTenant()` → query filters match nothing (fail-closed).
- Middleware requires an **active** membership in the *resolved* tenant before authorizing; suspended/revoked/invited memberships deny (existing `TenantGuardMiddlewareTests`).
- Cross-tenant header: TenantAdmin A + tenant B header → 403 (`TenantAdmin_CrossTenantHeader_Denied`, both directions + TenantUser).
- Cross-tenant resource id: branch created in tenant B → GET from tenant A → 404 (`TenantAdmin_CrossTenantResourceId_NotFound`).
- PlatformAdmin is not a cross-tenant bypass: no membership → 403 on tenant-scoped endpoints (`PlatformAdmin_TenantScopedEndpoint_WithoutMembership_Denied`).
- Query filters, `CurrentTenant`, middleware and tenant interceptor were not weakened.

## 11. Tests Added (42)

- **`Task22_PlatformAdminVerifierTests`** (10): valid PlatformAdmin allowed; non-platform denied; TenantAdmin/TenantUser with forged PlatformAdmin claim denied; revoked role (stale claim) denied; locked-out denied; unknown user denied; DB role without claim denied; unauthenticated denied; identity-store failure → fail-closed deny.
- **`Task22_AuthorizationHttpTests`** (24, real pipeline, production-matrix seeding): anonymous 401 (tenant + platform); PlatformAdmin allowed on platform reads; PlatformAdmin without membership denied on tenant endpoints; forged claim denied (platform read + tenant suspend); revoked role stale-token denied; lockout denied; TenantAdmin/TenantUser denied on platform read/write; platform permission granted to a tenant role still denied; TenantAdmin read/write allowed under the fixed matrix; TenantUser read allowed / write denied; feature gate still applies with permission present; refund approve/create denials; tenant lifecycle denial; cross-tenant header denials (3); cross-tenant resource id → 404.
- **`Task22_PlatformAdminSqlServerTests`** (5, real SQL Server identity store): valid allowed; revoked denied; locked denied; forged denied; TenantAdmin denied.
- **`Task22_HandlerBypassTests`** (3): allocate-payment, refund-approve, tenant-suspend — denied with no mutation.

## 12. SQL Server Verification

Local SQL Server (`Server=.`) via the repository's `SqlServerIntegrationFactory`
(`CENTERIX_SQLTEST_CONNECTION` → local probe). Migrations applied to a throwaway database.
All 5 T22 SQL tests PASS; the full SQL category (302 tests) PASS. No InMemory substitution
was used for the SQL proof; no test was converted to a skip.

## 13. Full Regression

```
dotnet build Centerix.slnx --no-restore   → 0 errors
dotnet test  Centerix.slnx --no-build     → Total 2053 | Passed 2052 | Failed 0 | Skipped 1* | Not Executed 0
  T22 subset: Total 42 | Passed 42 | Failed 0 | Skipped 0
dotnet ef migrations has-pending-model-changes (AppDbContext + TenantDbContext) → NO
```

\* `Task18_5CreditEconomicOriginSqlServerTests.Test15_Task1851_MixedLineageProportionalTransferredOrigin`
— pre-existing skip, present in the baseline run before T22; not caused by this redesign.

## 14. Remaining Known Limitations / Reported Ambiguities

Per the task's stop rules, the following catalog permissions remain **unassigned to any
tenant role** (effectively platform-only or unreachable) because repository evidence is
insufficient or contradictory to assign a tenant role. No policy was invented:

1. `TenantLimitOverrides.*` — inventory says tenant-scoped, `ILimitService` doc says
   "platform-granted", and **no write path exists** (no command/endpoint). Needs a product decision.
2. `Receipts.Read`, `Ledger.Read` — no API surface at all (queries exist, no controller binds them).
3. `Invoices.Create/Update/Delete`, `Payments.Create/Complete` — docs (Task 16 matrix) say
   PlatformAdmin, handlers are unguarded, and platform admins hold no tenant memberships in
   practice → these endpoints are effectively unreachable today. Pre-existing gap, unchanged.
4. `Contracts.Create` — proven platform-only (Task 18 catalog test), which makes
   `POST /api/contracts/from-offer` platform-only in practice. Unchanged by design.
5. `Benefits.Manage/Deliver` — "tenant-scoped" per code comments but unassigned; the
   free-months grant/apply endpoints are therefore platform-only in practice.
6. `Installments.Create/Update/Cancel` — controller is permission-bound but no role holds
   them; installments are system-generated in the billing flows.
7. `TenantCredits.Create` — Task 21 audit calls it "tenant permission … potential BUSINESS
   DECISION"; seed contradicts the doc. Left unassigned pending that business decision.
8. `TenantProvisioningJobs.*` — tenant-scoped per inventory but semantically odd; entities
   are inert. Left unassigned.
9. The BCrypt-based `PlatformUser` staff model is disconnected from login (see §5); the
   Platform*Staff endpoints manage records that cannot authenticate. Pre-existing; flagged,
   not redesigned here.
10. Refresh-token rotation re-reads roles, so revoked PlatformAdmins additionally lose the
    claim at next refresh; between checks, the verifier already denies at request time.

None of these block the redesign: each is a pre-existing, documented business-policy
question, not a defect introduced or left unresolved by T22's changes.

## 15. Final Verdict

CLOSED. PlatformAdmin identity is now backed by the authoritative identity store in one
centralized, fail-closed service; duplicated claim-trust decisions were eliminated; the
production role matrix matches the evidence-backed intended actors and is what the tests
actually verify; endpoint and handler enforcement is complete and proven over the real
pipeline; tenant isolation remains fail-closed; SQL Server verification and the full
regression (2052/2053, only a pre-existing skip) are green; no schema migration was required.
