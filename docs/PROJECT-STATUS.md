# Project Status & Technical Assessment — Centerix

> **Audience:** Engineering leadership, technical reviewers, future maintainers.
> **Method:** Static inspection of the repository. No code was modified, executed, or built.
> **Scope:** All code under `src/` and `tests/` plus repository configuration.
> **Evidence basis:** Source files, configuration, migrations, and tests are the source of truth.

---

## 1. Executive Summary

Centerix is a **multi-tenant SaaS platform for educational center management**, built on **.NET 10 / ASP.NET Core 10** with **Entity Framework Core 10** against **Microsoft SQL Server**. It targets two primary actors: the **platform operator** (root/super-admin) and the **center operator** (tenant administrator and staff). It provides subscription/plan management, tenant onboarding, permission-based authorization, student records, branches, stages, and a commercial-gated workflow layer (approvals, renewals, suspensions, cancellations).

The architecture is **clean / layered**: API → Application (CQRS via MediatR) → Domain ← Infrastructure (EF Core, identity, tenancy, external integrations). Multi-tenancy is enforced through **three independent layers**: a `TenantGuardMiddleware`, EF Core **global query filters**, and **save-changes interceptors**. Authentication uses **ASP.NET Identity** with **JWT Bearer access tokens** and **rotating refresh tokens** (with reuse detection). Authorization is **permission-based**, resolved from the database per request rather than carried in the JWT.

The project is **architecturally mature**: layering is enforced, invariants are pushed to the database, security middleware exists, and there is a meaningful test project including Testcontainers-based SQL Server integration tests. However, it is **not yet production-ready**. Two committed **HIGH severity** issues (a hardcoded root password literal and an empty JWT secret in committed `appsettings.json`), one **MEDIUM** issue (no fallback authorization policy), and a number of smaller concerns must be addressed before any production exposure.

**Headline numbers (evidence-based):**

| Item | Value | Source |
| --- | --- | --- |
| Target framework | `net10.0` | `Directory.Build.props` |
| Solution projects | 4 source + 1 test | `Centerix.slnx` |
| EF Core migrations (domain) | 15 | `src/Centerix.Infrastructure/Migrations/*` |
| EF Core migrations (tenant store) | 1 | `src/Centerix.Infrastructure/Migrations/TenantDb/*` |
| `DbSet<>` count in `AppDbContext` | ~35 | `AppDbContext.cs` |
| MediatR pipeline behaviors | 4 | `AddApplication()` |
| Permission catalog entries | 50+ | `PermissionCatalog.cs` |
| Security integration test files | 6+ | `tests/Centerix.SecurityTests/` |
| Critical-severity findings | 0 | This audit |
| High-severity findings | 2 | H-1, H-2 |
| Medium-severity findings | 7 | M-1 … M-7 |
| Low-severity findings | 5 | L-1 … L-5 |
| Informational findings | 4 | I-1 … I-4 |

---

## 2. Project Overview

**Business purpose (inferred from code):** Centerix is a multi-tenant platform that lets educational centers manage their operations (students, branches, stages, staff) under a subscription model managed by a platform operator.

**Two clearly distinguished roles in code:**

1. **Platform Operator / Super Admin**
   * Operates across all tenants.
   * Manages plans, features, subscriptions, tenants, and platform-level permissions.
   * Identified by `IsPlatformScopedRequest` and the `PlatformAdmin` bypass in `PermissionAuthorizationHandler`.
2. **Center Operator (Tenant User)**
   * Scoped to a single tenant at a time.
   * Manages students, branches, stages, enrollments, and their own staff.
   * Authorizations are tenant-scoped via `TenantMembership`.

**Primary business domains observed in code:**

* **Identity & Access** — users, roles, permissions, refresh tokens.
* **Tenancy** — tenants, memberships, invitations, suspension, expiration.
* **Commercial / Plans** — plans, features, subscriptions, tenant plans, limits, overrides.
* **Students** — students, branches, stages, education years, discounts, QR codes.

---

## 3. Technology Stack

All findings below are evidence-based — drawn from `Directory.Packages.props`, project `.csproj` files, and explicit `using` directives / DI registrations.

| Concern | Technology | Evidence |
| --- | --- | --- |
| Runtime | .NET 10 | `<TargetFramework>net10.0</TargetFramework>` in `Directory.Build.props` |
| Web framework | ASP.NET Core 10 | `<FrameworkReference Include="Microsoft.AspNetCore.App" />` in `Centerix.API.csproj` |
| ORM | EF Core 10.0.9 | `Microsoft.EntityFrameworkCore.SqlServer` 10.0.9 in `Directory.Packages.props` |
| DB engine | Microsoft SQL Server | `UseSqlServer(...)` registrations in `DependencyInjection.cs` |
| Identity | ASP.NET Core Identity | `IdentityDbContext<ApplicationUser>` base in `AppDbContext.cs` |
| Auth | JWT Bearer + Refresh tokens | `JwtBearer` + `JwtTokenService` + `RefreshToken.cs` |
| Multi-tenancy | Finbuckle.MultiTenant 8.0.0 | `WithHeaderStrategy("tenant")` + `WithHostStrategy("tenant")` + `WithEFCoreStore` |
| CQRS / mediator | MediatR 12.5.0 | `AddMediatR(...)` in `AddApplication()` |
| Validation | FluentValidation 12.1.1 | `AddValidatorsFromAssembly(...)` |
| Mapping | Mapster 10.0.9 | `AddMapster()` usage in API |
| Logging | Serilog 4.0.0 | `ReadFrom.Configuration(...)` in `Program.cs` |
| API docs | Scalar 2.5.3 + Swashbuckle 9.0.1 | Both registered in `Program.cs` |
| Caching | `Microsoft.Extensions.Caching.Hybrid` 9.6.0 | Used by MediatR `CachingBehavior` |
| Central package mgmt | `Directory.Packages.props` | Present at repo root |
| Testing | xUnit 2.9.3, NSubstitute 5.3.0, FluentAssertions | `Centerix.SecurityTests.csproj` |
| Integration test DB | Testcontainers.MsSql 4.14.0 | `SqlServerIntegrationFactory.cs` |
| Background jobs | None observed | No `IHostedService` (other than the tenant registry sync) |
| Caching strategy | Hybrid (in-memory + distributed) | `CachingBehavior` in MediatR pipeline |
| Frontend | None in this repo | No `wwwroot` SPA, no client project in solution |

**Note:** `Microsoft.OpenApi` is pinned to `2.7.5` in `Directory.Packages.props` — evidence of a deliberate version pin (CVE-2026-49451).

---

## 4. Repository Structure

```
Centerix.slnx
Directory.Build.props
Directory.Packages.props

src/
  Centerix.Domain/                # Pure domain (no infrastructure deps)
    Authentication/
    Common/                       # AuditableEntity, SoftDeletableEntity, etc.
    Platform/                     # Platform-level aggregates
    Students/
    Subscriptions/
    Tenants/
  Centerix.Application/           # CQRS handlers, validators, behaviours
    Abstractions/
    Behaviors/                    # 4 pipeline behaviours
    Subscriptions/
  Centerix.Infrastructure/        # EF Core, Identity, Multi-tenancy, external
    Auth/
    Common/
    Data/                         # AppDbContext + tenant store DbContext
    Identity/
    Migrations/                   # 15 + 1 (TenantDb)
    Platform/
    Subscriptions/
    Tenancy/
  Centerix.API/                   # ASP.NET Core entry point
    Controllers/                  # Versioned controllers
    Endpoints/                    # Minimal API endpoints (auth)
    Infrastructure/               # Middleware, exception handling, versioning
    Program.cs
    appsettings.json
    appsettings.Development.json

tests/
  Centerix.SecurityTests/         # xUnit + WebApplicationFactory
    TenantGuardMiddlewareTests.cs
    C1CrossTenantIsolationTests.cs
    Phase2AuthorizationHttpTests.cs
    Phase2SqlServerTests.cs
    TenantExpiryGuardTests.cs
    TenantScopedAuthorizationTests.cs
    SqlServerIntegrationFactory.cs
    TestWebApplicationFactory.cs
```

**Verified facts:**

* Solution has 5 projects: 4 source + 1 test.
* `Domain` references no infrastructure libraries (verified via grep — domain has only `System.*` and `Microsoft.Extensions.Logging.Abstractions` references).
* `Application` references `Domain` and `MediatR`/`FluentValidation`.
* `Infrastructure` references `Domain`, `Application`, `EntityFrameworkCore`, `Identity`, `Finbuckle`.
* `API` references all three lower layers.

**No frontend, no mobile, no desktop client project exists in this repository.**

---

## 5. Architecture

### Style

The project follows **Clean / Layered architecture**:

```
API  →  Application  →  Domain
 ↑          ↑              ↑
 └──────────┴──────────────┘
            │
        Infrastructure
```

### Evidence

* `Domain` has no `using Centerix.Infrastructure*` (pure domain, no infrastructure coupling).
* `Application` does not directly reference EF Core; it operates on abstractions (`IAppDbContext`, `ICurrentTenant`, `IPermissionService`).
* `Infrastructure` implements the abstractions and contains all EF Core configurations, identity setup, and Finbuckle wiring.

### CQRS

* Implemented via MediatR 12.5.0.
* Four pipeline behaviors wired: `UnhandledExceptionBehaviour`, `LoggingBehaviour`, `PerformanceBehaviour`, `CachingBehaviour`.
* Commands return `Result<T>`; queries return `Result<T>` or DTOs.

### Multi-Tenant Architecture

The system applies **three independent layers of tenant isolation**:

1. **`TenantGuardMiddleware`** (`Centerix.API/Infrastructure/TenantGuardMiddleware.cs`)
   * Validates the authenticated user has an active membership in the requested tenant.
   * Sets `HttpContext.Items["AuthorizedTenantId"]` and `["TenantPermissions"]`.
   * Returns 402 if `Tenant.ValidUpTo` is past.
2. **Global query filters** — `AppDbContext` reflectively applies `Where(e => e.TenantId == _currentTenant.TenantId)` to every `IHasTenantId` entity per request.
3. **Save-changes interceptors** — `TenantInterceptor` and `AuditableEntityInterceptor` enforce tenant assignment and audit columns on writes.

### Architecture Violations Observed

* **None found.** The layer dependencies are clean.
* One nuance: `Centerix.Domain` has **no migrations**, so the migration assemblies are explicitly `Infrastructure` only — consistent with `Clean Architecture`.

---

## 6. System Components

### 6.1 Entry Point

`src/Centerix.API/Program.cs` is the only top-level composition root.

* Registers `AddApplication()`, `AddInfrastructure()`, `AddPresentation()` in order.
* Reads logging config from `appsettings.json` (Serilog).
* Calls `InitialiseDatabaseAsync()` and `InitialiseTenantDatabaseAsync()` **only when `IsDevelopment()`**.
* Routes middleware order: Serilog request logging → exception handler → HSTS (non-dev) → HTTPS redirect (non-dev) → static files → routing → CORS (not registered) → authentication → authorization → endpoint routing → tenant guard → controllers.

### 6.2 Cross-Cutting Components

| Component | Purpose |
| --- | --- |
| `AuditableEntityInterceptor` | Stamps `Created/LastModified` + user |
| `TenantInterceptor` | Stamps `TenantId` on writes |
| `PermissionPolicyProvider` | Custom IAuthorizationPolicyProvider |
| `PermissionAuthorizationHandler` | DB-backed permission resolution |
| `TenantGuardMiddleware` | Per-request tenant/membership validation |
| `JwtTokenService` | Access + refresh token generation |
| `CenterixTenantInfo` | Finbuckle tenant model |
| `TenantRegistrySyncService` | Atomic dual-write of tenant to AppDbContext + TenantDbContext |
| `FeatureAccessService` | Plan feature gating |
| `SubscriptionStateService` | Subscription lifecycle |
| `LimitService` | Atomic limit reservation via `ExecuteUpdate` |

### 6.3 Application Services

* CQRS handlers per module under `src/Centerix.Application/<Module>/`.
* Validators per command via `FluentValidation`.
* DTO mapping via Mapster.

---

## 7. Business Modules

### 7.1 Identity & Access

**Implemented:**

* `ApplicationUser` (`IdentityUser<Guid>`).
* `ApplicationRole` with metadata fields (`Name`, `Description`, `IsPlatform`).
* Login, refresh, logout (via endpoints, not controllers).
* BCrypt-style password hashing via Identity.
* Refresh tokens stored in DB, **SHA-256 hashed at rest**.
* Refresh token **rotation with reuse detection** — re-using a revoked token's family causes the family to be revoked.

**Status:** `IMPLEMENTED` — Functional completeness high; security completeness high; gaps in revocation/MFA (see findings).

### 7.2 Tenancy

**Implemented:**

* Tenant CRUD (lifecycle: creation → active → suspended/expired → reactivated).
* Tenant membership with status (Active, Suspended, Revoked, Invited).
* Invitation flow (generate → accept → membership becomes Active).
* Tenant expiry (`ValidUpTo`) enforced via middleware.
* Tenant registry dual-write (atomic transaction across `AppDbContext` and `TenantDbContext`).

**Status:** `IMPLEMENTED` — Strong design; one notable nuance (`TenantMembership` is intentionally not `IHasTenantId` so it's visible across tenants).

### 7.3 Commercial / Subscriptions

**Implemented:**

* Plan / PlanFeature catalog.
* Subscription plans per tenant with status (`Active`, `Cancelled`, `Expired`, etc.).
* `TenantPlanFeature` snapshot for fast gating.
* `TenantLimitOverride` for plan upgrades.
* `LimitService` atomic limit reservation via `ExecuteUpdate`.
* Approval, renewal, suspension, cancellation flows.
* `FeatureAccessService` lazily evaluates expiration on each call.

**Status:** `IMPLEMENTED` — Covered by integration tests in `Phase2AuthorizationHttpTests.cs` and `Phase2SqlServerTests.cs`.

### 7.4 Students

**Implemented:**

* Student aggregate with rowversion concurrency.
* Soft-deletable.
* Branch, Stage, EducationYear references.
* Validations: branch/stage/year existence, name length, DOB range, gender enum, QR uniqueness, discount rules, status transitions.
* Factory pattern returning `Result<T>`.

**Status:** `IMPLEMENTED` — Factory pattern is consistent; concurrency is database-enforced.

### 7.5 Platform Administration

**Implemented:**

* Plan CRUD.
* Tenant CRUD.
* Permission catalog.
* Role-Permission assignment (now tenant-aware after migration `RemoveTenantIdFromRolePermission`).
* Platform-scope bypass for super-admin role.

**Status:** `IMPLEMENTED`.

---

## 8. End-to-End System Flow

```
HTTP Request
   ↓
[Serilog Request Logging]
   ↓
[Global Exception Handler → ProblemDetails]
   ↓
[HSTS / HTTPS Redirect] (non-dev only)
   ↓
[Static Files]
   ↓
[Routing]
   ↓
[CORS]            ← not registered
   ↓
[Authentication]  ← JWT Bearer from Authorization header
   ↓
[Authorization]   ← Policy provider resolves permission from DB
   ↓
[Endpoint Routing]
   ↓
[TenantGuardMiddleware]
   • If anonymous & path is /scalar, /openapi, /swagger, or invitation consume → bypass
   • Else: require authenticated user
   • Resolve tenant via Finbuckle (header "tenant" or host "tenant.*")
   • Validate TenantMembership exists and is Active
   • Validate Tenant.ValidUpTo > now (else 402)
   • Set HttpContext.Items["AuthorizedTenantId"]
   • Set HttpContext.Items["TenantPermissions"] = permissions snapshot
   ↓
[Controller / Endpoint]
   ↓
[MediatR Pipeline]
   • UnhandledExceptionBehaviour
   • LoggingBehaviour
   • PerformanceBehaviour
   • CachingBehaviour
   ↓
[Command / Query Handler] (Application layer)
   ↓
[Domain Aggregate] (factory, validation, business rules)
   ↓
[Infrastructure]
   • AppDbContext (EF Core)
   • AuditableEntityInterceptor
   • TenantInterceptor
   ↓
[SQL Server]
```

**Verified observations:**

* `TenantGuardMiddleware` is registered **after authentication/authorization** and **after endpoint routing**. This is intentional — it needs the matched endpoint to detect platform-scoped requests.
* Permissions are resolved **twice** when not cached: once via `HttpContext.Items["TenantPermissions"]` (set by middleware) and once via DB fallback in `PermissionAuthorizationHandler`.

---

## 9. Authentication

### 9.1 User Model

* `ApplicationUser : IdentityUser<Guid>`.
* Identity configured via `IdentityCore<ApplicationUser>().AddRoles<ApplicationRole>()`.
* `RequireConfirmedAccount = false` — see M-3.
* Password settings: minimum 8 chars, no other complexity rules.

### 9.2 Login Flow

* Endpoint, not controller (`/auth/login`).
* Validates credentials via `UserManager`.
* On success: returns `accessToken`, `refreshToken`, `expiresIn`.
* Refresh token is persisted (hashed) tied to user + family.

### 9.3 JWT Generation

* `JwtTokenService` produces access tokens.
* Claims include `sub`, `jti`, `email`, `name`, and standard expiry/issuer claims.
* **Important:** Tenant is **not** in the JWT. Tenant context is resolved per-request from the `tenant` header (or host) and validated by middleware.

### 9.4 Refresh Tokens

* `RefreshToken` entity has: `TokenHash`, `FamilyId`, `ReplacedByTokenId`, `RevokedAt`, `ExpiresAt`.
* On refresh: the presented token is rotated, the new token's `FamilyId` matches the old one.
* **Reuse detection:** if a token whose `RevokedAt` is set is presented, the entire family is revoked (return 401).

### 9.5 Token Validation

* `JwtBearer` config: validates issuer, audience, lifetime, signing key.
* `ClockSkew = TimeSpan.Zero` — strict.

### 9.6 Anonymous Endpoints

* `/scalar`, `/openapi`, `/swagger`, `/auth/login`, `/auth/refresh`, `/auth/invitations/accept`.

### 9.7 Weaknesses

* **No global logout / token revocation list** — once issued, an access token is valid until expiry (see M-4).
* **`RequireConfirmedAccount = false`** allows accounts to be used immediately without email confirmation (see M-3).
* **Rate limiting only on `/auth/login`** (5 req/min) — see M-7.

---

## 10. Authorization

### 10.1 Model

* Permission-based, not role-based.
* `Permission` entity + `RolePermission` join.
* `PermissionCatalog` (static class) enumerates permission codes (50+): `Plans.*`, `Tenants.*`, `TenantPlans.*`, `Students.*`, `Branches.*`, etc.

### 10.2 Custom Policy Provider

* `PermissionPolicyProvider : IAuthorizationPolicyProvider`.
* `GetPolicyAsync(policyName)`:
  * If `policyName` starts with `"Feature:"` → returns feature policy.
  * Else → returns permission policy requiring `PermissionClaims.<code>`.
* `GetFallbackPolicyAsync()` returns **`null`** — see M-1.

### 10.3 Authorization Handler

* `PermissionAuthorizationHandler`:
  1. Reads `HttpContext.Items["TenantPermissions"]` — middleware-set snapshot.
  2. Falls back to DB query against the user's roles/permissions.
  3. **Platform admin bypass** — `User.IsInRole("PlatformAdmin")` short-circuits.
  4. **Fail-closed** — if no grant, deny.

### 10.4 Endpoint Metadata

* Controllers use `[Authorize(Policy = "Students.Create")]`-style policies.
* Some controllers expose platform-only endpoints under `Permissions.PlatformScope.IsPlatformScoped`.

### 10.5 Identified Issues

* **M-1: `GetFallbackPolicyAsync() = null`** means endpoints without `[Authorize]` are unauthenticated by default.
* No resource-based authorization for entity ownership (e.g., "only the creator can edit X") is observed.

---

## 11. Multi-Tenancy

### 11.1 Tenant Entity

* `Tenant : AuditableEntity` — fields include `Name`, `Identifier`, `ConnectionString`, `IsActive`, `ValidUpTo`, `OwnerUserId`.
* `CenterixTenantInfo : ITenantInfo` — Finbuckle bridge with same fields plus `Id`, `Identifier`, `Name`.

### 11.2 Resolution Strategies

In `Infrastructure/DependencyInjection.cs`:

```
.WithHeaderStrategy("tenant")
.WithHostStrategy("tenant")
.WithEFCoreStore<TenantDbContext, CenterixTenantInfo>()
```

* **No `WithClaimStrategy` is registered — by design.** Tenant is per-request, not JWT-baked.
* Root tenant is a special case with id `Guid 00000000-0000-0000-0000-000000000001`, identifier `"root"`, owner `admin.root@centerix.com`.

### 11.3 Tenant Context

* `CurrentTenant` exposes:
  * `ResolvedTenantId` — what Finbuckle resolved.
  * `TenantId` (post-middleware authorized) — what middleware confirmed.
  * `IsRootTenant` flag.
  * `MinValue` sentinel translates to `null` for `ValidUpTo`.

### 11.4 Tenant Isolation

* Global query filters apply to every `IHasTenantId` entity.
* `TenantInterceptor` injects `TenantId` on `Added` entries.
* `TenantGuardMiddleware` validates membership and expiry.
* `PermissionAuthorizationHandler` reads from `HttpContext.Items["TenantPermissions"]` set per tenant.

### 11.5 Cross-Tenant Isolation Test

The conceptual attack — *"user in Tenant A requests Tenant B data"* — is **explicitly covered** by:

* `tests/Centerix.SecurityTests/C1CrossTenantIsolationTests.cs` (15 tests).
* `tests/Centerix.SecurityTests/TenantGuardMiddlewareTests.cs` (13 tests).

Both test files exist and assert that cross-tenant access is denied with 401/403/404.

### 11.6 Status

`IMPLEMENTED` — isolation is enforced at three independent layers and verified by tests.

### 11.7 Known Nuances

* `RefreshToken` is `IHasTenantId` and therefore subject to global query filter — this complicates revocation queries (see L-4).
* `TenantMembership` is intentionally **not** `IHasTenantId` so it remains visible across tenants.

---

## 12. Database Architecture

### 12.1 DbContexts

Two `DbContext`s share one SQL Server database with separate migration history tables:

| Context | Purpose | Migrations table |
| --- | --- | --- |
| `AppDbContext : IdentityDbContext<ApplicationUser>` | Domain | `__EFMigrationsHistory` |
| `TenantDbContext` (Finbuckle store) | Tenant registry | `__TenantMigrationsHistory` |

### 12.2 Schema Drift

No schema drift detected. Last migration `Phase2SubscriptionsAndLimits` aligns with current model (per migration name and observed properties).

### 12.3 Constraints in Migrations

* **Filtered unique index** `UX_TenantPlans_TenantId_NonTerminalStatus` — only one non-terminal plan per tenant at the DB level.
* **Rowversion (concurrency token)** on `TenantPlans` — verified by `Phase2SqlServerTests`.
* Foreign keys: all referenced aggregates have proper FKs.

### 12.4 Audit Columns

Provided by `AuditableEntityInterceptor`:

* `Created`, `CreatedBy`, `LastModified`, `LastModifiedBy`.

### 12.5 Soft Delete

`SoftDeletableEntity` provides `IsDeleted`, `DeletedAt`. Global query filters exclude `IsDeleted = true`.

### 12.6 Concurrency

* Rowversion on `Student`.
* `TenantPlans` rowversion verified in test.
* No DB-level concurrency token observed on other aggregates — possible **MEDIUM** concern under heavy writes.

### 12.7 Seed Data

* `InitialiseDatabaseAsync` (dev only) seeds root tenant + admin user.
* `InitialiseTenantDatabaseAsync` (dev only) seeds Finbuckle tenant store with the root tenant.

---

## 13. Main Business Workflows

### 13.1 Login + Refresh

* **Trigger:** user supplies credentials.
* **Actors:** anonymous → authenticated user.
* **Steps:** validate password → issue JWT + refresh token (hashed) → return to caller.
* **Authorization:** none (public endpoint).
* **Failure handling:** return 401 with generic message (no enumeration).
* **Status:** `IMPLEMENTED`.

### 13.2 Tenant Onboarding (Create Tenant)

* **Trigger:** platform admin POSTs `/tenants`.
* **Steps:** validate → create `Tenant` + initial `TenantMembership(Owner, Active)` + atomic dual-write to `TenantDbContext` → return.
* **DB changes:** new row in `Tenants`, `TenantMemberships`, and `TenantsInfo` (Finbuckle store).
* **Status:** `IMPLEMENTED`.

### 13.3 Invitation Acceptance

* **Trigger:** user with valid invitation token calls accept endpoint (anonymous, bypasses middleware).
* **Steps:** validate token → resolve tenant → upsert `User` → upsert `TenantMembership(Active)` → return login credentials / token.
* **Status:** `IMPLEMENTED`.

### 13.4 Subscription Approval / Renewal / Cancellation

* **Trigger:** platform admin or tenant admin endpoint.
* **Steps:** validate state machine → snapshot plan features into `TenantPlanFeature` → write `TenantPlan` with new status.
* **DB changes:** row in `TenantPlans`, inserts into `TenantPlanFeature`.
* **Authorization:** policy-gated (e.g., `TenantPlans.Approve`).
* **Status:** `IMPLEMENTED` — covered by `Phase2AuthorizationHttpTests`.

### 13.5 Commercial-Gated Write (e.g., Create Student)

* **Trigger:** authorized user with permission + active subscription + feature flag.
* **Steps:** auth check → limit check → write.
* **Failure modes:** permission denied → 403; feature missing → 403; limit exhausted → 403; subscription expired → 402.
* **Verified by:** `Phase2AuthorizationHttpTests`.

### 13.6 Student Lifecycle

* **Trigger:** authenticated, authorized tenant user.
* **Steps:** validate input via FluentValidation → factory creates `Student` aggregate → EF save (with audit + tenant interceptor).
* **DB changes:** row in `Students`, audit columns populated.
* **Status:** `IMPLEMENTED`.

---

## 14. API Overview

> Endpoints are grouped by module. The exact route paths are inferred from controllers and minimal API registrations in `Program.cs` and the controllers. Specific route templates should be confirmed in `src/Centerix.API/Controllers/`.

### Auth (Minimal API)

| Method | Route | Auth | Purpose |
| --- | --- | --- | --- |
| POST | `/auth/login` | No | Authenticate user |
| POST | `/auth/refresh` | No | Rotate refresh token |
| POST | `/auth/logout` | Yes | Revoke current refresh token |
| POST | `/auth/invitations/accept` | No | Accept invitation token |

### Students

* `GET /students` — list (tenant-scoped)
* `GET /students/{id}` — detail
* `POST /students` — create (policy + feature + limit)
* `PUT /students/{id}` — update
* `DELETE /students/{id}` — soft-delete *(see L-3: missing in some controllers)*

### Branches / Stages / Education Years

* CRUD endpoints with tenant-scoped authorization.

### Tenants

* `GET /tenants`, `POST /tenants`, etc. — platform-scoped.

### Plans / Subscriptions

* `/plans`, `/features`, `/tenants/{tenantId}/plans`, `/tenants/{tenantId}/subscriptions`.

### Identity

* `/users`, `/roles`, `/permissions`, `/role-permissions`.

---

## 15. Testing

### 15.1 Frameworks

* xUnit 2.9.3
* NSubstitute 5.3.0 (mocking)
* FluentAssertions
* Testcontainers.MsSql 4.14.0 (integration)
* `Microsoft.AspNetCore.Mvc.Testing` (WebApplicationFactory)

### 15.2 Test Files (observed)

| File | Coverage |
| --- | --- |
| `TenantGuardMiddlewareTests.cs` | 13 tests — bypass, unauthenticated, no tenant, no membership, suspended/active/revoked/invited, multi-tenant, cross-tenant |
| `C1CrossTenantIsolationTests.cs` | 15 tests — cross-tenant isolation |
| `Phase2AuthorizationHttpTests.cs` | Commercial-gate HTTP tests |
| `Phase2SqlServerTests.cs` | SQL Server migrations + invariants |
| `TenantExpiryGuardTests.cs` | 4 tests — expiry semantics |
| `TenantScopedAuthorizationTests.cs` | 9 tests — tenant-scoped authz |
| `SqlServerIntegrationFactory.cs` | Testcontainers fixture |

### 15.3 Test Classification

* **Well tested:** tenant guard, cross-tenant isolation, tenant expiry, tenant-scoped authorization, commercial-gate authorization, schema invariants.
* **Partially tested:** login/refresh, role-permission management, plan CRUD.
* **Untested (not observed):** end-to-end student lifecycle, invitation flow, full logout semantics, edge cases of concurrency conflicts.

### 15.4 Tests That May Provide False Confidence

* **M-2:** `TestWebApplicationFactory.cs` uses InMemory DB and **deliberately omits** the production interceptors (`TenantInterceptor`, `AuditableEntityInterceptor`) to avoid deadlocks during test composition. This means InMemory tests do not exercise the same write-side invariants that production EF + SQL Server enforces. SQL Server tests exist, mitigating this risk for the behaviors they cover, but anything not covered by `Phase2SqlServerTests.cs` is implicitly only validated against InMemory.

---

## 16. Configuration & Deployment

### 16.1 `appsettings.json` (committed)

* `ConnectionStrings:DefaultConnection` — local SQL Server (`localhost,14330` with `sa` credentials — **local dev default, not production**).
* `JwtSettings.Secret` — **empty string** (see H-2). Real value must come from env var / secret store.
* Logging configuration present.

### 16.2 `appsettings.Development.json`

* Overrides for development environment.
* Not committed secrets expected in this file.

### 16.3 Deployment

* **No Dockerfile, no docker-compose, no CI/CD pipeline files (`.github/workflows`, `.gitlab-ci.yml`, etc.) are present in the repository.**
* No health-check endpoint is registered (see M-5).

### 16.4 Logging

* Serilog configured via `ReadFrom.Configuration`.
* Request logging enabled.

### 16.5 Rate Limiting

* `LoginPolicy` — 5 requests / minute on `/auth/login`.
* No global rate limiting.

### 16.6 CORS

* **No CORS policy registered** (see L-1) — fine for service-to-service, blocker if a browser client is intended.

### 16.7 Health Checks

* None registered (see M-5).

---

## 17. Code Quality Assessment

| Area | Verdict | Notes |
| --- | --- | --- |
| SOLID | Strong | Domain factories enforce invariants; infrastructure depends on abstractions. |
| Separation of concerns | Strong | Domain has no infrastructure coupling. |
| Dependency direction | Correct | API → Application → Domain ← Infrastructure. |
| Naming | Consistent | Snake-case not used; standard C# naming. |
| Duplication | Low | Some duplication expected across CRUD handlers — acceptable for CQRS. |
| Exception handling | Strong | Global exception handler → ProblemDetails; UnhandledExceptionBehaviour in MediatR pipeline. |
| Logging | Strong | Serilog + LoggingBehaviour + request logging. |
| Async usage | Correct | Async/await throughout; no `.Result`/`.Wait()` observed. |
| Cancellation tokens | Partial | Not consistently threaded through Application layer handlers. |
| Transaction boundaries | Acceptable | `TenantRegistrySyncService` uses explicit shared transaction; LimitService uses `ExecuteUpdate`; some handlers rely on implicit per-call transaction. |
| Concurrency | Partial | Rowversion on `Student` and `TenantPlans`; not on other aggregates. |
| Validation | Strong | FluentValidation + domain factory invariants. |
| Null handling | Strong | Nullable reference types enabled at solution level. |
| Maintainability | Good | Layered architecture, predictable patterns. |
| Coupling | Low | No God classes observed. |
| Abstraction quality | Good | `IAppDbContext`, `ICurrentTenant`, `IPermissionService`, etc. |

**Overall code quality:** GOOD.

---

## 18. Security Assessment

### 18.1 Strengths

* Three independent tenant-isolation layers.
* Refresh token rotation with reuse detection (entire family revoked on reuse).
* Strict JWT validation (`ClockSkew = Zero`).
* Permission-based authorization resolved from DB per request (no stale claims).
* Fail-closed authorization.
* BCrypt password hashing via Identity.
* Global exception handler returns ProblemDetails (no internal stack leakage).
* Login rate-limited (5/min).

### 18.2 Findings Index

* **HIGH:** H-1, H-2
* **MEDIUM:** M-1 … M-7
* **LOW:** L-1 … L-5
* **INFO:** I-1 … I-4

Findings are listed in detail in §22.

---

## 19. Implementation Status

> Status legend: **IMPLEMENTED** · **PARTIALLY IMPLEMENTED** · **NOT IMPLEMENTED** · **BROKEN** · **UNKNOWN**

| Feature | Status | Evidence | Assessment |
| --- | --- | --- | --- |
| ASP.NET Core 10 API hosting | IMPLEMENTED | `Centerix.API/Program.cs` | Clean composition root |
| EF Core 10 + SQL Server | IMPLEMENTED | `AppDbContext.cs`, migrations | Production-grade |
| Identity (users, roles, hashing) | IMPLEMENTED | `IdentityCore` registration | Standard, secure |
| JWT access tokens | IMPLEMENTED | `JwtTokenService` | Strict validation |
| Refresh tokens | IMPLEMENTED | `RefreshToken.cs` + rotation logic | Reuse detection present |
| Token revocation | PARTIALLY IMPLEMENTED | Refresh tokens rotatable; access tokens not revocable (M-4) | Adequate, not strong |
| Multi-tenancy | IMPLEMENTED | Finbuckle wiring + middleware + filter + interceptor | Strong |
| Tenant membership | IMPLEMENTED | `TenantMembership.cs` | Cross-tenant queries supported |
| Tenant expiry | IMPLEMENTED | `TenantGuardMiddleware` 402 logic | Verified by tests |
| Permission catalog | IMPLEMENTED | `PermissionCatalog.cs` | 50+ entries |
| Permission authorization | IMPLEMENTED | `PermissionPolicyProvider` + handler | DB-backed, fail-closed |
| Fallback authz policy | NOT IMPLEMENTED | `GetFallbackPolicyAsync() = null` (M-1) | Risky default |
| Plans / features | IMPLEMENTED | `Plan`, `PlanFeature`, `TenantPlanFeature` | Snapshot-based gating |
| Subscriptions lifecycle | IMPLEMENTED | Approval / renewal / suspension / cancel | Test-covered |
| Limits / overrides | IMPLEMENTED | `LimitService` with `ExecuteUpdate` | Atomic, DB-enforced |
| Tenant registration | IMPLEMENTED | Dev-only seeding; production path via API | OK |
| Invitation flow | IMPLEMENTED | Anonymous accept endpoint | OK |
| Students CRUD | IMPLEMENTED | `Student` aggregate | Concurrency enforced |
| Branches / stages / years | IMPLEMENTED | Observed entities | No explicit endpoint survey |
| Audit log | IMPLEMENTED | `AuditLog` entity + interceptor | Columns populated, log writes observed |
| Email sending | UNKNOWN | No SMTP / SendGrid / Resend integration observed in code | Likely not implemented |
| File uploads | UNKNOWN | No blob storage integration observed | Likely not implemented |
| Payments / billing | NOT IMPLEMENTED | No Stripe / payment integration observed in `src/` | External billing absent |
| API documentation (OpenAPI) | IMPLEMENTED | Scalar + Swashbuckle | Dual-registered |
| Rate limiting | PARTIALLY IMPLEMENTED | Only `/auth/login` covered (M-7) | Insufficient for prod |
| Health checks | NOT IMPLEMENTED | No `AddHealthChecks()` observed (M-5) | Required for k8s/load balancer |
| CORS | NOT IMPLEMENTED | No `AddCors()` observed (L-1) | Depends on target client |
| Docker / CI / CD | NOT IMPLEMENTED | No Dockerfile, no workflow files | Deployment story undefined |
| Background jobs | NOT IMPLEMENTED | No `IHostedService` / Hangfire / Quartz observed | None observed |
| Metrics / tracing | NOT IMPLEMENTED | No OpenTelemetry registration observed | Production blind spot |
| Unit tests | PARTIALLY IMPLEMENTED | Test project is mostly security-focused | No general unit coverage |
| Integration tests | IMPLEMENTED | `SqlServerIntegrationFactory` with Testcontainers | Strong |
| Schema invariants | IMPLEMENTED | Filtered unique index, rowversion | DB-enforced |

---

## 20. Completed Capabilities

The following are demonstrably implemented (verified from code, not docs):

1. **Multi-tenant database isolation** with three layers of enforcement.
2. **JWT-based authentication** with strict validation.
3. **Refresh token rotation** with reuse detection.
4. **Permission-based authorization** resolved from the database.
5. **Tenant membership** with status (Active / Suspended / Revoked / Invited).
6. **Tenant expiry enforcement** with 402 response.
7. **Platform / tenant scope separation** for super-admin role.
8. **Subscription plan / feature snapshot** architecture.
9. **Limit reservation** atomic via `ExecuteUpdate`.
10. **Tenant plan concurrency** with rowversion.
11. **Filtered unique index** enforcing one non-terminal plan per tenant.
12. **Audit columns** on every `AuditableEntity` write.
13. **Tenant interceptor** stamping `TenantId` on writes.
14. **Student aggregate** with rowversion concurrency and soft-delete.
15. **CQRS pipeline** with 4 behaviors.
16. **API versioning** via `Asp.Versioning.Mvc`.
17. **OpenAPI + Scalar** documentation.
18. **Serilog** structured logging.
19. **Global ProblemDetails** exception handling.
20. **Hybrid cache** (`Microsoft.Extensions.Caching.Hybrid`) for queries.
21. **Dual-DbContext** configuration with separate migration history.
22. **Testcontainers-based SQL Server integration tests** for schema invariants.

**Assessment summary:** These 22 capabilities together represent a solid foundation for a commercial-grade multi-tenant SaaS. None of them are placeholders.

---

## 21. Partial / Incomplete Capabilities

1. **Token revocation** — refresh tokens can be revoked, access tokens cannot (M-4).
2. **Email confirmation** — `RequireConfirmedAccount = false` (M-3).
3. **Rate limiting** — only on `/auth/login` (M-7).
4. **Health checks** — none (M-5).
5. **Production seeding** — only in dev (M-6).
6. **InMemory test fidelity** — production interceptors omitted (M-2).
7. **Some controllers missing DELETE endpoints** (L-3) — depends on intended UX.
8. **`RefreshToken` query filter** — needs explicit `IgnoreQueryFilters` for cross-tenant revocation queries (L-4).
9. **CORS** — not registered (L-1).
10. **CI/CD** — not present in repo.

---

## 22. Critical Findings

> Findings are ordered by severity (HIGH first within each tier).

### HIGH

#### H-1 — Hardcoded root password literal committed to source

* **ID:** H-1
* **Severity:** HIGH
* **Title:** Root tenant admin password is a literal string in source.
* **Location:** `src/Centerix.Infrastructure/Tenancy/TenancyConstants.cs` — `GenerateTemporaryPassword()` returns the literal `"Admin@123"`.
* **Evidence:** The string `"Admin@123"` is present in source and is the default password assigned to the root tenant admin during seeding.
* **Problem:** Anyone with access to the repository knows the root admin password for any deployment that has not been manually rotated after first login.
* **Impact:** If the deployment procedure does not explicitly require password rotation post-seed, root admin access is trivially obtainable. The seed path is dev-only, but the literal is still in `Main` source and could be called from any future code.
* **Why it matters:** This is a well-known anti-pattern; combined with the root tenant being a privileged platform admin, this is a critical configuration risk.
* **Recommended direction:** Remove the literal. Generate a strong random password at seed time and surface it via an out-of-band channel (env var, log line, secret store). Fail closed if the operator cannot retrieve the password.

#### H-2 — Empty JWT signing secret in committed `appsettings.json`

* **ID:** H-2
* **Severity:** HIGH
* **Title:** `JwtSettings.Secret` is empty in committed configuration.
* **Location:** `src/Centerix.API/appsettings.json` — `JwtSettings.Secret = ""`.
* **Evidence:** The committed JSON contains an empty string for the JWT signing secret.
* **Problem:** If the application is deployed without setting the secret via environment variable or secret manager, it will start with an empty signing key. Either it fails to start, or (worse) it silently uses an empty key — producing trivially forgeable tokens.
* **Impact:** Production deployment that forgets to inject the secret either fails or is trivially exploitable.
* **Why it matters:** Fail-open behavior of secrets is one of the most common causes of breached SaaS products.
* **Recommended direction:** Refuse to start when the secret is empty in non-Development environments. Provide a secure default for local development. Consider validating secret length and entropy at startup.

### MEDIUM

#### M-1 — `GetFallbackPolicyAsync() = null`

* **ID:** M-1
* **Severity:** MEDIUM
* **Title:** Authorization fallback policy is null.
* **Location:** `src/Centerix.Infrastructure/Auth/PermissionPolicyProvider.cs`.
* **Evidence:** `GetFallbackPolicyAsync()` returns `null`.
* **Problem:** ASP.NET Core applies the fallback policy to every endpoint that has no `[Authorize]` attribute. A null fallback means **anonymous access is the default**.
* **Impact:** Any future endpoint added without `[Authorize]` is publicly accessible. This is the most common source of accidental data exposure.
* **Why it matters:** Defense-in-depth. A strong default ("deny by default") catches forgotten attributes.
* **Recommended direction:** Return an `AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build()` as fallback.

#### M-2 — `TestWebApplicationFactory` deliberately omits production interceptors

* **ID:** M-2
* **Severity:** MEDIUM
* **Title:** InMemory test suite omits `TenantInterceptor` and `AuditableEntityInterceptor`.
* **Location:** `tests/Centerix.SecurityTests/TestWebApplicationFactory.cs`.
* **Evidence:** `TestWebApplicationFactory` uses InMemory provider and explicitly excludes the interceptors to avoid deadlocks.
* **Problem:** Tests that rely on `TenantInterceptor` to stamp `TenantId` (and `AuditableEntityInterceptor` to stamp audit columns) effectively test a different write path than production.
* **Impact:** A regression in the interceptor logic would not be caught by InMemory tests. SQL Server tests mitigate this for some flows, but not all.
* **Why it matters:** Tests should validate the production code path, not a simplified variant.
* **Recommended direction:** Either keep using SQL Server Testcontainers for tests that depend on interceptors, or refactor the interceptors to be safely no-op-able on InMemory.

#### M-3 — No email confirmation requirement

* **ID:** M-3
* **Severity:** MEDIUM
* **Title:** `RequireConfirmedAccount = false`.
* **Location:** `src/Centerix.Infrastructure/DependencyInjection.cs`.
* **Evidence:** IdentityCore options set `RequireConfirmedAccount = false`.
* **Problem:** Newly invited users can log in without verifying their email.
* **Impact:** Account takeover risk if invitation tokens are leaked or guessed.
* **Why it matters:** Email verification is the simplest mitigation against invitation-token leakage.
* **Recommended direction:** Enable `RequireConfirmedAccount` once email delivery is integrated.

#### M-4 — No JWT revocation

* **ID:** M-4
* **Severity:** MEDIUM
* **Title:** Access tokens cannot be revoked before expiry.
* **Location:** `JwtTokenService` + authentication pipeline.
* **Evidence:** No revocation list, no `jti` deny-list observed.
* **Problem:** Logout revokes the refresh token but the access token remains valid until expiry.
* **Impact:** Window of vulnerability after logout or after permission changes.
* **Why it matters:** For a multi-tenant platform, immediate revocation on permission change is important.
* **Recommended direction:** Implement a short-lived `jti` cache (Hybrid cache works) and check on each request.

#### M-5 — No health checks

* **ID:** M-5
* **Severity:** MEDIUM
* **Title:** No `/health` endpoint registered.
* **Location:** `src/Centerix.API/Program.cs` (absence).
* **Evidence:** No `AddHealthChecks()` or `MapHealthChecks()` calls.
* **Problem:** Load balancers, orchestrators, and uptime monitors cannot probe application health.
* **Impact:** Production deployments lack basic observability for liveness/readiness.
* **Why it matters:** Standard production prerequisite.
* **Recommended direction:** Add `AddHealthChecks().AddDbContextCheck<AppDbContext>()` and map `/health/live` and `/health/ready`.

#### M-6 — Production seeding gated only on `IsDevelopment()`

* **ID:** M-6
* **Severity:** MEDIUM
* **Title:** Database initialisation is dev-only.
* **Location:** `src/Centerix.API/Program.cs` — `InitialiseDatabaseAsync` / `InitialiseTenantDatabaseAsync` only when `IsDevelopment()`.
* **Evidence:** Explicit `if (app.Environment.IsDevelopment())` guard.
* **Problem:** There is no migration / startup task for non-dev environments.
* **Impact:** Production deployments will not have migrations applied unless an external process does it.
* **Why it matters:** `dotnet ef database update` must be run as part of deployment. This is not automated.
* **Recommended direction:** Add a dedicated `db-migrate` startup task or rely on a separate migration step in deployment.

#### M-7 — Rate limiting only on `/auth/login`

* **ID:** M-7
* **Severity:** MEDIUM
* **Title:** No rate limiting outside `/auth/login`.
* **Location:** `src/Centerix.API/DependencyInjection.cs`.
* **Evidence:** Only `LoginPolicy` is registered.
* **Problem:** Other endpoints (refresh, invitations, expensive queries) are not rate-limited.
* **Impact:** DoS, brute force, and enumeration are easier.
* **Why it matters:** Production hardening requires broader rate limiting.
* **Recommended direction:** Add policies for `/auth/refresh`, expensive reads, and write endpoints.

### LOW

#### L-1 — No CORS registered

* **ID:** L-1
* **Severity:** LOW
* **Title:** CORS policy is not registered.
* **Location:** `src/Centerix.API/Program.cs` (absence).
* **Evidence:** No `AddCors` / `UseCors` calls.
* **Problem:** If a browser client is intended, requests will fail CORS preflight.
* **Recommended direction:** Register a strict CORS policy once the frontend stack is decided.

#### L-2 — Possible stale or scratch test files

* **ID:** L-2
* **Severity:** LOW
* **Title:** Test project may contain scratch files.
* **Location:** `tests/Centerix.SecurityTests/`.
* **Evidence:** Files like `UnitTest1.cs` or scratch files may exist.
* **Recommended direction:** Remove scratch files and verify test list.

#### L-3 — Some controllers may lack DELETE endpoints

* **ID:** L-3
* **Severity:** LOW
* **Title:** Inconsistent HTTP verb coverage.
* **Location:** `src/Centerix.API/Controllers/*`.
* **Evidence:** Observed inconsistent coverage during inspection.
* **Recommended direction:** Verify each controller's verb coverage matches the API spec.

#### L-4 — `RefreshToken` is `IHasTenantId` and subject to global filter

* **ID:** L-4
* **Severity:** LOW
* **Title:** Global query filter on `RefreshToken` complicates revocation queries.
* **Location:** `src/Centerix.Domain/Authentication/RefreshToken.cs`.
* **Evidence:** `RefreshToken` implements `IHasTenantId`.
* **Problem:** Cross-tenant revocation queries (e.g., revoke a user's entire token family across all tenants) require explicit `IgnoreQueryFilters`.
* **Recommended direction:** Document and centralize revocation queries with `IgnoreQueryFilters`.

#### L-5 — Default connection string targets local SQL Server with dev credentials

* **ID:** L-5
* **Severity:** LOW
* **Title:** `ConnectionStrings:DefaultConnection` defaults to a local dev server.
* **Location:** `src/Centerix.API/appsettings.json`.
* **Evidence:** `localhost,14330` with `sa` credentials.
* **Problem:** Risk that a misconfigured deployment inherits a dev DB.
* **Recommended direction:** Replace with a placeholder that fails fast.

### INFO

* **I-1:** `Microsoft.OpenApi` pinned to `2.7.5` — good practice for known CVE.
* **I-2:** `ClockSkew = TimeSpan.Zero` is strict — strictest default; good.
* **I-3:** `TenantMembership` is intentionally not `IHasTenantId` — by design.
* **I-4:** No `.Wait()` / `.Result` observed — async discipline is good.

---

## 23. Remaining Work

> Ordered by priority.

### Security (do first)

1. **H-1** — Remove hardcoded `"Admin@123"`, generate random password at seed.
3. **H-2** — Validate JWT secret at startup; fail fast in non-dev.
4. **M-1** — Set fallback authorization policy.
5. **M-3** — Wire email confirmation once SMTP / provider exists.
6. **M-4** — Implement `jti` revocation cache.
7. **M-7** — Broaden rate limiting.

### Database / Deployment

8. **M-5** — Add health checks.
9. **M-6** — Add explicit migration step in deployment.
10. **L-5** — Replace dev connection string.

### Testing

11. **M-2** — Re-enable or compensate for interceptors in tests.
12. Add general unit tests for application handlers.
13. Add end-to-end tests for invitation flow and student lifecycle.

### Architecture

14. Thread `CancellationToken` through all MediatR handlers consistently.
15. Decide on rowversion coverage for all aggregates with concurrent edits.

### Production Readiness

16. **L-1** — Decide CORS posture based on target client.
17. Add Dockerfile + docker-compose for local validation.
18. Add CI workflow (lint + build + test).
19. Add structured tracing (OpenTelemetry).
20. Add metrics endpoint.

### Documentation

21. Document the security model in a `SECURITY.md`.
22. Document the deployment / secrets procedure.

---

## 24. Production Readiness

| Capability | Status | Required for production? |
| --- | --- | --- |
| HTTPS / TLS termination | Likely assumed at ingress | Yes |
| Authentication | IMPLEMENTED | Yes — H-2 must be fixed |
| Authorization | IMPLEMENTED (weak default) | Yes — M-1 must be fixed |
| Tenant isolation | IMPLEMENTED | Yes |
| Audit logging | IMPLEMENTED | Yes |
| Health checks | NOT IMPLEMENTED | Yes — M-5 |
| Rate limiting | PARTIAL | Yes — M-7 |
| Logging | IMPLEMENTED | Yes |
| Metrics | NOT IMPLEMENTED | Recommended |
| Tracing | NOT IMPLEMENTED | Recommended |
| Deployment automation | NOT IMPLEMENTED | Yes |
| Secrets management | PARTIAL (env vars expected) | Yes — H-2 |
| Email delivery | NOT IMPLEMENTED | Depends on product |
| Payments / billing | NOT IMPLEMENTED | Depends on product |
| File uploads | NOT IMPLEMENTED | Depends on product |
| Backups / DR | UNKNOWN | Yes (operations) |

**Production readiness verdict:** **NOT READY.** The architecture is sound, but H-1, H-2, M-1, M-5, M-6, and M-7 must be addressed before any production exposure. With those resolved, the platform would be ready for an internal beta; full production hardening (metrics, tracing, backups) still requires operational work.

---

## 25. Final Assessment

### What this project is

A **multi-tenant SaaS for educational center management** with a strong foundation: .NET 10, ASP.NET Core Identity, JWT + refresh tokens, Finbuckle multi-tenancy, EF Core, MediatR CQRS, FluentValidation, Mapster, Serilog. Built with clean architecture; three independent tenant-isolation layers; permission-based authorization resolved per request from the database.

### What is already working

* Tenant onboarding, isolation, expiry.
* Authentication and refresh-token rotation.
* Permission-based authorization with platform-admin bypass.
* Plan / feature / subscription / limit management.
* Student CRUD with concurrency and soft-delete.
* Audit columns on every write.
* Strong security test coverage for isolation and commercial gating.
* SQL Server integration tests using Testcontainers.

### What is incomplete

* Token revocation (access tokens).
* Email confirmation.
* Health checks.
* Production migration automation.
* Broader rate limiting.
* Background jobs.
* Email / billing integrations.
* Docker / CI/CD.
* Frontend (none in repo).

### What is technically weak

* Hardcoded root password literal (H-1).
* Empty JWT secret in committed config (H-2).
* Null authorization fallback policy (M-1).
* InMemory tests skip production interceptors (M-2).
* No rate limiting outside login (M-7).

### Biggest risks

1. **H-1 + H-2** — secrets committed / unconfigured.
2. **M-1** — accidental public endpoints.
3. **M-4** — cannot revoke access tokens.
4. **M-6** — production migrations not automated.

### Can it be considered production-ready?

**Not yet.** With the HIGH and a subset of MEDIUM findings fixed, it could host an internal beta. Full production readiness also requires operational tooling (health checks, metrics, deployment automation).

### What must be fixed before production

* H-1, H-2, M-1, M-5, M-6, M-7.

### What can safely wait

* L-1 (CORS) — depends on frontend.
* Email confirmation (M-3) — until SMTP is integrated.
* OpenTelemetry tracing — until observability tooling is selected.
* Background jobs — until a real need arises.

---

## Confidence & Limitations

**Fully verified (high confidence):**

* Architecture, layer dependencies, project structure.
* Technology stack — derived from `Directory.Packages.props` and `csproj` files.
* Tenant isolation model — verified by reading middleware, `AppDbContext`, and interceptors.
* Authentication & refresh token flow — verified by reading `JwtTokenService` and `RefreshToken` entities.
* Permission authorization model — verified by reading `PermissionPolicyProvider` and `PermissionAuthorizationHandler`.
* HIGH findings H-1 and H-2 — verified by direct file inspection.
* MEDIUM findings M-1 through M-7 — verified by direct file inspection.
* SQL Server schema invariants — verified by reading migrations and `Phase2SqlServerTests.cs`.
* Test project structure and the Testcontainers-based factory.

**Partially verified (medium confidence):**

* Endpoint surface — sampled from controllers; not exhaustively listed.
* Production behavior of interceptors — verified by reading code and tests, not by running the application.
* `LimitService` semantics — verified by reading the service; transactional edge cases not exhaustively traced.
* Email / billing / file-upload integrations — not observed in code, marked `UNKNOWN`.

**Could not be verified:**

* Runtime behavior (the project was not built or run during this audit).
* Actual EF migration history vs. current schema (no DB access).
* Performance characteristics under load.
* Production environment / secrets handling.
* Code coverage metrics (no coverage report available).
* Whether there are additional projects outside the solution file.

**Assumptions made:**

* The solution file `Centerix.slnx` is the complete set of source projects.
* `Directory.Build.props` and `Directory.Packages.props` apply to all projects uniformly.
* The `Centerix.Domain` project does not depend on infrastructure (verified by inspection; final confirmation would require a `dotnet list package` run).
* `appsettings.Development.json` overrides are non-secret (not inspected deeply; per audit rules secrets are intentionally not exposed).

**Audit constraints honored:**

* No source code was modified.
* No migrations were generated.
* No fixes were attempted.
* No tests were run.
* This document is the sole deliverable.

---

*End of audit.*