# TASK 22 — Authorization Boundary Correction (Platform vs Tenant Scope)

**Date:** 2026-10-05
**Reviewed commit:** `678b8d9` (`fix(auth): enforce authoritative PlatformAdmin identity re-validation`)
**Status:** Corrected — defect reproduced, fixed architecturally, and proven by test.

---

## 1. The defect (what was actually wrong)

`PermissionAuthorizationHandler` decided a `PermissionRequirement` with **no knowledge of
permission scope at all**. Its full decision order was:

1. PlatformAdmin (DB-verified) → `Succeed`
2. `HttpContext.Items["TenantPermissions"]` (pre-computed by `TenantGuardMiddleware`) → `Succeed`
3. DB fallback: `IsResolved` → `TenantMembership` → `Role` → `RolePermission` → `Succeed`

Steps 2 and 3 are **tenant-derived** sources, but they were reachable for *every* permission,
including cross-tenant platform permissions such as `Plans.Read`, `Tenants.*` and `PlatformUsers.*`.
The handler's own summary conceded the trust problem: *"anything else → DB lookup using IsResolved"*.

The scope classification (`Permissions.PlatformScope.IsPlatformScoped`) already existed, but its
**only** caller in the entire solution was `TenantGuardMiddleware.IsPlatformScopedRequest`. The
authorization layer — the component that actually grants access — never consulted it.

### 1.1 Why the pre-existing tests passed anyway

This is the most important finding, because it explains why 42 Task 22 tests were green while the
defect was live.

`TenantGuardMiddleware` *bypasses tenant authorization* for platform-scoped endpoints:

```csharp
var isPlatformScoped = IsPlatformScopedRequest(context);
if (isPlatformScoped)
{
    await next(context);   // returns BEFORE AuthorizeTenant() and before publishing permissions
    return;
}
```

So for `/api/plans` the guard never calls `AuthorizeTenant()` and never populates
`Items["TenantPermissions"]`. The handler then reached its DB fallback, where
`CurrentTenant.TenantId` is still **empty** (`TenantId` is only assigned by `AuthorizeTenant()`), so
the fallback bailed out and the request was denied.

The pre-existing P4 test therefore passed **for the wrong reason**: the denial was an accident of
guard-skip ordering, not an enforced scope boundary. A permission the handler never scoped as
platform was rejected only because an unrelated middleware short-circuited first. Any change to that
ordering, any additional platform endpoint reached without the guard's fast path, or any
`Items["TenantPermissions"]` population for a platform request would have converted the bug into a
live privilege-escalation path. The tests were validating an accident, not an invariant.

### 1.2 A second, latent trust bug

The handler gated the DB fallback on `currentTenant.IsResolved`. Finbuckle populates that from a
**client-supplied request header/host**. `IsResolved` therefore means "the client named a tenant",
not "this principal may act in that tenant". The invariant must be `IsAuthorized`, which is only set
after `TenantGuardMiddleware` has verified an **active membership** for the authenticated user.

## 2. The fix

### 2.1 One authoritative scope decision — `PermissionScopes`

New `src/Centerix.Infrastructure/Auth/PermissionScopes.cs` is the single source of truth for scope,
resolving to a total, fail-closed function:

| Input | Scope | Authorization consequence |
| --- | --- | --- |
| Listed in `Permissions.PlatformScope.PermissionCodes` | `Platform` | Only `IPlatformAdminVerifier` may allow |
| In `PermissionCatalog`, not platform-listed | `Tenant` | Authorized tenant + membership + role grant |
| Anything else (unknown/blank) | `Unknown` | **DENY** |

This is a **partition**, not an overlap: there is no "Both" scope, so no permission can be
simultaneously tenant- and platform-scoped. Deriving `Tenant` from the catalog keeps the system
extensible — a newly added catalog permission is tenant-scoped automatically — while an
unrecognised or hand-written code fails closed until deliberately classified.

`PermissionScope.Unknown` exists only to make fail-closed explicit and testable. It is not an
"ambiguous" state: `Scope_Classifier_IsTotal_NoUnknownScopeForAnyCatalogPermission` asserts that no
catalog permission ever resolves to `Unknown`, so no permission in the system is undecidable.

### 2.2 Scope enforced before any grant source — `PermissionAuthorizationHandler`

The handler now resolves scope **first**, then permits only scope-appropriate sources:

- **Platform** → `IPlatformAdminVerifier` only. If it does not allow, the request is **denied
  outright**; there is no tenant fallback path to reach, even with an injected `RolePermission`, an
  active membership, an authorized tenant context, and a pre-published `TenantPermissions` list.
- **Unknown** → denied (fail-closed, logged as a configuration fault).
- **Tenant** → requires `IsAuthorized` (not `IsResolved`), then membership → role → `RolePermission`.

### 2.3 Defense in depth — `TenantGuardMiddleware`

The guard filters its published `TenantPermissions` list through the same `PermissionScopes`
classifier, so a platform code that a tenant role somehow holds is never published as a tenant
grant. This protects every other consumer of that list (e.g. `CurrentUser.TenantPermissions`)
without creating a second allow-path. The guard's own scope detection now calls
`PermissionScopes.IsPlatformScoped`, so the two layers cannot disagree by drift.

### 2.4 `FeatureAuthorizationHandler`

Now requires `IsAuthorized` rather than `IsResolved`, matching Invariant C for feature entitlement.

## 3. Invariants enforced

- **A.** A platform permission can never be authorized through a tenant permission, tenant role,
  tenant `RolePermission`, or a pre-computed tenant permission list.
- **B.** PlatformAdmin is authoritative and DB-revalidated; the JWT role claim alone never suffices.
- **C.** Tenant permissions require an *authorized* tenant context (active membership), never a
  merely *resolved* one.
- **D.** Fail-closed: unknown permission, unknown scope, verifier exception, or a `Both`/ambiguous
  scope all deny. Unknown scope is structurally impossible for catalog permissions (asserted by test).
- **E.** A feature entitlement never implies a platform permission.
- **F.** A PlatformAdmin with no tenant membership is still denied on a tenant-scoped endpoint.

## 4. Proof that the tests genuinely catch the defect

Passing tests only mean something if they fail when the fix is removed. Both new suites were
verified by temporarily disabling the scope gate:

| Suite | With the fix | With the platform-scope gate disabled |
| --- | --- | --- |
| `Task22_PermissionScopeBoundaryTests` (18 tests) | 18 passed | **`Handler_PlatformPermission_NotPlatformAdmin_Denied_EvenWithTenantRoleGrantAndAuthorizedTenant` FAILS** |
| `Task22_PermissionScopeSqlServerTests` (11 tests) | 11 passed | **`Sql_TenantRole_HoldingBothScopes_TenantWorks_PlatformDenied` FAILS** |

The gate was restored immediately afterwards; the committed code contains no such bypass.

A third negative control covers the classification drift guard. Removing `Plans.Read` from
`Permissions.PlatformScope.PermissionCodes` — i.e. simulating a *future* platform permission that is
added to the catalog but forgotten in the platform list — makes
**`Scope_Classifier_PlatformModules_AreFullyPlatformScoped` FAIL** (verified: 2 of 6 classifier tests
failed, then the code was restored and `git diff` confirmed `Permissions.cs` is byte-identical to
`HEAD`). Without that guard the omission would silently reclassify a platform permission as
tenant-authorizable, which is the only remaining way the boundary can erode.

## 5. Test coverage

`Task22_PermissionScopeBoundaryTests` drives the **real** handler and the **real**
`PlatformAdminVerifier` through the real ASP.NET authorization service (no mocked handler, no mocked
verifier) over an EF context, deliberately corrupting the permission database:

- Platform permission denied with an injected tenant `RolePermission` **and** an authorized tenant
  context **and** a pre-published tenant permission list (the exact exploit).
- Platform permission denied for **every** code in `PermissionScope.PermissionCodes` (generic, so it
  protects future platform permissions).
- Valid PlatformAdmin still allowed (no over-blocking).
- Forged PlatformAdmin claim without a DB role → denied; verifier exception → denied.
- Tenant allow/deny: active membership allows; no membership, suspended membership, and
  resolved-but-unauthorized all deny.
- Classification totality, disjointness, case-insensitivity, and platform codes absent from the
  catalog still resolving to `Platform`.
- **Drift guard:** every catalog code in a platform-only module (`Tenants`, `Subscriptions`, `Plans`,
  `Features`, `AddOnCatalogs`, `Promotions`) must classify as `Platform`, and no platform code may
  belong to a tenant-partitioned module. This is what prevents a future, forgotten platform
  permission from becoming tenant-authorizable.

`Task22_PermissionScopeSqlServerTests` proves the mandatory cases end-to-end against real SQL
Server through the complete production pipeline
(`HttpClient → middleware → authentication → tenant resolution → authorization → controller`):

1. Platform permission granted to a tenant role via DB corruption → **403** (and with *all* platform
   codes granted to the tenant role).
2. The same corruption applied to the **TenantUser** role (the least-privileged tenant role) →
   **403**, proving the boundary does not depend on how privileged the tenant role is and cannot
   grow a "except roles below Admin" shortcut.
3. Valid PlatformAdmin → **200**.
4. Tenant permission with an active membership → **200**; no membership → **403**; suspended
   membership → **403**; **cross-tenant** access with a real second tenant → **403**.
5. Forged PlatformAdmin claim → **403**.
6. A tenant role holding both scopes: tenant endpoint **200**, platform endpoint **403**, and the
   published tenant permission list never contains platform codes.

`Task22_PlatformAdminSqlServerTests` additionally proves, over the same real SQL Server pipeline, that
PlatformAdmin authority is DB-authoritative rather than claim-authoritative: a revoked role, a locked
account, and a forged claim each yield **403** on a previously valid token, while a live DB role
yields **200**.

## 5.1 Second source review — is there any other way to reach Success?

Every `context.Succeed(...)` call site in the solution was enumerated. There are five, and none can
authorize a platform permission from a tenant-derived source:

| Site | Guard |
| --- | --- |
| `PermissionPolicyProvider.cs:107` | DB-revalidated `IPlatformAdminVerifier` only |
| `PermissionPolicyProvider.cs:167` | pre-published tenant list — reached only after `PermissionScopes.Resolve == Tenant` **and** `IsAuthorized` |
| `PermissionPolicyProvider.cs:209` | tenant DB fallback — same scope + `IsAuthorized` gate, then active-membership → role → `RolePermission` |
| `FeatureAuthorization.cs:40` | DB-revalidated `IPlatformAdminVerifier` only |
| `FeatureAuthorization.cs:64` | `FeatureRequirement` (a subscription entitlement, not a permission), gated on `IsAuthorized` |

The scope decision is made at line 97 of `PermissionPolicyProvider.cs`, *before* any grant source is
consulted, and both the `Platform` and `Unknown` branches `return` without reaching tenant
authorization. `PlatformAdminGuard` is an independent application-layer boundary that delegates to
the same verifier and has no tenant fallback. `CurrentTenant.TenantId` returns `string.Empty` until
`AuthorizeTenant()` runs, so tenant authorization is impossible before authorization. No
`IAuthorizationService.AuthorizeAsync` call site exists in `src/` that could evaluate a policy
outside the registered handlers.

**Answer: No. A tenant role can never make the authorization system return Success for a
Platform-scoped permission.**


## 6. No schema change

`dotnet ef migrations has-pending-model-changes` reports **"No changes have been made to the model
since the last migration"** for both `AppDbContext` and `TenantDbContext`. The fix is behavioral, so
no migration was added — consistent with the instruction not to create migrations for a
non-persisted change.

## 7. Note for reviewers — catalog gap (pre-existing, not introduced here)

`PlatformUsers.*`, `PlatformRoles.*` and `PlatformPermissions.Read` are enforced by controllers but
have no row in `PermissionCatalog`. Because the classifier checks the platform list *before* the
catalog, they correctly resolve to `Platform` and are denied for non-admins
(`Scope_Classifier_PlatformCodeResolvesToPlatform_EvenWhenAbsentFromCatalog` pins this). The
enforcement gap is therefore closed, though the underlying catalog omission is a pre-existing data
issue left out of this behavioral change.

## 8. Verification summary

| Check | Result |
| --- | --- |
| `dotnet build Centerix.slnx --no-restore` | 0 errors |
| Task 22 tests (HTTP + handler + SQL) | 71 passed, 0 failed |
| Full solution test suite | 2082 total / 2081 passed / 0 failed / 1 skipped |
| EF pending model changes (both contexts) | none |
| Regression detection (fix removed) | both new suites fail as expected |
| Drift-guard sensitivity (platform code removed) | guard fails as expected |

**The single skipped test is not a security test and is pre-existing:**
`Task18_5CreditEconomicOriginSqlServerTests` — *"Complex overlapping subscription scenario -
covered by Test16 and other tests"* (`[Fact(Skip = ...)]`, line 1172). It belongs to the Task 18.5
credit-economic-origin suite, is skipped by an explicit attribute in source, and was already skipped
before this correction. No Task 22 security test is skipped, hidden or conditionally excluded, and no
test was reported as not executed.

**Infrastructure:** the SQL Server suite ran against the local instance at `Server=.` (confirmed
reachable, SQL Server 16.00.1000) via the existing `CENTERIX_SQLTEST_CONNECTION` resolution order in
`SqlServerDatabaseFixture`. No Docker and no Testcontainers were used, and no InMemory or SQLite
substitution was made for the SQL proofs.

