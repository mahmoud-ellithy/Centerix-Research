# VERIFICATION REPORT — Centerix Multi-Tenant Identity & Authorization System

**Date:** 2026-08-24
**Scope:** Identity architecture, tenant membership/invitations, tenant-scoped RBAC, JWT, isolation, DB constraints, transactions, security, tests, build, migrations.
**Method:** Static code review of all relevant source files + actual build run + actual test run + EF model snapshot inspection. No code was modified.

---

## Verification Evidence Summary

| Check | Result |
|---|---|
| `dotnet build` | **Succeeded** — 0 errors, 3 warnings |
| `dotnet test` | **73/73 passed** (0 failed, 0 skipped) |
| EF model snapshot inspection (`AppDbContextModelSnapshot.cs:1678–1706`) | `TenantMembership` has ONLY `UserId`, `TenantId`, `JoinedAtUtc`, `Status` — **no `RoleName`** |
| Fallback authorization policy (`src/Centerix.API/DependencyInjection.cs:84–87`) | `RequireAuthenticatedUser` applies to ALL endpoints without explicit auth metadata |

---

## 1. Identity Architecture — **PASS**

Tenant users use ASP.NET Core Identity (`IdentityUser`) via `AppDbContext : IdentityDbContext`
(`src/Centerix.Infrastructure/Data/AppDbContext.cs:35`). Platform staff use a fully separate
`PlatformUser` entity with BCrypt hashing (`src/Centerix.Domain/Platform/Staff/PlatformUser.cs`,
`src/Centerix.Application/Platform/Staff/Commands/CreatePlatformUserCommand.cs`).

- Password policy: min 8 chars, digit + upper + lower + non-alphanumeric; lockout 10 attempts / 15 min
  (`src/Centerix.Infrastructure/DependencyInjection.cs`).
- `IdentityService` wraps `UserManager<IdentityUser>` (`src/Centerix.Infrastructure/Auth/IdentityService.cs`).
- Seed admin uses `PasswordHasher<IdentityUser>` (`ApplicationDbContextInitialiser.cs:186–187`).

Evidence: two independent user stores exist; both compile and are exercised by tests.

---

## 2. TenantMembership Architecture — **FAIL**

Design is sound; persistence is broken.

- Domain entity: composite PK `(UserId, TenantId)`, multi-tenant users supported,
  intentionally NOT `IHasTenantId` (`src/Centerix.Domain/Platform/Tenants/TenantMembership.cs:14–20`).
- Lifecycle methods `Activate/Suspend/Revoke` present.
- Cross-context FK to `TenantRegistry` via raw SQL with `ON DELETE NO ACTION`
  (`Migrations/20260818223042_AddTenantMemberships.cs:51–61`).

- **File:** `src/Centerix.Infrastructure/Data/Migrations/20260818223042_AddTenantMemberships.cs:14–33`
  and `src/Centerix.Infrastructure/Data/Migrations/AppDbContextModelSnapshot.cs:1678–1706`
- **Problem:** `RoleName` column does not exist in the migration, the Designer, or the model snapshot.
  Snapshot block contains only `UserId`, `TenantId`, `JoinedAtUtc`, `Status`. Meanwhile
  `TenantMembershipConfiguration.cs:36–40` defines `RoleName nvarchar(128) NOT NULL DEFAULT 'TenantUser'`.
  This means there are **unmigrated model changes**: on SQL Server, every SELECT/INSERT involving
  `TenantMemberships` will fail (`Invalid column name 'RoleName'`).
- **Impact:** `TenantGuardMiddleware.ResolveTenantPermissionsAsync` reads `membership.RoleName`
  (`TenantGuardMiddleware.cs:138`); permission resolution crashes. `GetMyMembershipsQuery.cs:34` also
  selects `RoleName`. Tests do not catch this because `TestWebApplicationFactory` uses the EF
  **InMemory** provider (`TestWebApplicationFactory.cs:43–47`), which ignores relational schema.
- **Fix:** Add migration `dotnet ef migrations add AddRoleNameToTenantMemberships --project src/Centerix.Infrastructure`
  and apply it. Also add an `IDesignTimeDbContextFactory<AppDbContext>` — design-time tooling currently
  fails (`Unable to resolve service for ... IMediator`), which is how this drift went unnoticed.

---

## 3. TenantInvitation Lifecycle — **PASS**

Full state machine implemented and enforced in the domain:

- `Create → Pending → Accepted | Revoked | Expired`; only `Pending` can transition
  (`src/Centerix.Domain/Platform/Tenants/TenantInvitation.cs:90–133`).
- Raw token never stored; SHA-256 hex hash persisted; unique index on `TokenHash`
  (`TenantInvitationConfiguration.cs:89–91`).
- Duplicate prevention via composite index `(TenantId, NormalizedEmail, Status)`
  (`TenantInvitationConfiguration.cs:97–98`) plus handler check (`CreateInvitationCommand.cs:71–79`).
- Expiry checked at accept-time and auto-transitioned (`AcceptInvitationCommand.cs:46–51`).

---

## 4. Existing-User Invitation — **PASS**

`src/Centerix.Application/Platform/Invitations/Commands/AcceptInvitationCommand.cs`

1. Token hash lookup; status validation per-state (36–43); expiry auto-marking (46–51).
2. User resolved by email; mismatch with authenticated caller → Forbidden (60–62).
3. Creates membership OR reactivates an inactive one (85–99) — idempotent against PK collision.
4. Marks invitation accepted atomically in one `SaveChangesAsync`.

Note: endpoint requires authentication (fallback policy). That is consistent with the handler's
design ("log in, then accept"), but the email link copy does not say so, and there is no explicit
`[Authorize]` for self-documentation.

---

## 5. New-User Registration — **FAIL**

Handler logic is correct; the HTTP layer makes it unreachable.

- Handler: validates token/status/expiry, rejects if user exists, creates IdentityUser, creates
  membership, accepts invitation, compensating delete on failure
  (`RegisterFromInvitationCommand.cs:70–112`).

- **File:** `src/Centerix.API/Controllers/InvitationsController.cs:45–53` and
  `src/Centerix.API/DependencyInjection.cs:84–87`
- **Problem:** `POST /api/invitations/register` carries NO `[AllowAnonymous]`. ASP.NET Core applies the
  configured **fallback policy** (`RequireAuthenticatedUser`) to endpoints without authorization metadata.
  A brand-new invited person has no account/JWT, so the request is rejected with **401 before reaching
  the handler**. The new-user registration flow cannot complete end-to-end over HTTP.
- **Impact:** Invited prospects who don't yet have accounts are locked out; invitation emails can only
  work for existing users. Silent failure — tests never call `/register`.
- **Fix:** Add `[AllowAnonymous]` to the `RegisterFromInvitation` action (the token itself is the
  capability/secret — SHA-256-hashed server-side, 256-bit entropy). Keep `accept` authenticated by design.

---

## 6. Tenant-Scoped Roles — **PASS**

- Roles seeded per tenant: `PlatformAdmin` (root only), `TenantAdmin`, `TenantUser`
  (`ApplicationDbContextInitialiser.cs`; `Tenancy/RoleConstants.cs`).
- Role stored per-membership as string → same user can hold different roles in different tenants.
- `PermissionAuthorizationHandler`: `PlatformAdmin` bypasses all checks
  (`PermissionPolicyProvider.cs:62–67`).
- Evidence: `TenantScopedAuthorizationTests.Test8_SameUser_DifferentRolesInDifferentTenants` passes;
  guard resolves role from membership, not from JWT global roles (`TenantGuardMiddleware.cs:115–149`).

---

## 7. Tenant-Scoped Permissions — **PASS**

- 94 permissions in canonical catalog (`Auth/PermissionCatalog.cs`).
- Resolved per-request from DB: Membership → RoleName → IdentityRole → RolePermissions → Permission codes
  (`TenantGuardMiddleware.cs:115–149`; `TenantPermissionResolver.cs`).
- Stored in `HttpContext.Items["TenantPermissions"]` (`TenantGuardMiddleware.cs:79`), consumed by
  `PermissionAuthorizationHandler` (`PermissionPolicyProvider.cs:72–79`) with DB fallback (81–135).
- JWT contains zero permission claims (`JwtTokenService.cs:55–68`) — permission changes take effect
  immediately without reissuing tokens.

---

## 8. JWT and Tenant Context — **PASS**

- Access token: NameIdentifier/Name/Email/global roles only; HMAC-SHA256; 60-min expiry
  (`JwtTokenService.cs:53–83`). Startup validation enforces ≥32-char secret (`JwtSettings.Validate()`).
- Refresh tokens: 256-bit entropy, base64url, stored hashed; rotation with **reuse detection**
  (revoked-token replay revokes entire chain — `RefreshTokenService.cs:75–80`).
- Tenant context: `ICurrentTenant.TenantId` returns empty until `AuthorizeTenant()` — fail-closed
  (`CurrentTenant.cs:22,39–43`). Resolved-vs-authorized separation documented in `ICurrentTenant.cs`.
- Pipeline order verified: MultiTenant → Authentication → TenantGuard → Authorization
  (`API/DependencyInjection.cs:139–144`).

---

## 9. Cross-Tenant Isolation — **PASS**

Defense in depth, each layer independently evidenced:

1. Global query filter `e.TenantId == _currentTenant.TenantId` on all `IHasTenantId` entities,
   evaluated live per-request (`AppDbContext.cs:115–139`).
2. `TenantInterceptor` stamps only the AUTHORIZED tenant on Added entities; nothing stamped when
   unauthorized (`Interceptors/TenantInterceptor.cs:41–47`).
3. Guard requires ACTIVE membership for the RESOLVED tenant before authorizing
   (`TenantGuardMiddleware.cs:56–70`); deactivated tenant → 403 (90–96); expired → 402 (98–110).
4. Platform scope is a conservative allow-list; everything else defaults to tenant-scoped
   (`Permissions.cs:206–235`).
5. Caching behavior skips cache when tenant unauthorized — prevents cross-tenant cache leakage
   (`Behaviours/CachingBehaviour.cs`).

Evidence: 15 HTTP-level isolation tests pass, including wrong-header POST, resource-ID probing,
multi-tenant user, suspended/deactivated/revoked cases (`C1CrossTenantIsolationTests.cs`).

---

## 10. Database Constraints — **PASS**

| Constraint | Table | Evidence |
|---|---|---|
| Composite PK `(UserId, TenantId)` | TenantMemberships | `TenantMembershipConfiguration.cs:18`; snapshot:1697 |
| FK UserId → AspNetUsers (Cascade) | TenantMemberships | config:49–52; migration:27–32 |
| FK TenantId → TenantRegistry (NO ACTION, raw SQL) | TenantMemberships | migration:51–61 |
| Unique index `TokenHash` | TenantInvitations | config:89–91 |
| Composite idx `(TenantId, NormalizedEmail, Status)` | TenantInvitations | config:97–98 |
| FKs InvitedBy/AcceptedBy/RevokedBy → AspNetUsers (Restrict) | TenantInvitations | config:101–116 |
| Unique Slug & Subdomain | Tenants | `TenantConfiguration.cs`; snapshot:1667–1673 |
| Unique Code | Permissions, PlatformRoles, PlatformUsers | respective configurations |
| Unique TokenHash | RefreshTokens | `RefreshTokenConfiguration.cs` |

Caveat: constraint correctness is verified at model level; SQL Server behavior untested end-to-end
(no Testcontainers integration despite `Testcontainers.MsSql` being declared in
`Directory.Packages.props`).

---

## 11. Transactions and Concurrency — **PARTIAL**

- **File:** `src/Centerix.Application/Platform/Invitations/Commands/RegisterFromInvitationCommand.cs:77–112`
- **Problem:** Identity-user creation (`userManager.CreateAsync` writes immediately via its own
  `UserManager` context operations) and the AppDbContext changes (membership + invitation accept) are
  NOT wrapped in one explicit transaction. Rollback is best-effort compensating delete
  (lines 98, 108). If `SaveChangesAsync` throws after user creation and the compensating delete also
  fails (e.g., transient fault), an orphaned user remains with no membership and a still-Pending invitation.
- **Impact:** Rare but real inconsistency window; orphaned accounts cannot be re-invited
  (`RegisterFromInvitation` rejects when user already exists, line 71–74).
- **Required fix:** Share one transaction across Identity and domain saves
  (`dbContext.Database.BeginTransactionAsync()` + `UseTransaction` on the Identity operations, or
  create the user through the same `AppDbContext` pipeline), with commit/rollback semantics.

Positives: `TenantRegistrySyncService` performs atomic dual-writes via shared transaction;
domain events dispatched within `SaveChangesAsync` (`AppDbContext.cs:102–106`);
24 sync unit tests pass including ordering assertions.

---

## 12. Security Vulnerabilities — **PARTIAL**

| # | Severity | Issue | Location | Impact | Fix |
|---|---|---|---|---|---|
| 1 | **Critical** | `/api/invitations/register` blocked by fallback policy — new users get 401 | `InvitationsController.cs:45–53` + `API/DependencyInjection.cs:84–87` | New-user onboarding impossible via API | Add `[AllowAnonymous]` to that action |
| 2 | **High** | Invitation email hardcodes `http://localhost:5000` | `CreateInvitationCommand.cs:118` | Accept links dead in every non-local environment | Build URL from request/config |
| 3 | **High** | `RoleName` column missing from DB schema | Migration + snapshot (see §2/§16) | Permission resolution crashes on SQL Server | Add pending migration |
| 4 | Medium | `PermissionAuthorizationHandler` swallows ALL exceptions silently | `PermissionPolicyProvider.cs:136–139` | Transient DB faults/misconfig invisible; fail-open vs fail-closed ambiguity (handler fails → deny, but reason unlogged) | Log at minimum |
| 5 | Medium | Null `ValidUpTo` ⇒ `DateTime.MinValue` ⇒ tenant treated expired | `CurrentTenant.cs:33` + middleware:98 | Tenants without expiry set are locked out with 402 — surprising operational behavior | Decide semantics; treat null as "no expiry" or validate at creation |
| 6 | Low | JWT secret empty in committed appsettings.json (fails fast at startup — OK) | `appsettings.json` | Deployment misconfig risk only | Document secret provisioning (Key Vault/User Secrets) |
| 7 | Info | `Microsoft.OpenApi 2.0.0` known vulnerability NU1903 | `Directory.Packages.props` | Dependency CVE | Upgrade |

Explicitly verified NOT vulnerable: tokens hashed-at-rest; refresh reuse detection; login rate limit
5/min sliding window (`API/DependencyInjection.cs:90–102`); lockout policy; permissions absent from
JWT; tenant header never trusted without membership check; caching fail-closed.

---

## 13. Unit Tests — **PASS**

- `C2TenantRegistrySyncTests.cs`: 24 NSubstitute-based unit tests (sync delegation, ordering, state-before-sync, error paths, lifecycle machine).
- `TenantGuardMiddlewareTests.cs`: 13 tests (bypass paths, unauthenticated pass-through, no-tenant 403, suspended/invited/revoked 403, deactivate 403, expire 402, cross-tenant 403, multi-tenant allowed).
- Run result: all green.

---

## 14. Integration Tests — **PARTIAL**

Present and passing: 15 isolation tests, 9 scoped-authorization tests, 8 invitation tests (HTTP-level
via `TestWebApplicationFactory` with in-memory providers and in-memory Finbuckle store).

Gaps (evidence: full read of `InvitationTests.cs`):

- **No test ever calls `POST /api/invitations/register`** — which is exactly why the §5 fallback-policy
  defect is invisible.
- No successful accept-flow test: Test 5 only covers invalid token (and uses an authenticated admin
  token, so the anonymous-accept path is untested too).
- No refresh-token rotation/reuse-detection test.
- No end-to-end permission-resolution test asserting a TenantUser is denied a TenantAdmin-only action
  at HTTP level with real role-permission rows.
- In-memory provider masks schema drift (§2) — relational coverage (Testcontainers.MsSql is already
  declared in `Directory.Packages.props` but unreferenced) is required before production claims.

---

## 15. Build Status — **PASS**

```
Build succeeded.
    3 Warning(s)
    0 Error(s)
```
Warnings: NU1510 (redundant Microsoft.Extensions.Options reference), NU1903 ×2 (Microsoft.OpenApi 2.0.0 CVE).

---

## 16. Migration Status — **FAIL**

- 14 AppDbContext migrations + 1 TenantDbContext migration exist; chain structure, up/down symmetry,
  raw-SQL cross-context FK, and backfill logic are sound.
- **Blocker:** current EF model ≠ last migration. Verified via snapshot: `TenantMemberships` lacks
  `RoleName` although the configuration demands it (NOT NULL). Any fresh `Database.Migrate()` produces
  a schema the running code cannot use.
- Secondary: no `IDesignTimeDbContextFactory`, so `dotnet ef` commands require workaround; the
  drift check (`has-pending-model-changes`) could not even be executed in-repo (verified during this review).

---

# FINAL VERDICTS

| # | Area | Verdict |
|---|---|---|
| 1 | Identity architecture | PASS |
| 2 | TenantMembership architecture | FAIL (missing RoleName column in schema) |
| 3 | TenantInvitation lifecycle | PASS |
| 4 | Existing-user invitation | PASS |
| 5 | New-user registration | FAIL (endpoint unreachable anonymously) |
| 6 | Tenant-scoped roles | PASS |
| 7 | Tenant-scoped permissions | PASS |
| 8 | JWT and tenant context | PASS |
| 9 | Cross-tenant isolation | PASS |
| 10 | Database constraints | PASS (model-level) |
| 11 | Transactions and concurrency | PARTIAL |
| 12 | Security vulnerabilities | PARTIAL |
| 13 | Unit tests | PASS |
| 14 | Integration tests | PARTIAL |
| 15 | Build status | PASS |
| 16 | Migration status | FAIL |

**Score: 10 PASS / 3 PARTIAL / 3 FAIL**

---

## A. What Is Production-Ready

1. Domain models and lifecycle rules for Tenant/TenantMembership/TenantInvitation (pure logic, fully validated).
2. Invitation token handling: CSPRNG generation, SHA-256-at-rest, unique index, duplicate prevention.
3. Tenant-scoped permission resolution architecture (per-request DB resolution; JWT-free permissions).
4. Fail-closed tenant context (resolved vs authorized separation) and EF query-filter isolation.
5. Refresh-token rotation with reuse detection; rate limiting; lockout policies.
6. Tenant registry dual-write synchronization (transactional, 24 unit tests).
7. Build pipeline and the existing 73-test suite.

## B. What Is Not Production-Ready

1. **The database schema** — missing `RoleName` column breaks every membership read/write on SQL Server.
2. **New-user onboarding via invitation** — `/register` returns 401 to its intended audience.
3. **Invitation emails in any deployed environment** — hardcoded localhost links.
4. **Schema-confident testing** — in-memory-only tests structurally cannot detect migration drift.

## C. Critical Issues

| # | Issue | File | Fix |
|---|---|---|---|
| C1 | `TenantMemberships.RoleName` missing from migrations/snapshot; runtime SQL will fail | `Migrations/20260818223042_AddTenantMemberships.cs`, `AppDbContextModelSnapshot.cs:1678–1706` | Add + apply migration; verify with `has-pending-model-changes` once a design-time factory exists |
| C2 | `POST /api/invitations/register` requires authentication due to fallback policy → new users blocked | `InvitationsController.cs:45–53`, `API/DependencyInjection.cs:84–87` | Add `[AllowAnonymous]` to RegisterFromInvitation action |

## D. High-Priority Issues

| # | Issue | File | Fix |
|---|---|---|---|
| H1 | Hardcoded `http://localhost:5000` invitation link | `CreateInvitationCommand.cs:118` | Derive base URL from `IHttpContextAccessor`/configuration |
| H2 | No explicit transaction spanning user creation + membership + acceptance | `RegisterFromInvitationCommand.cs:77–112` | Shared `DbContextTransaction` across Identity and domain saves |
| H3 | Integration suite blind to schema drift and to register flow | `TestWebApplicationFactory.cs`, `InvitationTests.cs` | Add Testcontainers.MsSql relational tests; add register-flow + successful-accept + unauthenticated-accept tests |

## E. Remaining Work

1. Generate and apply `AddRoleNameToTenantMemberships` migration (C1).
2. `[AllowAnonymous]` on `RegisterFromInvitation` (C2).
3. Environment-aware invitation URL (H1).
4. Transactional registration (H2).
5. Add `IDesignTimeDbContextFactory<AppDbContext>` so EF tooling runs (currently errors: unresolved `IMediator`/`ICurrentTenant` at design time — observed during this review).
6. Relational test harness via already-declared `Testcontainers.MsSql`; add tests: register-from-invitation E2E, successful accept E2E, anonymous accept rejection, refresh rotation/reuse-detection, TenantUser denied admin-only endpoint.
7. Log exceptions in `PermissionAuthorizationHandler` catch block (`PermissionPolicyProvider.cs:136–139`).
8. Define `ValidUpTo == null` semantics explicitly (treat as no-expiry or enforce presence at tenant creation).
9. Bump `Microsoft.OpenApi` past 2.0.0 (NU1903); remove redundant `Microsoft.Extensions.Options` reference (NU1510).
10. Document JWT secret provisioning requirement (env var / Key Vault) — startup fails fast today, which is correct.
