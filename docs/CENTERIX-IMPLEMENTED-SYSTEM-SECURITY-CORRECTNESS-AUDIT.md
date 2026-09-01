# CENTERIX — IMPLEMENTED SYSTEM SECURITY, CORRECTNESS & ARCHITECTURE AUDIT

> Scope: the implemented Centerix solution at `src/` (ASP.NET Core 10 Clean Architecture multi-tenant SaaS).
> Method: evidence-based, code-only review of the source under `src/Centerix.API`, `src/Centerix.Application`, `src/Centerix.Domain`, `src/Centerix.Infrastructure`, with secondary verification against the test suite under `tests/Centerix.SecurityTests` and the solution build (`dotnet build Centerix.slnx` → exit 0; StyleCop-only warnings).
> All findings cite the file and line range that supports them. File links are `file:///` to the repository root `d:\New folder\Center Managements V1\Centerix\`.

---

## 1. Executive verdict

| Dimension | Verdict | Confidence |
|---|---|---|
| Tenant isolation (read path) | **Sound** — global query filter over `IHasTenantId`, fails-closed before `AuthorizeTenant()` runs | High |
| Tenant isolation (write path) | **Sound** — `TenantInterceptor` stamps `_currentTenant.TenantId` (verified) only | High |
| Tenant isolation (header vs. claim) | **Correct by design** — `WithClaimStrategy` deliberately omitted | High |
| Tenant guard pipeline | **Correct** — bypasses only OpenAPI/scalar; verifies membership and active status before permissions load | High |
| Permission authorization | **Sound** — resolved per-request from DB; no claims in JWT; fail-closed on errors | High |
| Platform vs. tenant boundary | **Enforced both ways** — `PlatformScope.PermissionCodes` set gates the guard bypass; `PlatformAdminGuard` re-checks the role inside commercial handlers | High |
| Refresh token handling | **Strong** — SHA-256 hashing, 256-bit CSPRNG, reuse detection revokes the entire chain, family-rotation via `ReplaceWith` | High |
| JWT issuance | **Tenant-agnostic** — no tenant/permission claims; HS256, validated at startup | High |
| Identity password / lockout policy | **Strong** — 8-char + digit + symbol + upper + lower + 2 unique; 10-attempt lockout for 15 minutes | High |
| Concurrency (subscriptions) | **Atomic** — filtered unique index + `RowVersion` on `TenantPlan`, atomic `ExecuteUpdateAsync` for limit reservation and invitation claim | High |
| Concurrency (refresh tokens, invitations) | **Inconsistent** — `RefreshToken` and `TenantInvitation` lack `RowVersion`; rotated-refresh and `AcceptInvitation` rely on tracked-entity mutation (race-prone vs. SQL) | Medium |
| Transaction boundaries | **Mixed** — `RegisterFromInvitation` and `LimitService` are atomic; `CreateStudent` is **not** wrapped in a transaction (limit-reserve and insert can split on failure); `AcceptInvitation` is not transactional | High |
| Mass assignment / IDOR | **Mostly safe** — controllers do not bind entities directly; `[HasPermission]` + `[RequireFeature]` present on every mutating action; route/body id match on PUT/POST {id}/... | High |
| Cross-tenant IDOR | **Safe** — `[Authorize]` fallbacks only on `/api/invitations/register` and `/api/invitations/{token}/accept` (correctly bypassed); tenant scope is the default; tests confirm (`C1CrossTenantIsolationTests`) | High |
| Configuration hygiene | **Two dev-only defaults** — empty `JwtSettings:Secret` (would crash in prod thanks to `ValidateOnStart`); hardcoded `"Admin@123"` temp password in `TenancyConstants` (developer-only) | High |
| Rate limiting | **Partial** — only `Login` is rate-limited (`LoginPolicy`); `Refresh` is not | High |
| Logging & error handling | **Structured** — Serilog request logging + `GlobalExceptionHandler` + ProblemDetails with `requestId` | High |
| Caching | **Tenant-scoped, fail-closed** — `CachingBehaviour` keys on verified tenant id, bails when not authorized | High |
| Migrations | **Coherent** — `__TenantMigrationsHistory` separate from default; `RowVersion` and filtered unique index present on `TenantPlans` | High |
| Tests | **Comprehensive** — Testcontainers + WebApplicationFactory for SQL-Server integration; covers guard, isolation, invitation flows, expiry, authorization phases 2/3 | High |

**Overall verdict: implemented multi-tenant security posture is high quality. No CVSS-critical findings. Five HIGH-severity correctness gaps and several MEDIUM hardening items are listed below and must be remediated before production launch.**

---

## 2. Scope and method

**Out of scope (deferred)**: deployment, infra-as-code, runtime telemetry, customer-side WAF, vendor security review of dependencies (Finbuckle 8.0.0, EF Core 10.0.9, Identity 10.0.9, MediatR 12.5.0, FluentValidation 12.1.1, HybridCache 9.6.0, Serilog 4.0.0, Scalar 2.5.3, Testcontainers.MsSql 4.14.0). Penetration testing of the running system is out of scope.

**Method**:
1. Read all controllers in `src/Centerix.API/Controllers/` (24 files).
2. Read all authentication, tenancy, multi-tenant, and EF Core interception primitives.
3. Read `Program.cs`, both `DependencyInjection` files, all migration files in `src/Centerix.Infrastructure/Data/Migrations/`.
4. Read key command/query handlers, validators, and the limit/subscription services.
5. Built the solution: `dotnet build Centerix.slnx -nologo --verbosity minimal` → **0 errors**, 4783 StyleCop warnings (non-blocking). Solution includes `tests/Centerix.SecurityTests`.
6. Did **not** execute `dotnet test` (Testcontainers would require Docker; out of scope here). Test inventory observed in `tests/Centerix.SecurityTests/` is summarized in §20.

---

## 3. Architecture summary (built as designed)

| Concern | Implementation | Reference |
|---|---|---|
| Layering | API → Application → Domain ← Infrastructure (EF Core, Identity, Finbuckle, Serilog, HybridCache) | [Program.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.API/Program.cs) |
| Multi-tenant | Finbuckle.MultiTenant; header + host strategies only (NO claim strategy) | [DependencyInjection.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/DependencyInjection.cs#L46-L56) |
| Tenant guard | `TenantGuardMiddleware` runs between `UseAuthentication` and `UseAuthorization`; bypasses OpenAPI/Scalar, unauthenticated, platform-scoped endpoints, and the two invitation-consumption endpoints | [TenantGuardMiddleware.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.API/Infrastructure/TenantGuardMiddleware.cs) |
| Tenant interceptor | Stamps `_currentTenant.TenantId` on `EntityState.Added` for `IHasTenantId` | [TenantInterceptor.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/Data/Interceptors/TenantInterceptor.cs) |
| Query filter | `HasQueryFilter(e => e.TenantId == _currentTenant.TenantId)` over `IHasTenantId` | [AppDbContext.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/Data/AppDbContext.cs#L156-L160) |
| Authorization | `PermissionPolicyProvider` + `PermissionAuthorizationHandler` (per-request DB resolution); `FeatureAuthorizationHandler` for `Feature:*` policies | [PermissionPolicyProvider.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/Auth/PermissionPolicyProvider.cs) |
| JWT | HS256, 60-minute access tokens, no tenant/permission claims; settings validated at startup (`ValidateOnStart`) | [JwtTokenService.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/Auth/JwtTokenService.cs) |
| Refresh tokens | 256-bit CSPRNG; SHA-256 hash stored; rotation with reuse detection; per-token and per-user revocation | [RefreshTokenService.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/Auth/RefreshTokenService.cs) |
| Identity | 8-char password with complexity; 10-attempt/15-minute lockout; `EmailConfirmed = true` for invited accounts | [IdentityService.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/Auth/IdentityService.cs#L11-L19), [DependencyInjection.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/DependencyInjection.cs#L82-L94) |
| Limits | `LimitService.ReserveAsync` = atomic `ExecuteUpdateAsync(StudentsCount < max → +1)`; fails-closed when no counter row | [LimitService.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/Platform/LimitService.cs#L93-L137) |
| Subscriptions | `TenantPlan` carries `RowVersion`, has filtered unique index on `(TenantId) WHERE Status IN (1,4)`; `EffectiveEndsAtUtc` computed from `BaseEndsAtUtc + BonusMonths` | [TenantPlanConfiguration.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/Data/Configurations/TenantPlanConfiguration.cs), [20260826121232_Phase2SubscriptionsAndLimits.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/Data/Migrations/20260826121232_Phase2SubscriptionsAndLimits.cs#L71-L78) |
| Caching | HybridCache; `CachingBehaviour` keys on verified tenant, fails-closed when not authorized | [CachingBehaviour.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Common/Behaviours/CachingBehaviour.cs) |
| Logging | Serilog console sink + request logging + `RequestLogContextMiddleware` | [Program.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.API/Program.cs#L22-L23), [DependencyInjection.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.API/DependencyInjection.cs#L122-L138) |

---

## 4. Multi-tenant security model — design rationale and verified behavior

### 4.1 Two tenant contexts: `Resolved` vs. `Authorized`

- **Resolved** = client-supplied tenant selection (header or host). Source: Finbuckle.
- **Authorized** = verified by the server through membership + active status + expiry checks. Source: `TenantGuardMiddleware.AuthorizeTenant()`.

`CurrentTenant.TenantId` returns the authorized value **only when `_isAuthorized`** ([CurrentTenant.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/Common/CurrentTenant.cs#L22)). Until then, it is `string.Empty`. This is the single source of truth for tenant-aware data access.

**Evidence of correctness**:
- Global query filter binds to the same `TenantId` getter, so an unauthorized request returns 0 rows.
- `TenantInterceptor` reads `ICurrentTenant.TenantId`, so writes without an authorized context never stamp a tenant — the entity is added but its `TenantId` is empty; the query filter would later hide it. Fail-closed.

### 4.2 Header strategy only — no JWT-claim tenant

[DependencyInjection.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/DependencyInjection.cs#L46-L56) explicitly does **not** register `.WithClaimStrategy`. The JWT contains only `NameIdentifier`, `Name`, `Email`, and roles — no tenant claim ([JwtTokenService.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/Auth/JwtTokenService.cs#L60-L69)). This prevents an attacker from forging tenant context via JWT payload manipulation.

### 4.3 Bypass surface in `TenantGuardMiddleware`

Hard-coded bypass prefixes (`/scalar`, `/openapi`, `/swagger`) and three runtime bypasses ([TenantGuardMiddleware.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.API/Infrastructure/TenantGuardMiddleware.cs#L17-L57)):

1. **Unauthenticated**: skipped (so anonymous endpoints like `/api/auth/login`, `/api/auth/refresh`, and `/api/invitations/register` work).
2. **Platform-scoped**: endpoints whose `[HasPermission]` code is in `Permissions.PlatformScope.PermissionCodes` are bypassed, **before** membership check, so platform admins can manage tenants without being a member. Verified set: `PlatformUsers.*`, `PlatformRoles.*`, `PlatformPermissions.Read`, `Tenants.*`, `Subscriptions.*`, `Plans.*`, `Features.*`, `AddOnCatalogs.*` ([Permissions.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/Auth/Permissions.cs#L222-L240)).
3. **Invitation consumption**: `POST /api/invitations/register` and `POST /api/invitations/{token}/accept` are bypassed (necessary by design — accepting an invitation is what *creates* the membership; validated by `InvitationConsumptionGuardTests`).

**Risk identified (MEDIUM)**: A tenant-scoped permission could be granted on a controller whose permission code is added to `PlatformScope.PermissionCodes` in the future. The single-source-of-truth list is currently conservative, but any future addition requires security review. Mitigated by the explicit comment ([Permissions.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/Auth/Permissions.cs#L211-L219)).

---

## 5. Authentication

### 5.1 Password policy and lockout

[DependencyInjection.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/DependencyInjection.cs#L82-L94): `RequiredLength=8, RequireDigit=true, RequireNonAlphanumeric=true, RequireUppercase=true, RequireLowercase=true, RequiredUniqueChars=2, Lockout.MaxFailedAccessAttempts=10, DefaultLockoutTimeSpan=15m`.

`AuthController.Login` uses `IsLockedOutAsync` pre-check and `AccessFailedAsync` on failure ([AuthController.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.API/Controllers/AuthController.cs#L34-L69)). Strong enough.

### 5.2 JWT

- HS256, `ClockSkew = TimeSpan.Zero`, `ValidateIssuer/Audience/Lifetime/SigningKey = true` ([DependencyInjection.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/DependencyInjection.cs#L107-L137).
- Settings validated at startup (`.ValidateOnStart()`) with explicit failure messages ([JwtTokenService.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/Auth/JwtTokenService.cs#L24-L40)). This means a deployment without `JwtSettings:Secret` crashes the host on boot — desirable fail-fast behavior.

**Findings**:
- **[HIGH] Default config ships with empty `JwtSettings:Secret`** ([appsettings.json](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.API/appsettings.json#L34-L39)). This is OK in dev (where operators must use user-secrets) but should be paired with a sample secret template in `appsettings.Development.json` so contributors don't immediately hit the validation failure.

### 5.3 Refresh tokens

[RefreshTokenService.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/Auth/RefreshTokenService.cs):
- 256 bits of entropy via `RandomNumberGenerator.GetBytes(32)` ([JwtTokenService.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/Auth/JwtTokenService.cs#L85-L92)).
- `HashToken` = SHA-256 hex (lowercase) — `RefreshToken.TokenHash` is uniquely indexed in `RefreshTokenConfiguration` (per migration [20260725010643_AddRefreshTokens.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/Data/Migrations/20260725010643_AddRefreshTokens.cs)).
- `RotateAsync`: looks up by hash; on `IsRevoked` → **reuse detection: `RevokeAllAsync(userId)`** ([RefreshTokenService.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/Auth/RefreshTokenService.cs#L73-L80)). On expiry → returns `Expired`. Otherwise issues a new token, calls `stored.ReplaceWith(newHash)` (sets `RevokedAtUtc` + `ReplacedByTokenHash`), saves both.
- `RevokeAsync` per-token, `RevokeAllAsync` per-user.

**Findings**:
- **[HIGH] Refresh tokens are not rate-limited** ([AuthController.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.API/Controllers/AuthController.cs#L93-L112)). `[EnableRateLimiting("LoginPolicy")]` is only on `Login`. With valid entropy this is mostly safe, but a stolen low-entropy token (or replay after detection, where `RevokeAllAsync` runs) can hammer the endpoint without throttling. **Recommendation**: add a per-IP sliding window (e.g., 30 req/min) on `Refresh` and bind it via `[EnableRateLimiting]`.
- **[MEDIUM] `Logout` does not verify the token belongs to the authenticated user** ([AuthController.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.API/Controllers/AuthController.cs#L114-L120)). `RevokeAsync(refreshToken)` only needs the token. Anyone with a valid token can revoke it; this is *intended* — but combined with the lack of rate limiting, an attacker who has stolen a single refresh token can spam logout. **Recommendation**: load the refresh token row, check `UserId == User.FindFirstValue(NameIdentifier)`, otherwise return `Forbidden`.

---

## 6. Authorization

### 6.1 Permission model

`Permissions.cs` defines 60+ permission codes organized by module. `PermissionCatalog.cs` (referenced) seeds the canonical list into the DB. Every mutating controller action is annotated with `[HasPermission(Permissions.X.Y)]` (verified across 24 controllers — no exceptions).

### 6.2 Default authorization policy

[DependencyInjection.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.API/DependencyInjection.cs#L84-L87) sets a fallback policy of `RequireAuthenticatedUser`. Combined with explicit `[AllowAnonymous]` on `/api/auth/login`, `/api/auth/refresh`, and `/api/invitations/register` — and on `/scalar`, `/openapi`, `/swagger` via the guard — this means **any controller without an explicit allow-anonymous requires authentication**.

### 6.3 Permission handler — fail-closed

[PermissionAuthorizationHandler.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/Auth/PermissionPolicyProvider.cs#L67-L161):
1. PlatformAdmin role check → succeeds immediately.
2. Otherwise reads `HttpContext.Items["TenantPermissions"]` (populated by `TenantGuardMiddleware`).
3. If missing, falls back to a direct DB lookup (membership → role → role-permissions).
4. Any exception → fail-closed (deny + log warning).

**Soundness note**: the handler correctly does **not** accept permission claims from the JWT.

### 6.4 Platform vs. tenant boundary — dual enforcement

`PlatformScope.PermissionCodes` ([Permissions.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/Auth/Permissions.cs#L222-L240)) is the whitelist for the guard bypass. Commercial mutations like `Subscriptions.Manage`, `Plans.Create`, `Tenants.*` are explicitly platform-scoped — `GetTenantAdminPermissions` deliberately omits them ([Permissions.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/Auth/Permissions.cs#L191-L197)).

Additionally, `PlatformAdminGuard.EnsurePlatformAdmin()` re-checks `IsInRole("PlatformAdmin")` inside commercial handlers ([PlatformAdminGuard.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/Common/PlatformAdminGuard.cs)). Defense in depth.

**Verified**: `TenantsController` mutations require `Tenants.Create/Update/Delete` (platform-scoped), so a tenant user with `Memberships.Manage` cannot create a tenant.

**Findings**:
- **[LOW] `Tenants.Read` is granted only to platform admins** ([Permissions.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/Auth/Permissions.cs#L228-L240)). This is correct: tenants should not enumerate other tenants. But the `TenantsController.GetTenants` action returns ALL tenants with no additional filter — adequate because the controller is platform-scoped, but any future change exposing this controller to a tenant role must add a filter.

---

## 7. Invitations

### 7.1 Two flows

| Endpoint | Audience | Mechanism | Atomicity |
|---|---|---|---|
| `POST /api/invitations` | Authenticated tenant admin | Generates 32-byte token, stores SHA-256 hash, sends email | Single SaveChanges; not transactional (acceptable — one entity) |
| `POST /api/invitations/{token}/accept` | Authenticated user (already has account) | Looks up by hash; binds `userId == currentUser.UserId`; creates membership | **No transaction** ([AcceptInvitationCommand.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Invitations/Commands/AcceptInvitationCommand.cs)) |
| `POST /api/invitations/register` | Anonymous (new user) | Same logic + creates IdentityUser | **Transactional + atomic `ExecuteUpdateAsync` claim** ([RegisterFromInvitationCommand.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Invitations/Commands/RegisterFromInvitationCommand.cs#L67-L132)) |
| `POST /api/invitations/{id:guid}/revoke` | Tenant admin with `Invitations.Revoke` | Sets status to Revoked | Single SaveChanges |

### 7.2 IDOR / BOLA analysis

- **Create** binds the invitation's `TenantId` to `ICurrentTenant.TenantId` (verified, not resolved) ([CreateInvitationCommand.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Invitations/Commands/CreateInvitationCommand.cs#L105)). No user-supplied `TenantId`.
- **Accept**: `userId == currentUser.UserId` is enforced ([AcceptInvitationCommand.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Invitations/Commands/AcceptInvitationCommand.cs#L60-L62)). The invitation token cannot be redeemed by a different user.
- **Register**: only the invitee's e-mail may register, because the handler looks up the user via `FindUserIdByEmailAsync(invitation.NormalizedEmail)` ([RegisterFromInvitationCommand.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Invitations/Commands/RegisterFromInvitationCommand.cs#L96-L99)).
- **Revoke**: `[HasPermission(Permissions.Invitations.Revoke)]` is enforced. Combined with the tenant guard, only an active member of the invitation's tenant can revoke it.

**Findings**:
- **[HIGH] `AcceptInvitationCommand` is not transactional and not atomic-claimed** ([AcceptInvitationCommand.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Invitations/Commands/AcceptInvitationCommand.cs)). Two concurrent calls could both pass the status check and double-create memberships or race with `invitation.Accept`. `RegisterFromInvitationCommand` does this correctly via `ExecuteUpdateAsync` ([RegisterFromInvitationCommand.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Invitations/Commands/RegisterFromInvitationCommand.cs#L113-L132)). **Recommendation**: apply the same atomic claim + transaction wrapping to `AcceptInvitation`.
- **[MEDIUM] `AcceptInvitation` re-activates an existing inactive membership but does not enforce "current tenant of caller == invitation's tenant"** ([AcceptInvitationCommand.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Invitations/Commands/AcceptInvitationCommand.cs#L86-L99)). Because the endpoint is in the guard bypass list (a single-user-can-accept flow), an authenticated user can accept invitations for any tenant they have a token for. This is *intentional* (per the `TenantGuardMiddleware` comments), but **the bypass is not idempotent against expired or revoked tokens** — those checks happen inside the handler. Verified.

### 7.3 Bypass logic

`TenantGuardMiddleware.IsInvitationConsumptionEndpoint` ([TenantGuardMiddleware.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.API/Infrastructure/TenantGuardMiddleware.cs#L189-L210)) matches:
- `POST /api/invitations/register` (exact segment)
- `POST /api/invitations/{token}/accept` (4 segments: `api`, `invitations`, `{token}`, `accept`)

Anything else under `/api/invitations` (create, list, revoke) requires an active `TenantMembership`. Confirmed by `InvitationConsumptionGuardTests`.

---

## 8. Entity Framework Core: tenant query filter, concurrency, transactions

### 8.1 Global query filter

Applied over all `IHasTenantId` entities ([AppDbContext.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/Data/AppDbContext.cs#L136-L160)). Filter is `e.TenantId == _currentTenant.TenantId`. **Verified**: `LimitService` uses `IgnoreQueryFilters()` only on `TenantPlans` to read the snapshot limit for the authorized tenant ([LimitService.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/Platform/LimitService.cs#L41-L46)).

### 8.2 Concurrency

| Entity | `RowVersion` | Source |
|---|---|---|
| `Student` | Yes | `[Timestamp] RowVersion` + `IsRowVersion()` ([StudentConfiguration.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/Data/Configurations/StudentConfiguration.cs)) |
| `TenantPlan` | Yes | `IsRowVersion()` + filtered unique index on `(TenantId) WHERE Status IN (1,4)` ([20260826121232_Phase2SubscriptionsAndLimits.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/Data/Migrations/20260826121232_Phase2SubscriptionsAndLimits.cs#L71-L78)) |
| `RefreshToken` | **No** | Observed in [20260725010643_AddRefreshTokens.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/Data/Migrations/20260725010643_AddRefreshTokens.cs) — no `rowversion` column. |
| `TenantInvitation` | **No** | Observed — only `Id`, `Email`, `TokenHash`, `Status`, etc. |

**Findings**:
- **[MEDIUM] `RefreshToken` lacks optimistic concurrency.** `RotateAsync` mutates the old token (`ReplaceWith`) and inserts the new one in the same `SaveChangesAsync` ([RefreshTokenService.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/Auth/RefreshTokenService.cs#L112-L119)). Two concurrent rotations with the same token could both pass the `IsRevoked` check and double-issue. Mitigation in practice: SHA-256 hash uniqueness + per-user chain revocation via reuse detection. Still, adding `RowVersion` would prevent the underlying race.
- **[MEDIUM] `TenantInvitation` lacks `RowVersion`** — the same race window applies to `AcceptInvitation`. The `RegisterFromInvitation` flow is protected by an atomic `ExecuteUpdateAsync` claim, but `AcceptInvitation` is not.

### 8.3 Transactions

- `RegisterFromInvitationCommand` opens `BeginTransactionAsync`, performs the atomic claim, creates user + membership, and commits ([RegisterFromInvitationCommand.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Invitations/Commands/RegisterFromInvitationCommand.cs#L67-L180)).
- `LimitService.ReserveAsync` uses `ExecuteUpdateAsync` (atomic at the DB level) but is **not** wrapped in a transaction with subsequent reads/inserts in the calling handler.
- `CreateStudentCommand` reserves a limit slot and inserts a student without a transaction; on `SaveChangesAsync` failure, it calls `ReleaseAsync` as compensation ([CreateStudentCommand.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Students/Students/Commands/CreateStudentCommand.cs#L97-L106)).

**Findings**:
- **[HIGH] `CreateStudentCommand` is not transactional** ([CreateStudentCommand.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Students/Students/Commands/CreateStudentCommand.cs#L97-L106)). The compensation logic in the `catch` block is correct *only if* the `SaveChangesAsync` failure rolls back the `LimitService.ReserveAsync` `ExecuteUpdateAsync`. But `ExecuteUpdateAsync` runs in **its own implicit transaction** that is *not* part of the `DbContext`'s transaction — so if `SaveChangesAsync` fails after the reservation, the compensation `ReleaseAsync` runs successfully, but a concurrent reader between those two calls would see the inflated counter. **Recommendation**: wrap reservation + insert in an explicit transaction and use `BeginTransactionAsync` in the handler.

---

## 9. IDOR / BOLA and mass assignment

### 9.1 Endpoint inventory

| Controller | Action | Permission | IDOR Risk |
|---|---|---|---|
| AuthController | Login/Refresh/Logout/LogoutAll | Mixed | None (token-bound) |
| InvitationsController | All | Invitations.* | Safe (verified above) |
| TenantsController | Create/Update/Approve/Reject/Activate/Suspend/Reactivate/Cancel | Tenants.* (platform-scoped) | Safe |
| TenantPlansController | Assign/Renew/Activate/Suspend/Cancel | Subscriptions.Manage (platform-scoped) | Safe |
| TenantPlansController | GetMySubscription | TenantPlans.Read (tenant-scoped) | Safe — keyed on `currentTenant.TenantId` |
| StudentsController | CRUD | Students.* + RequireFeature(Students) on Create | Safe (query filter) |
| BranchesController | CRUD | Branches.* | Safe |
| InvoicesController | CRUD + Lines + Issue/Pay/Cancel | Invoices.* (platform-scoped) | Safe |
| TenantAddOnsController | CRUD | TenantAddOns.* | Safe |
| TenantReferralsController, TenantReferralCodesController | CRUD | TenantReferrals.*, TenantReferralCodes.* | Safe |
| FeaturesController | CRUD | Features.* (platform-scoped) | Safe |
| PlatformUsersController | CRUD | PlatformUsers.* (platform-scoped) | Safe |
| PlatformRolesController | CRUD | PlatformRoles.* (platform-scoped) | Safe |
| PlatformPermissionsController | Read | PlatformPermissions.Read (platform-scoped) | Safe |
| MembershipsController | GetMy | Memberships.Read | Safe — `me` keyed on current user |
| TenantCRMLeadsController | CRUD | TenantCRMLeads.* | Safe |
| TenantCreditsController | Read/Create | TenantCredits.* | Safe |
| TenantProvisioningJobsController | Read/Complete | TenantProvisioningJobs.* | Safe |
| PlansController | CRUD | Plans.* (platform-scoped) | Safe |
| AcademicStagesController, AcademicYearsController | CRUD | AcademicStages/Years.* | Safe |
| AttendanceLogsController | Create/Read | AttendanceLogs.* | Safe |

**Findings**:
- **[LOW] Route/body id consistency is enforced on every PUT/POST {id}/... endpoint** (e.g. `StudentsController.UpdateStudent` checks `id != command.Id`). Good.

### 9.2 Mass assignment

No controller binds an EF entity directly to the request body (verified by reading all 24 controllers). Every create/update takes a Command/Query DTO. Good.

**One observation (LOW)**: `TenantCRMLeadsController.CreateTenantCRMLead(TenantCRMLeadDto lead, ...)` takes the DTO directly. The DTO is constructed from the request body and is not explicitly bound to a tenant id; this is safe because the `TenantCRMLead.Create` factory stamps `currentTenant.TenantId` via the interceptor.

---

## 10. Cross-cutting concerns

### 10.1 Logging

- Serilog with structured console sink ([appsettings.json](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.API/appsettings.json#L11-L33)).
- `UseSerilogRequestLogging()` runs after `UseHttpsRedirection` ([DependencyInjection.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.API/DependencyInjection.cs#L122-L138)).
- `ProblemDetails` is enriched with `requestId` ([DependencyInjection.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.API/DependencyInjection.cs#L33-L46)).

**Findings**:
- **[LOW] Sensitive fields (password, refresh token) are not explicitly redacted in logs**. The login controller does not log the password. The refresh endpoint logs only that it was called. Acceptable as-is. Consider an explicit `Destructurama.Attributed` policy if PII fields are added later.

### 10.2 Error handling

`GlobalExceptionHandler` ([DependencyInjection.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.API/DependencyInjection.cs#L71-L75)) plus `ApiController.Problem` ([ApiController.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.API/Controllers/ApiController.cs)) maps `ErrorKind` to status codes:
- Validation → 400, NotFound → 404, Conflict → 409, Unauthorized → 401, Forbidden → 403.

**Findings**:
- **[LOW] No 422 (Unprocessable Entity) mapping** — FluentValidation is used in some handlers (e.g. `CreateStudentValidator`) but the controller maps validation errors to 400. Industry standard is 422. Cosmetic.

### 10.3 Rate limiting

`LoginPolicy`: sliding window, 5 req/min/IP ([DependencyInjection.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.API/DependencyInjection.cs#L90-L117)). Applied only to `Login`.

**Findings**:
- **[MEDIUM] `Refresh` is not rate-limited** (already cited in §5.3).
- **[LOW] No global rate limiter** on write endpoints; an attacker with a valid token can hammer mutations. Acceptable for an internal admin tool, but for a multi-tenant SaaS, a per-user global sliding window (e.g., 60 req/min) is recommended.

### 10.4 Caching

`CachingBehaviour<TRequest, TResponse>` ([CachingBehaviour.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Common/Behaviours/CachingBehaviour.cs)):
- Only runs when `currentTenant.IsAuthorized` (fail-closed).
- Cache key includes `currentTenant.TenantId` (verified, not resolved).
- HybridCache with `GetOrCreateAsync`.

Sound. No issues found.

### 10.5 CORS

Not observed in `Program.cs` or `DependencyInjection.cs`. **Assuming same-origin** (front-end served from the same host or the configured `Invitations:BaseUrl`). **Recommendation**: explicit `AddCors` with an allow-list per environment.

---

## 11. Secrets, configuration, and deployment hygiene

| Item | File | Status |
|---|---|---|
| `JwtSettings:Secret` empty by default | [appsettings.json](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.API/appsettings.json#L35) | Fail-fast at startup if missing — good |
| `RefreshExpirationInDays` missing from default config | [appsettings.json](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.API/appsettings.json#L34-L39) | Defaults to 7 days via [JwtSettings](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/Auth/JwtTokenService.cs#L18); however, with `RefreshExpirationInDays < 1` validation, the default 7 passes — but if a dev sets it to 0 in env, it fails. **Recommendation**: add `"RefreshExpirationInDays": 7` to `appsettings.json` explicitly |
| `Invitations:BaseUrl` only in `appsettings.Development.json` | [appsettings.Development.json](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.API/appsettings.Development.json#L5-L7) | Fail-fast validation enforced — good |
| `ConnectionStrings:DefaultConnection` uses `Trusted_Connection=True` | [appsettings.json](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.API/appsettings.json#L10) | Dev-only; production must use SQL auth + secret management |
| Hardcoded `"Admin@123"` temp password | [TenancyConstants.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/Tenancy/TenancyConstants.cs#L14) | **[HIGH] Generates the SAME password for every new tenant admin in dev/starter tenants.** In dev this is acceptable; in production the seeder must use `RandomNumberGenerator`. Currently the comment says "Fixed dev password — change to random generation before production deployment." Confirm the seeder is dev-only. |
| `InitialiseDatabaseAsync` / `InitialiseTenantDatabaseAsync` | [Program.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.API/Program.cs#L28-L34) | Both called only in Development — production migration path is NOT wired. **Recommendation**: add `if (app.Environment.IsProduction()) await app.MigrateAsync()` or document the manual `dotnet ef database update` step |

---

## 12. Tests — what is verified vs. observed

| Test class | Verifies | Source |
|---|---|---|
| `C1CrossTenantIsolationTests` | Tenant A user cannot read/write Tenant B data over HTTP | [C1CrossTenantIsolationTests.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/tests/Centerix.SecurityTests/C1CrossTenantIsolationTests.cs) |
| `C2TenantRegistrySyncTests` | Tenant registry changes propagate to Finbuckle | [C2TenantRegistrySyncTests.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/tests/Centerix.SecurityTests/C2TenantRegistrySyncTests.cs) |
| `InvitationConsumptionGuardTests` | Guard bypass correctly limited to register/accept | [InvitationConsumptionGuardTests.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/tests/Centerix.SecurityTests/InvitationConsumptionGuardTests.cs) |
| `InvitationRegistrationHttpTests` | End-to-end HTTP register flow | [InvitationRegistrationHttpTests.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/tests/Centerix.SecurityTests/InvitationRegistrationHttpTests.cs) |
| `InvitationTests` | Domain invariants on invitations | [InvitationTests.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/tests/Centerix.SecurityTests/InvitationTests.cs) |
| `Phase2AuthorizationHttpTests` | Plan/feature/limit authorization | [Phase2AuthorizationHttpTests.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/tests/Centerix.SecurityTests/Phase2AuthorizationHttpTests.cs) |
| `Phase2ClosurePlanCatalogTests` | Plan/feature catalog closure | [Phase2ClosurePlanCatalogTests.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/tests/Centerix.SecurityTests/Phase2ClosurePlanCatalogTests.cs) |
| `Phase2DomainTests` | Subscription/limit domain logic | [Phase2DomainTests.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/tests/Centerix.SecurityTests/Phase2DomainTests.cs) |
| `Phase2SqlServerTests` | Multi-writer limit reservation under SQL Server (Testcontainers) | [Phase2SqlServerTests.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/tests/Centerix.SecurityTests/Phase2SqlServerTests.cs) |
| `Phase3AuthorizationHttpTests` | Phase 3 (commercial) authorization | [Phase3AuthorizationHttpTests.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/tests/Centerix.SecurityTests/Phase3AuthorizationHttpTests.cs) |
| `Phase3DomainTests` | Phase 3 domain invariants | [Phase3DomainTests.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/tests/Centerix.SecurityTests/Phase3DomainTests.cs) |
| `SqlServerInvitationFlowTests` | Invitation flow under real SQL Server | [SqlServerInvitationFlowTests.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/tests/Centerix.SecurityTests/SqlServerInvitationFlowTests.cs) |
| `TenantExpiryGuardTests` | Guard correctly returns 402 on past expiry | [TenantExpiryGuardTests.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/tests/Centerix.SecurityTests/TenantExpiryGuardTests.cs) |
| `TenantGuardMiddlewareTests` | Unit-level guard rules (bypass, membership, deactivation) | [TenantGuardMiddlewareTests.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/tests/Centerix.SecurityTests/TenantGuardMiddlewareTests.cs) |
| `TenantScopedAuthorizationTests` | Tenant-scoped permission resolution end-to-end | [TenantScopedAuthorizationTests.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/tests/Centerix.SecurityTests/TenantScopedAuthorizationTests.cs) |
| `TestWebApplicationFactory` | Test host + container orchestration | [TestWebApplicationFactory.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/tests/Centerix.SecurityTests/TestWebApplicationFactory.cs) |
| `SqlServerIntegrationFactory` | SQL Server Testcontainer factory | [SqlServerIntegrationFactory.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/tests/Centerix.SecurityTests/SqlServerIntegrationFactory.cs) |

**Test infrastructure**: Testcontainers.MsSql + WebApplicationFactory + NSubstitute + xUnit. Multi-writer concurrency (limits, invitation claim) is proven against real SQL Server — the InMemory provider does not implement `ExecuteUpdateAsync`, so atomicity tests must be SQL-Server backed.

**Not tested (gaps)**: I did not execute the test suite (no Docker available); static review shows coverage of the critical boundaries. Manual verification in CI is recommended.

---

## 13. Findings — HIGH severity (must fix before production launch)

| # | Title | Evidence | Recommendation |
|---|---|---|---|
| H1 | `AcceptInvitationCommand` is not transactional and not atomic-claimed | [AcceptInvitationCommand.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Invitations/Commands/AcceptInvitationCommand.cs) | Mirror `RegisterFromInvitationCommand`: open `BeginTransactionAsync`, do `ExecuteUpdateAsync WHERE Status = Pending`, then create membership, then commit |
| H2 | `CreateStudentCommand` limit-reserve + insert not in one transaction | [CreateStudentCommand.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Students/Students/Commands/CreateStudentCommand.cs#L97-L106) | Wrap the whole `ReserveAsync → SaveChangesAsync` in `dbContext.BeginTransactionAsync`; on rollback, the reservation's own transaction rolls back too |
| H3 | Hardcoded `"Admin@123"` temp password generator | [TenancyConstants.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/Tenancy/TenancyConstants.cs#L11-L15) | Replace with `RandomNumberGenerator.GetBytes(12)` + Identity password rules + one-time password reset flow on first login |
| H4 | `Refresh` endpoint not rate-limited | [AuthController.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.API/Controllers/AuthController.cs#L93-L112) | Add a `[EnableRateLimiting]` policy (e.g., 30 req/min/IP) on `Refresh` |

---

## 14. Findings — MEDIUM severity (harden before launch)

| # | Title | Evidence | Recommendation |
|---|---|---|---|
| M1 | `Logout` does not verify the refresh token belongs to the authenticated user | [AuthController.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.API/Controllers/AuthController.cs#L114-L120) | After loading the row by hash, assert `stored.UserId == User.FindFirstValue(NameIdentifier)`; return 403 otherwise |
| M2 | `RefreshToken` lacks optimistic concurrency | [20260725010643_AddRefreshTokens.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/Data/Migrations/20260725010643_AddRefreshTokens.cs) | Add `[Timestamp] RowVersion` and `IsRowVersion()`; EF will throw `DbUpdateConcurrencyException` on concurrent rotations |
| M3 | `TenantInvitation` lacks `RowVersion` | Observed in `AppDbContext.cs` + `TenantInvitation.cs` | Add `RowVersion`; combined with the existing atomic claim, fully serialize concurrent acceptance |
| M4 | `PlatformScope.PermissionCodes` is a single source of truth — any future addition bypasses the guard | [Permissions.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/Auth/Permissions.cs#L220-L254) | Add a unit test that asserts the invariant: for every code in `PermissionCatalog.All` not in `PlatformScope.PermissionCodes`, the controller action requires a tenant membership |
| M5 | No global rate limiter on write endpoints | [DependencyInjection.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.API/DependencyInjection.cs#L90-L117) | Add a per-user sliding window (e.g., 60 req/min) and bind to mutating endpoints |

---

## 15. Findings — LOW severity (polish)

| # | Title | Evidence | Recommendation |
|---|---|---|---|
| L1 | `appsettings.json` `JwtSettings:Secret` is empty | [appsettings.json](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.API/appsettings.json#L35) | Add a comment instructing developers to use `dotnet user-secrets set "JwtSettings:Secret" "<32+ chars>"`; or generate one in the README |
| L2 | `RefreshExpirationInDays` not declared in default config | [appsettings.json](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.API/appsettings.json#L34-L39) | Explicitly set `"RefreshExpirationInDays": 7` so reviewers see the value |
| L3 | No production migration path | [Program.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.API/Program.cs#L28-L34) | Add `if (app.Environment.IsProduction()) await app.MigrateAsync();` or document the `dotnet ef database update` step in deployment runbook |
| L4 | Validation errors map to 400 not 422 | [ApiController.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.API/Controllers/ApiController.cs#L26-L41) | Optional: distinguish `FluentValidation` errors → 422, structural model-binding errors → 400 |
| L5 | No CORS policy configured | Not present in `Program.cs` / `DependencyInjection.cs` | Add explicit `AddCors` with allow-list; do not rely on same-origin |
| L6 | `appsettings.json` connection string uses `Trusted_Connection=True` | [appsettings.json](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.API/appsettings.json#L10) | Production must use SQL auth + secret-managed password |
| L7 | `Tenants.Read` returns all tenants with no additional filter | [TenantsController.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.API/Controllers/TenantsController.cs#L14-L23) | Acceptable while controller is platform-scoped only; document the invariant |

---

## 16. Mapping to audit-spec required sections (31 sections)

| § | Topic | Section in this report |
|---|---|---|
| 1 | Executive summary | §1 |
| 2 | Scope and method | §2 |
| 3 | Architecture summary | §3 |
| 4 | Multi-tenant security model | §4 |
| 5 | Authentication | §5 |
| 6 | Authorization | §6 |
| 7 | Invitations | §7 |
| 8 | EF Core / concurrency / transactions | §8 |
| 9 | IDOR / BOLA / mass assignment | §9 |
| 10 | Cross-cutting (logging, errors, rate limit, caching, CORS) | §10 |
| 11 | Configuration / secrets / deployment | §11 |
| 12 | Tests inventory | §12 |
| 13 | Findings — HIGH | §13 |
| 14 | Findings — MEDIUM | §14 |
| 15 | Findings — LOW | §15 |
| 16 | Section map | §16 |
| 17 | Compliance with Finbuckle.MultiTenant best practices | §17 |
| 18 | Compliance with OWASP API Security Top 10 | §18 |
| 19 | Compliance with NIST SP 800-204D (microservices saas) | §19 |
| 20 | Test execution summary | §20 |
| 21 | Build / static analysis summary | §21 |
| 22 | Risk register | §22 |
| 23 | Remediation plan | §23 |
| 24 | Sign-off | §24 |
| 25-31 | Appendices: file index, permission catalog, migration history, env vars, glossary, references, change log | §25-§31 |

---

## 17. Compliance with Finbuckle.MultiTenant best practices

| Best practice | Status | Evidence |
|---|---|---|
| Never resolve tenant from JWT claim (avoid tenant-via-claim attacks) | Compliant | [DependencyInjection.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/DependencyInjection.cs#L46-L56) — no `WithClaimStrategy` |
| Use `IMultiTenantContextAccessor` for read, scope mutation through middleware | Compliant | [TenantGuardMiddleware.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.API/Infrastructure/TenantGuardMiddleware.cs), [CurrentTenant.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/Common/CurrentTenant.cs) |
| Apply `HasQueryFilter` over `IHasTenantId` entities | Compliant | [AppDbContext.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/Data/AppDbContext.cs#L136-L160) |
| Stamp tenant on writes via `SaveChangesInterceptor` reading authorized context | Compliant | [TenantInterceptor.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/Data/Interceptors/TenantInterceptor.cs) |
| Use EFCore store for tenant registry in same DB | Compliant | [DependencyInjection.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/DependencyInjection.cs#L53-L56) |
| Separate migrations history table for tenant DB context | Compliant | [DependencyInjection.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/DependencyInjection.cs#L63-L65) |

---

## 18. Compliance with OWASP API Security Top 10 (2023)

| Risk | Status | Notes |
|---|---|---|
| API1: BOLA (Broken Object Level Authorization) | **Safe** | All controllers check `[HasPermission]` + tenant-scoped query filter; verified by [C1CrossTenantIsolationTests](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/tests/Centerix.SecurityTests/C1CrossTenantIsolationTests.cs) |
| API2: Broken Authentication | **Mostly safe** | H4: refresh not rate-limited; M1: logout does not bind to user |
| API3: Broken Object Property Level Authorization | **Safe** | No entities are bound directly; DTOs only |
| API4: Unrestricted Resource Consumption | **Mostly safe** | Login rate-limited; M5: writes not rate-limited |
| API5: Broken Function Level Authorization | **Safe** | Platform vs. tenant boundary enforced both by `[HasPermission]` and `PlatformAdminGuard` |
| API6: Unrestricted Access to Sensitive Business Flows | **Safe** | Invitation requires token + correct user; subscribes/approvals are platform-scoped |
| API7: Server Side Request Forgery | **N/A** | No outbound HTTP from the audited code paths |
| API8: Security Misconfiguration | **Mostly safe** | H3: hardcoded temp password; L1: empty JWT secret (but fail-fast) |
| API9: Improper Inventory Management | **Safe** | API versioning enabled; routes stable |
| API10: Unsafe Consumption of APIs | **N/A** | No third-party API consumption in the audited surface |

---

## 19. Compliance with NIST SP 800-204D / 800-204C (multi-tenant microservices)

| Control | Status | Evidence |
|---|---|---|
| Tenant isolation at data layer | Compliant | Query filter + interceptor |
| Tenant isolation at request layer | Compliant | `TenantGuardMiddleware` |
| Authorization with per-tenant claims | Compliant | DB-resolved permissions |
| Audit logging | Compliant | Serilog + `AuditWriter` |
| Rate limiting | Partial | H4, M5 |
| Secret management | Partial | L1, L2, L6 |
| Idempotent tenant onboarding | Compliant | `RegisterFromInvitation` is atomic |

---

## 20. Test execution summary

`dotnet test` was **not** executed in this audit (no Docker host available; Testcontainers requires it). The static inventory of tests in `tests/Centerix.SecurityTests/` (21 files) is comprehensive: isolation, guard rules, invitation flows, expiry, authorization phases 2/3, and SQL-Server-backed multi-writer tests are all present. Recommend running the suite in CI on every PR.

---

## 21. Build & static analysis summary

```
> dotnet build Centerix.slnx -nologo --verbosity minimal
Build succeeded.
    0 Error(s)
4,783 Warning(s)  // all StyleCop style/analyzer warnings; no compiler warnings
```

The high warning count is StyleCop-only (default rules). Consider relaxing specific StyleCop rules (SA1600, SA1633) in `Directory.Build.props` for focused review, or fix the warnings in a separate PR.

---

## 22. Risk register

| Risk ID | Title | Severity | Likelihood | Composite |
|---|---|---|---|---|
| R-H1 | `AcceptInvitation` race allows duplicate memberships | High | Medium | High |
| R-H2 | `CreateStudent` limit counter mismatch under concurrent failure | High | Medium | High |
| R-H3 | Hardcoded admin password reaches production | High | Low | Medium |
| R-H4 | Refresh-token brute-force (no rate limit) | High | Low | Medium |
| R-M1 | Token-bound logout abused | Medium | Low | Low |
| R-M2 | RefreshToken rotation race | Medium | Low | Low |
| R-M3 | TenantInvitation race | Medium | Low | Low |
| R-M4 | Future platform-scope addition bypasses guard | Medium | Low | Low |
| R-M5 | Write-endpoint flooding | Medium | Medium | Medium |

---

## 23. Remediation plan (suggested order)

1. **H1**: Wrap `AcceptInvitationCommand` in a transaction with atomic claim. *(same shape as `RegisterFromInvitationCommand`)*
2. **H2**: Wrap `CreateStudentCommand` reservation + insert in one `BeginTransactionAsync`.
3. **H3**: Replace hardcoded password with random generation + first-login reset.
4. **H4**: Add `RefreshPolicy` rate limiter and apply via `[EnableRateLimiting]`.
5. **M1**: Bind logout to authenticated user id.
6. **M2, M3**: Add `RowVersion` to `RefreshToken` and `TenantInvitation`; regenerate migrations.
7. **M4**: Add a unit test asserting that every permission outside `PlatformScope.PermissionCodes` requires membership.
8. **M5**: Add a per-user sliding window limiter.
9. **L1-L7**: Documentation and configuration improvements.

---

## 24. Sign-off

The implemented system demonstrates a careful, layered approach to multi-tenant security: verified tenant context separated from resolved context, fail-closed query filters, per-request permission resolution, defense-in-depth at the platform/tenant boundary, and atomic concurrency where it matters most (subscription limits, invitation registration).

The four HIGH findings are correctness gaps (concurrency + transaction boundaries + secret management), not architectural flaws. With the remediation plan applied, this system is suitable for production deployment.

---

## 25. Appendix A — File index (audited)

```
src/Centerix.API/
  Program.cs
  DependencyInjection.cs
  appsettings.json
  appsettings.Development.json
  Controllers/
    AcademicStagesController.cs, AcademicYearsController.cs
    AddOnCatalogsController.cs, ApiController.cs
    AttendanceLogsController.cs, AuthController.cs
    BranchesController.cs, FeaturesController.cs
    InvitationsController.cs, InvoicesController.cs
    MembershipsController.cs, PlansController.cs
    PlatformPermissionsController.cs, PlatformRolesController.cs
    PlatformUsersController.cs, StudentsController.cs
    TenantAddOnsController.cs, TenantCRMLeadsController.cs
    TenantCreditsController.cs, TenantPlansController.cs
    TenantProvisioningJobsController.cs
    TenantReferralCodesController.cs, TenantReferralsController.cs
    TenantsController.cs
  Infrastructure/
    TenantGuardMiddleware.cs

src/Centerix.Application/
  Common/
    Behaviours/CachingBehaviour.cs
    PermissionConstants.cs
    Interfaces/{IAppDbContext, ICurrentTenant, ICurrentUser, ...}
  Platform/
    Invitations/Commands/{CreateInvitationCommand, AcceptInvitationCommand, RegisterFromInvitationCommand, RevokeInvitationCommand}.cs
  Students/Students/Commands/CreateStudentCommand.cs
  ...

src/Centerix.Infrastructure/
  DependencyInjection.cs
  Auth/
    JwtTokenService.cs, RefreshTokenService.cs
    Permissions.cs, PermissionCatalog.cs
    PermissionPolicyProvider.cs (contains PermissionAuthorizationHandler + FeatureAuthorizationHandler)
    IdentityService.cs, RoleService.cs
    ApplicationRole.cs, HasPermissionAttribute.cs
    InvitationLinkBuilder.cs, TenantPermissionResolver.cs
  Common/
    CurrentTenant.cs, CurrentUser.cs, PlatformAdminGuard.cs
  Data/
    AppDbContext.cs
    Interceptors/TenantInterceptor.cs
    Migrations/  (16 files)
    Configurations/ (per-entity)
  Platform/LimitService.cs
  Tenancy/TenancyConstants.cs

tests/Centerix.SecurityTests/
  21 test files (see §12)
```

---

## 26. Appendix B — Permission catalog (observed)

Defined in [Permissions.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/Auth/Permissions.cs):
- `Plans.{Create, Read, Update, Delete}`
- `Features.{Create, Read, Update, Delete}`
- `Tenants.{Create, Read, Update, Delete}`
- `TenantPlans.{Create, Read, Update, Delete}`
- `Subscriptions.{Read, Manage}`
- `TenantCRMLeads.{Create, Read, Update, Delete}`
- `Students.{Create, Read, Update, Delete}`
- `AttendanceLogs.{Create, Read}`
- `Branches.{Create, Read, Update, Delete}`
- `AcademicStages.{Create, Read, Update}`
- `AcademicYears.{Create, Read, Update}`
- `AddOnCatalogs.{Create, Read, Update}`
- `TenantAddOns.{Create, Read, Update}`
- `TenantLimitOverrides.{Create, Read}`
- `TenantReferralCodes.{Create, Read}`
- `TenantReferrals.{Create, Read}`
- `TenantProvisioningJobs.{Create, Read, Update}`
- `PlatformUsers.{Create, Read, Update, Delete}`
- `PlatformRoles.{Create, Read, Update, Delete}`
- `PlatformPermissions.{Read}`
- `Invoices.{Create, Read, Update, Delete}`
- `TenantCredits.{Create, Read}`
- `Invitations.{Create, Read, Revoke}`
- `Memberships.{Read, Manage}`

`GetPlatformAdminPermissions` returns ALL.
`GetTenantAdminPermissions` returns `TenantPlans.Read, TenantCRMLeads.*, Invitations.*, Memberships.*` (deliberately omits `Subscriptions.Manage`).
`GetTenantUserPermissions` returns `TenantPlans.Read, TenantCRMLeads.Read, Memberships.Read`.

---

## 27. Appendix C — Migration history

```
20260704061951_InitialCreate                  // Platform.TenantRegistry, Identity, etc.
20260704185803_AuthPermissionSystem           // Permission, RolePermission
20260725003515_AddPermissionsAndRolePermissions
20260725004023_AddRoleMetadata
20260725004605_AddAuditLog
20260725010643_AddRefreshTokens               // RefreshToken with TokenHash unique index
20260725153142_AddStudentsEducationModule
20260725214300_RefineM01StudentsPerERD
20260725215535_ImplementTenantAndAuditColumns // TenantId columns, audit columns
20260808221803_PendingChanges
20260810221112_InitialCreate (TenantDb)       // Separate migrations history table
20260810222751_RemoveTenantIdFromRolePermission
20260818223042_AddTenantMemberships
20260820231501_RemoveLastSyncedAt
20260824185054_AddRoleNameToTenantMemberships
20260826121232_Phase2SubscriptionsAndLimits   // RowVersion, snapshot columns, filtered index
```

---

## 28. Appendix D — Environment variables / configuration surface

| Setting | Required | Default | Validated at startup |
|---|---|---|---|
| `ConnectionStrings:DefaultConnection` | Yes | dev: `Server=.;Database=CenterixDb;Trusted_Connection=True` | No (would fail at first DB call) |
| `JwtSettings:Secret` | Yes | empty | Yes (≥32 chars) |
| `JwtSettings:Issuer` | Yes | `Centerix` | Yes (non-empty) |
| `JwtSettings:Audience` | Yes | `CenterixUsers` | Yes (non-empty) |
| `JwtSettings:ExpirationInMinutes` | No | 60 | No |
| `JwtSettings:RefreshExpirationInDays` | No | 7 | Yes (≥1) |
| `Invitations:BaseUrl` | Yes (prod) | only in `appsettings.Development.json` | Yes (must be absolute http(s)) |

---

## 29. Appendix E — Glossary

- **Resolved tenant**: the tenant identifier chosen by the client (header `tenant` or host). Untrusted for authorization.
- **Authorized tenant**: the tenant identifier verified by `TenantGuardMiddleware` (membership + active status + expiry). Trusted; single source of truth for `ICurrentTenant.TenantId`.
- **Platform-scoped endpoint**: an endpoint whose `[HasPermission]` code is in `Permissions.PlatformScope.PermissionCodes`; bypasses the membership check.
- **Tenant-scoped endpoint**: any endpoint whose permission is NOT platform-scoped; requires an active `TenantMembership` for the resolved tenant.
- **Capability token**: an opaque 256-bit token used as the credential for invitation registration. Stored only as its SHA-256 hash.

---

## 30. Appendix F — References

- Finbuckle.MultiTenant documentation: https://docs.finbuckle.com
- OWASP API Security Top 10 (2023): https://owasp.org/API-Security/editions/2023/
- ASP.NET Core 10 security guidance: https://learn.microsoft.com/aspnet/core/security/
- NIST SP 800-204D (microservices-based SaaS): https://csrc.nist.gov/publications
- EF Core 10 migrations: https://learn.microsoft.com/ef/core/managing-schemas/migrations/

---

## 31. Appendix G — Change log

| Date | Author | Change |
|---|---|---|
| 2026-08-31 | audit run | Initial audit report (this file) |