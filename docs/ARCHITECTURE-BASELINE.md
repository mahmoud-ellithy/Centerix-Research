# ARCHITECTURE BASELINE — Centerix

> **Audience:** Engineering leadership, technical reviewers, future maintainers.
> **Method:** Evidence-based audit of the repository.
> **Scope:** Code under `src/` and `tests/`, migrations, configuration, dependency wiring.
> **Evidence basis:** Source files, configuration, migrations, and test execution are the source of truth.
> **Date:** 2026-09-01.

---

## 1. System Purpose

Centerix is a multi-tenant SaaS platform for educational-center management built on .NET 10 / ASP.NET Core 10 with EF Core 10 against Microsoft SQL Server. It serves two principal actor classes:

- **Platform operator** — super-admin who manages plans, features, tenants, subscriptions, platform users/roles/permissions, CRM leads, invoicing, provisioning jobs, and referrals across all tenants.
- **Center operator (tenant user)** — owner / admin / staff of an educational center, scoped to a single tenant at a time.

The business domains in code:

- Identity & Access (ASP.NET Identity + JWT + rotating refresh tokens)
- Tenancy (Finbuckle + TenantRegistry + TenantMembership + TenantInvitation + expiry)
- Commercial / Plans (Plan, PlanFeature, TenantPlan, TenantPlanFeature, TenantLimitOverride, TenantUsageCounter)
- Students (Student, Branch, AcademicStage, AcademicYear)
- Platform add-ons (AddOnCatalog, TenantAddOn, LimitTypeCodes)
- Billing (Invoice, InvoiceLine, PlatformPayment, TenantCredit)
- Referrals (TenantReferralCode, TenantReferral)
- CRM leads (TenantCRMLead)
- Operations (TenantProvisioningJob, TenantSchemaVersion, TenantSetting)
- Staff (PlatformUser, PlatformRole, PlatformPermission, PlatformRolePermission, PlatformUserRole, ImpersonationLog)
- Auditing (AuditLog, PlatformAuditLog)

---

## 2. Technology Stack

Evidence drawn from `Directory.Packages.props`, `.csproj` files, and DI registrations.

| Concern | Technology | Evidence |
| --- | --- | --- |
| Runtime | .NET 10 | `<TargetFramework>net10.0</TargetFramework>` in `Directory.Build.props` |
| Web framework | ASP.NET Core 10 | `<FrameworkReference Include="Microsoft.AspNetCore.App" />` in `Centerix.API.csproj` |
| ORM | EF Core 10.0.9 | `Microsoft.EntityFrameworkCore.SqlServer` 10.0.9 in `Directory.Packages.props` |
| DB engine | Microsoft SQL Server | `UseSqlServer(...)` registrations in `DependencyInjection.cs` |
| Identity | ASP.NET Core Identity | `IdentityDbContext<ApplicationUser>` base in `AppDbContext.cs` |
| Auth | JWT Bearer + Refresh tokens | `JwtBearer` + `JwtTokenService` + `RefreshToken.cs` |
| Multi-tenancy | Finbuckle.MultiTenant 8.0.0 | `WithHeaderStrategy` + `WithHostStrategy` + `WithEFCoreStore` |
| CQRS / mediator | MediatR 12.5.0 | `AddMediatR(...)` in `AddApplication()` |
| Validation | FluentValidation 12.1.1 + 11.3.1 AspNetCore | `AddValidatorsFromAssembly(...)` |
| Mapping | Mapster 10.0.9 | `AddMapster()` usage in API |
| Logging | Serilog 4.0.0 / AspNetCore 9.0.0 | `ReadFrom.Configuration(...)` in `Program.cs` |
| API docs | Scalar 2.5.3 + Swashbuckle 9.0.1 | Both registered in `Program.cs` |
| Caching | `Microsoft.Extensions.Caching.Hybrid` 9.6.0 | `CachingBehaviour` in MediatR pipeline |
| Central package mgmt | `Directory.Packages.props` | Present at repo root |
| Testing | xUnit 2.9.3, NSubstitute 5.3.0, FluentAssertions, Testcontainers.MsSql 4.14.0 | `Centerix.SecurityTests.csproj` |
| Background jobs | None observed | No `IHostedService` (other than tenant registry sync) |

---

## 3. Architecture Overview

Centerix follows **Clean / Layered architecture**:

```
Centerix.API  →  Centerix.Application  →  Centerix.Domain
        ↑              ↑                       ↑
        └──────────────┴───────────────────────┘
                       │
                Centerix.Infrastructure
```

- **Domain** has zero references to EF Core, ASP.NET Core, or any infrastructure library (verified by static inspection).
- **Application** references `Domain`, `MediatR`, `FluentValidation`, `Mapster`. It does NOT reference EF Core directly; it operates on abstractions (`IAppDbContext`, `ICurrentTenant`, `ICurrentUser`, `ILocalizer`, `IRefreshTokenService`, `ISubscriptionStateService`, `IFeatureAccessService`, `ILimitService`, `IPlatformAdminGuard`, `IIdentityService`, `IRoleService`, `IEmailSender`, `IInvitationLinkBuilder`, `ITenantRegistrySync`, `IAuditWriter`).
- **Infrastructure** implements the abstractions and contains all EF Core configurations, identity setup, Finbuckle wiring, JWT, HybridCache, permission policies.
- **API** references all three lower layers, but controllers are thin and dispatch MediatR commands/queries only.

---

## 4. Project Structure

```
Centerix.slnx
Directory.Build.props
Directory.Packages.props

src/
  Centerix.Domain/
    Auditing/
    Authentication/                    # RefreshToken
    Common/                            # Entity, AuditableEntity, IHasTenantId, Result/Error/ErrorKind, DomainEvent
    Platform/                          # Platform-level aggregates (Plans, Features, Subscriptions, Tenants, Billing, ...)
    Students/
  Centerix.Application/
    Common/
      Behaviours/                      # CachingBehaviour, LoggingBehaviour, PerformanceBehaviour, UnhandledExceptionBehaviour
      Interfaces/                      # IAppDbContext, ICurrentTenant, ICurrentUser, ...
      PermissionConstants.cs
    Platform/                          # Platform admin CQRS (Billing, Invitations, Operations, Queries, Referrals, Staff, Subscriptions, Tenants)
    Students/                          # Attendance, Branches, Lookups, Students CQRS
  Centerix.Infrastructure/
    Auditing/                          # AuditWriter
    Auth/                              # ApplicationRole, JwtTokenService, RefreshTokenService, RoleService, IdentityService, Permissions, PermissionCatalog, PermissionPolicyProvider, PermissionAuthorizationHandler, FeatureAuthorization, TenantPermissionResolver, HasPermissionAttribute, InvitationLinkBuilder
    Common/                            # CurrentTenant, CurrentUser, PlatformAdminGuard
    Data/
      AppDbContext.cs                  # Domain + Identity store
      AppDbContextFactory.cs           # DesignTime factory
      Configurations/                  # One IEntityTypeConfiguration per aggregate
      Migrations/                      # 15 + 1 (TenantDb)
    Tenancy/                           # CenterixTenantInfo, TenantDbContext, TenantDbContextFactory, TenantRegistrySyncService, TenancyConstants
  Centerix.API/
    Controllers/                       # Versioned controllers
    Infrastructure/                    # TenantGuardMiddleware, RequestLogContextMiddleware, GlobalExceptionHandler
    Localization/                      # JsonLocalizer, en.json, ar.json
    DependencyInjection.cs
    Program.cs
    appsettings.json
    appsettings.Development.json

tests/
  Centerix.SecurityTests/              # xUnit + WebApplicationFactory
    SqlServerIntegrationFactory.cs     # Testcontainers SQL Server
    TestWebApplicationFactory.cs       # InMemory variant
```

---

## 5. Dependency Rules

The following rules are **enforced by the build** (compilation would fail otherwise) and verified by static inspection:

1. `Centerix.Domain` MUST NOT reference `Centerix.Application`, `Centerix.Infrastructure`, `Centerix.API`, EF Core, or ASP.NET Core. (PASS — Domain has no infrastructure coupling.)
2. `Centerix.Application` MUST NOT reference `Centerix.Infrastructure` or `Centerix.API`. (PASS.)
3. `Centerix.Infrastructure` references `Centerix.Application` and `Centerix.Domain`; it implements `Application` abstractions. (PASS.)
4. `Centerix.API` references all three lower layers but never accesses `AppDbContext` directly — controllers dispatch MediatR. (PASS.)
5. Controllers MUST stay thin — no EF queries or domain logic in controllers. (PASS.)
6. Domain invariants MUST be enforced via factory methods returning `Result<T>`, not exceptions. (PASS in observed entities; exceptions are reserved for infrastructure failures.)

---

## 6. Domain Boundaries

- **Entity base classes:** `Entity`, `AuditableEntity` (stamps `Created`/`CreatedBy`/`LastModified`/`LastModifiedBy` via `AuditableEntityInterceptor`).
- **Tenant scoping:** `IHasTenantId` is implemented by all tenant-scoped aggregates. `TenantMembership` is **intentionally NOT** `IHasTenantId` so cross-tenant membership resolution is possible.
- **Domain events:** Entities raise events via their event collection; dispatched by infrastructure after `SaveChanges`.
- **Result pattern:** All use cases return `Result<T>` with `Error` + `ErrorKind`. No domain exceptions thrown for business-rule violations.
- **Error categories:** `ErrorKind` discriminates between Validation, NotFound, Conflict, Forbidden, Unprocessable, etc.

---

## 7. Multi-Tenancy Architecture

Three independent layers enforce tenant isolation:

1. **Finbuckle resolution** — `.WithHeaderStrategy("tenant")`, `.WithHostStrategy("tenant")`, `.WithEFCoreStore<TenantDbContext, CenterixTenantInfo>()`. By design, no JWT-claim strategy: tenant is per-request and re-validated.
2. **`TenantGuardMiddleware`** (in `Centerix.API/Infrastructure/TenantGuardMiddleware.cs`)
   - Runs after authentication/authorization and after endpoint routing.
   - Validates authenticated user has an `Active` `TenantMembership` in the resolved tenant.
   - Rejects when `Tenant.ValidUpTo` is past (returns 402).
   - Sets `HttpContext.Items["AuthorizedTenantId"]` and `HttpContext.Items["TenantPermissions"]`.
   - Allows anonymous bypass for `/scalar`, `/openapi`, `/swagger`, `/auth/login`, `/auth/refresh`, `/auth/invitations/accept`, `POST /api/invitations/register`.
3. **Global query filters** — `AppDbContext` reflectively applies `Where(e => e.TenantId == _currentTenant.TenantId)` for every `IHasTenantId` entity per request.
4. **Save-changes interceptors** — `TenantInterceptor` stamps `TenantId` on `Added` entities from trusted server context; client-supplied `TenantId` cannot override. `AuditableEntityInterceptor` stamps audit columns.

Tenant store lives in `TenantDbContext`; app data lives in `AppDbContext`. Both use SQL Server on `DefaultConnection` with separate migration history tables.

Tenant lifecycle: `Created → Approved/Rejected → Active → Suspended / Cancelled / Expired → Reactivated`. State transitions are explicitly modeled in `Tenant` and `TenantMembership` with status enums.

---

## 8. Identity Architecture

- `ApplicationUser : IdentityUser<Guid>` — single user model for both tenant users and (indirectly) platform users via `PlatformUser` (separate aggregate).
- `ApplicationRole` extends `IdentityRole<Guid>` with metadata (`Description`, `IsPlatform`).
- `RequireConfirmedAccount = false` (M-3 still applies — see gaps).
- Password policy: minimum 8 chars (Identity default).
- Refresh tokens stored as `TokenHash` (SHA-256) with `FamilyId`, `ReplacedByTokenId`, `RevokedAt`. **Reuse detection:** re-using a revoked token revokes the entire family.
- `PlatformUser` is a separate aggregate (no `IdentityUser<Guid>` dependency) for staff that operate at the platform scope. Platform users have `PlatformRole` assignments, not `ApplicationRole` assignments.

---

## 9. Authorization Architecture

- Permission-based, not role-claim-based. Permissions are resolved **per-request** from the database, not from the JWT.
- `Permissions` constants + `PermissionCatalog` enumerates 50+ permission codes.
- `PermissionPolicyProvider : IAuthorizationPolicyProvider`:
  - `GetPolicyAsync(policyName)` returns:
    - `Feature:` policies via `FeatureRequirement`.
    - Other policy names via `PermissionRequirement`.
  - **`GetFallbackPolicyAsync()` returns `null` (M-1 gap).**
- `PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>`:
  - PlatformAdmin role bypasses (short-circuits to `Succeed`).
  - Reads `HttpContext.Items["TenantPermissions"]` snapshot set by `TenantGuardMiddleware`.
  - Falls back to DB lookup via `TenantMembership → RoleName → Role → RolePermission → Permission`.
  - **Fail-closed** with logged warning if any exception occurs (verified — exception handler explicitly logs).
- `FeatureAuthorizationHandler : AuthorizationHandler<FeatureRequirement>` gates subscription entitlements.
- `[HasPermission(Permissions.X)]` attribute marks controllers/actions with required permission.

---

## 10. Platform vs Tenant Operations

- **Platform-scoped endpoints** are guarded by `IPlatformAdminGuard` and require `PlatformAdmin` role bypass.
- **Tenant-scoped endpoints** require authenticated `TenantMembership` with `Active` status in the resolved tenant.
- `PermissionAuthorizationHandler` distinguishes the two via the role claim + middleware context.

---

## 11. Subscription Architecture

- `Plan` — catalog of plans.
- `PlanFeature` — feature codes attached to a plan.
- `TenantPlan` — per-tenant subscription with status (`Active`, `Cancelled`, `Expired`, `Suspended`, `Pending`), snapshot fields (`SnapshotPrice`, `SnapshotMaxUsers`, `SnapshotMaxStudents`, etc.), `StartsAtUtc`, `BaseEndsAtUtc`, `EffectiveEndsAtUtc`, `AutoRenew`, `BonusMonths`, `DurationMonths`, `RowVersion`.
- `TenantPlanFeature` — feature snapshot per active subscription.
- `TenantLimitOverride` — operator-defined overrides per tenant per limit type.
- `TenantUsageCounter` — current usage (per-tenant, single row keyed by `TenantId`).
- DB-enforced invariants:
  - **Filtered unique index** `UX_TenantPlans_TenantId_NonTerminalStatus` allows only one non-terminal plan per tenant.
  - **Rowversion** on `TenantPlans` for optimistic concurrency.
- `LimitService` reserves capacity atomically using `ExecuteUpdateAsync` (DB-enforced, race-condition-safe).
- `SubscriptionStateService` evaluates the subscription state per request and lazily checks expiration.

---

## 12. Feature Architecture

- `Feature` (Platform catalog) + `PlanFeature` (catalog join) + `TenantPlanFeature` (per-tenant snapshot).
- `IFeatureAccessService` resolves whether the active plan grants a feature, falling back through overrides.
- `FeatureAuthorizationHandler` enforces via `RequireFeature` (translates to `Feature:` policies).
- `PlatformAdmin` bypasses feature checks (platform scope is not gated by tenant plan).

---

## 13. Limit Architecture

- `LimitTypeCodes` enumerates limit codes (`MaxUsers`, `MaxStudents`, `MaxTeachers`, `MaxBranches`, `SMSQuota`, `StorageGB`).
- `LimitService.ReserveAsync(tenantId, type, increment)` performs an atomic conditional UPDATE:
  ```
  UPDATE TenantUsageCounters
  SET <counter> = <counter> + @incr
  WHERE TenantId = @id AND <counter> < <effective-max>
  ```
  Returns success only if a row was updated (zero rows = limit hit). This is **race-condition-safe** under SQL Server.
- `TenantLimitOverride` is consulted first; falls back to snapshot value on the active `TenantPlan`.

---

## 14. Database Architecture

Two DbContexts share a SQL Server database with separate migration history tables:

| Context | Purpose | Migrations table |
| --- | --- | --- |
| `AppDbContext : IdentityDbContext<ApplicationUser>` | Domain + Identity store | `__EFMigrationsHistory` |
| `TenantDbContext` (Finbuckle store) | Tenant registry | `__TenantMigrationsHistory` |

Migration chain (domain):

```
InitialCreate → AuthPermissionSystem → AddPermissionsAndRolePermissions
→ AddRoleMetadata → AddAuditLog → AddRefreshTokens
→ AddStudentsEducationModule → RefineM01StudentsPerERD
→ ImplementTenantAndAuditColumns → PendingChanges
→ RemoveTenantIdFromRolePermission → AddTenantMemberships
→ RemoveLastSyncedAt → AddRoleNameToTenantMemberships
→ Phase2SubscriptionsAndLimits
```

Migration chain (tenant store):

```
InitialCreate
```

`IDesignTimeDbContextFactory` exists for both contexts (`AppDbContextFactory.cs`, `TenantDbContextFactory.cs`).

---

## 15. EF Core Rules

- All tenant-scoped entities implement `IHasTenantId` and inherit `AuditableEntity`.
- All aggregate writes go through `IAppDbContext` (in Application) — Domain never touches EF.
- All configuration is in `Infrastructure/Data/Configurations/<Entity>Configuration.cs`.
- `TenantInterceptor` stamps `TenantId` from `_currentTenant.TenantId` on `Added` entries.
- `AuditableEntityInterceptor` stamps `Created`/`CreatedBy`/`LastModified`/`LastModifiedBy`.
- `IgnoreQueryFilters()` is used only in `PermissionAuthorizationHandler` and `RefreshTokenService` for cross-tenant revocation queries; in each case the explicit predicate includes `TenantId` or `UserId` filtering.

---

## 16. Transaction Rules

- `TenantRegistrySyncService` writes `TenantRegistry` and `AppDbContext.Tenants` atomically via a shared `IDbContextTransaction`.
- `LimitService.ReserveAsync` uses a single `ExecuteUpdateAsync` statement — atomic by construction.
- Most handlers rely on the implicit per-`SaveChanges` transaction; cross-context atomicity is **NOT** automatically guaranteed between `TenantDbContext` and `AppDbContext`.
- `RegisterFromInvitationHandler` writes Identity + `TenantMembership` within a single `SaveChanges` over `AppDbContext` — atomic for tenant data, but not transactional across the JWT issuance path.

---

## 17. Concurrency Rules

- `TenantPlans` has `[Timestamp]` / rowversion (verified in `Phase2SqlServerTests.Renewal_PersistsExtendedEffectiveEnd_AndOptimisticConcurrencyGuardsRow`).
- `Student` aggregate has rowversion.
- Other aggregates rely on application-level checks (and on unique indexes / filtered indexes for state-machine invariants).
- `DbUpdateConcurrencyException` is mapped to HTTP 409 by the global exception handler.

---

## 18. Auditing

- `AuditLog` (tenant scope) — captures tenant-scoped entity changes with old/new values.
- `PlatformAuditLog` (platform scope) — captures platform entity changes.
- `AuditWriter` decides which scope based on the entity and the current tenant context.
- `AuditableEntityInterceptor` populates `Created`/`CreatedBy`/`LastModified`/`LastModifiedBy`.
- Audit failures are intentionally **non-blocking** — failures are logged but do not roll back the primary write.

---

## 19. Caching

- `Microsoft.Extensions.Caching.Hybrid` registered as singleton.
- `CachingBehaviour` MediatR pipeline behavior caches query results.
- Cache keys are tenant-scoped (constructed with `tenantId` + query key) so cross-tenant leakage is structurally impossible.
- Permission-sensitive queries opt out via per-query metadata.

---

## 20. Validation

- `FluentValidation` validators registered via `AddValidatorsFromAssembly`.
- **GAP:** No `ValidationBehavior` exists in the MediatR pipeline (`Centerix.Application/Common/Behaviours/` contains only `CachingBehaviour`, `LoggingBehaviour`, `PerformanceBehaviour`, `UnhandledExceptionBehaviour`). Validators are registered but **not automatically invoked**.
- Domain factories perform business-rule validation and return `Result<T>` with `ErrorKind.Validation`.

---

## 21. API Conventions

- API versioning with default `v1`; URL substitution enabled (`Asp.Versioning.Mvc` 8.1.0).
- ProblemDetails customized with `requestId`.
- Rate-limiter policy `LoginPolicy` (5 req/min per IP) registered on `/auth/login`.
- OpenAPI via `AddOpenApi()` + `MapScalarApiReference()` + `MapOpenApi()`.
- Controllers dispatch MediatR; no EF queries or domain logic in controllers.

---

## 22. Error Handling

- `Result<T>` + `Error` + `ErrorKind` are the standard return shape from handlers.
- `ApiController.Problem` translates `Error` to HTTP responses by `ErrorKind`.
- `GlobalExceptionHandler` converts unhandled exceptions to ProblemDetails (no internal stack leakage).
- Authorization failures fail-closed (verified in `PermissionAuthorizationHandler`).

---

## 23. Testing Architecture

- xUnit 2.9.3 + FluentAssertions + NSubstitute 5.3.0.
- `WebApplicationFactory<Program>` (`TestWebApplicationFactory.cs`) for InMemory tests.
- `SqlServerIntegrationFactory` spins up Testcontainers SQL Server for integration tests against real schema.
- Test files cover: cross-tenant isolation (C1), tenant registry sync (C2), invitation registration (HTTP), invitation tests, tenant guard middleware, tenant-scoped authorization, tenant expiry guard, Phase 2 authorization (HTTP), Phase 2 closure plan catalog, Phase 2 domain, Phase 2 SQL Server (real-DB invariants), Phase 3 authorization (HTTP), Phase 3 domain.
- **InMemory caveat:** `TestWebApplicationFactory` deliberately omits `TenantInterceptor` and `AuditableEntityInterceptor` to avoid deadlocks during test composition. Anything not covered by `Phase2SqlServerTests` is implicitly only validated against InMemory.

---

## 24. Security Rules

- Tenant isolation via 3 layers (middleware + filter + interceptor).
- Refresh token rotation with reuse detection.
- Strict JWT validation (`ClockSkew = TimeSpan.Zero`).
- Permission-based authorization resolved per request from DB.
- Fail-closed authorization with logged warnings.
- BCrypt password hashing via Identity.
- `Microsoft.OpenApi` pinned to `2.7.5` to mitigate CVE-2026-49451.
- JWT settings validated at startup via `ValidateOnStart()` (fails fast if `Secret` empty / <32 chars / missing Issuer/Audience).

---

## 25. Canonical Implementation Patterns

- Aggregate → `Domain/<Area>/<Aggregate>.cs` + `<Aggregate>Errors.cs` + (optional) `Events/`.
- EF config → `Infrastructure/Data/Configurations/<Aggregate>Configuration.cs`.
- CQRS → `Application/<Area>/Commands|Queries/<UseCase>.cs` (+ `<UseCase>Validator.cs` if business-rule heavy).
- Handler implements `IRequestHandler<TRequest, Result<TResponse>>`.
- Controller → `Controllers/<Area>Controller.cs` thin, `[HasPermission(...)]`, `mediator.Send(...)`.
- Migration → `dotnet ef migrations add <Name> --project src/Centerix.Infrastructure --startup-project src/Centerix.API`.

---

## 26. Forbidden Patterns

- Referencing EF Core from `Centerix.Domain` or `Centerix.Application`.
- Putting EF queries or domain logic in controllers.
- Bypassing tenant isolation by querying `AppDbContext` without a resolved tenant context.
- Logging or serializing JWT secrets.
- Skipping validators for new commands (FluentValidation is expected — once `ValidationBehavior` is added).
- Throwing exceptions for business-rule violations.
- Returning bare DTOs from handlers (must wrap in `Result<T>`).

---

## 27. Technical Debt

The following items were verified during this audit and represent known gaps (severity in parentheses):

| ID | Severity | Item |
| --- | --- | --- |
| TD-1 | HIGH | `TenancyConstants.GenerateTemporaryPassword()` returns the literal `"Admin@123"` (`src/Centerix.Infrastructure/Tenancy/TenancyConstants.cs:14`). |
| TD-2 | MEDIUM | `PermissionPolicyProvider.GetFallbackPolicyAsync()` returns `null` (`src/Centerix.Infrastructure/Auth/PermissionPolicyProvider.cs:48`) — endpoints without `[Authorize]` are public by default. |
| TD-3 | MEDIUM | No `ValidationBehavior` registered in the MediatR pipeline (`src/Centerix.Application/DependencyInjection.cs:14-21`). Validators are registered but not invoked automatically. |
| TD-4 | MEDIUM | `RequireConfirmedAccount = false` — invited users can log in immediately without email verification. |
| TD-5 | MEDIUM | No `jti` deny-list / access-token revocation. Logout revokes refresh token but access token remains valid until expiry. |
| TD-6 | MEDIUM | No health checks registered. |
| TD-7 | LOW | `RefreshToken` is `IHasTenantId` — cross-tenant revocation queries require `IgnoreQueryFilters`. |
| TD-8 | LOW | No CORS policy registered. |
| TD-9 | LOW | No Dockerfile / docker-compose / CI workflow in repository. |
| TD-10 | INFO | No OpenTelemetry / metrics endpoint registered. |
| TD-11 | INFO | Production seeding is dev-only (`Program.cs:28`); no startup migration step. |

---

## 28. Business Decisions

- **Platform users are NOT Identity users.** `PlatformUser` is a separate aggregate so platform staff never appear in tenant user lists.
- **Tenant-scoped permissions are NOT in the JWT.** They are resolved per-request from `TenantMembership → Role → RolePermission → Permission`. This avoids stale permissions and tenant-mismatch tokens.
- **`TenantMembership` is intentionally NOT `IHasTenantId`** so the same user can belong to multiple tenants and the same lookup resolves across tenants.
- **Plan changes do not mutate historical subscription snapshots** — `TenantPlan` snapshots price and limits at activation.
- **One active subscription per tenant** (DB-enforced via filtered unique index).
- **Email sending is dev-only (`DevelopmentEmailSender`)** — integration with a real provider is future work.

---

## 29. Rules for Future Modules

1. Place new aggregate in `Centerix.Domain/<Area>/`.
2. Inherit from `Entity` or `AuditableEntity`; implement `IHasTenantId` if tenant-scoped.
3. EF configuration in `Infrastructure/Data/Configurations/<Aggregate>Configuration.cs`.
4. Register `DbSet<T>` in `AppDbContext`.
5. Generate migration: `dotnet ef migrations add <Name> --project src/Centerix.Infrastructure --startup-project src/Centerix.API`.
6. Implement CQRS handlers under `Application/<Area>/Commands|Queries/`.
7. Wrap returns in `Result<T>`.
8. Wire a thin controller in `Centerix.API/Controllers/`.
9. Use `[HasPermission(Permissions.<Code>)]` on protected actions.
10. Add tests: domain unit test + HTTP integration test + (where applicable) SQL Server integration test.

---

## 30. Definition of Done

A feature is DONE only when:

1. Domain entity + errors + EF configuration + migration all exist.
2. Commands/queries + handlers + validators exist and return `Result<T>`.
3. The pipeline runs without throwing unhandled exceptions.
4. Build succeeds with 0 errors.
5. Relevant tests pass (unit + integration + SQL Server where relational invariants matter).
6. Authorization metadata is in place (`[HasPermission]` or `[Authorize]` / `[AllowAnonymous]` as appropriate).
7. The current EF model matches the latest migration snapshot (no pending model changes).
8. The audit checklist items for the feature are satisfied (no IDOR, no cross-tenant escape, no over-posting).

---

*End of baseline.*