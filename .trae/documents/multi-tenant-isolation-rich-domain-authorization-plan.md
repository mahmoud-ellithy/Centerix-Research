# Plan: Tenant Query Filter, Rich Domain, and Enforced Isolation + Authorization

## Summary
Centerix is a multi-tenant SaaS built on Clean Architecture with Finbuckle multi-tenancy, MediatR, EF Core, and a Result pattern. Today the scaffolding exists but three production-critical concerns are not actually enforced:

1. **Tenant read isolation** — `TenantId` is stamped on write by `TenantInterceptor`, but there is **no global query filter**, so reads are not isolated. Callers must remember to add `.Where(x => x.TenantId == tenantId)`, and the `tenantId` comes from method arguments instead of the authenticated context.
2. **Rich domain** — entities are anemic get/set bags; the `DomainEvent` machinery on `Entity` is wired but never used; the MediatR pipeline (logging/performance/caching/validation) is registered but no command/query handler exists.
3. **Authorization** — JWT is configured but **no `[Authorize]`** exists anywhere; platform-admin data (Plans/Features/Tenants) is publicly reachable, and tenant-scoped data has no ownership enforcement.

This plan delivers, in order: (A) an ambient tenant/user context abstraction, (B) a global tenant query filter with an explicit platform-admin bypass, (C) enforced authorization with a `PlatformAdmin` role separated from tenant users, and (D) a rich-domain refactor plus one real CQRS vertical slice to justify the pipeline.

Per user decisions:
- **Admin scope**: Separate roles + bypass filter. Plans/Features are platform-global (no `TenantId`); a `PlatformAdmin` role manages them. Tenant-scoped entities (`TenantPlan`, `TenantBilling`, `TenantCRMLead`, `PlatformAuditLog`) get the global filter and derive `TenantId` from the ambient context.
- **Rich domain**: Full rich domain (encapsulated setters, factory/update methods, domain events) **plus** one complete MediatR command/query vertical slice.

## Current State Analysis
- Entry / composition:
  - `src/Centerix.API/Program.cs` — pipeline order; `UseMultiTenant()` runs after `UseSerilogRequestLogging` and before auth.
  - `src/Centerix.API/DependencyInjection.cs` (`AddPresentation`, `UseCoreMiddlewares`) — no authorization policies; `AddControllers` has no global auth filter.
  - `src/Centerix.Infrastructure/DependencyInjection.cs` — registers interceptors, `AppDbContext`, Finbuckle (`Header`/`Host`/`Claim` strategies keyed by `TenancyConstants.TenantIdName = "tenant"`), Identity (`IdentityUser`/`IdentityRole`), JWT, HybridCache.
  - `src/Centerix.Application/DependencyInjection.cs` — MediatR + 4 behaviours + FluentValidation validators, all currently unused by controllers.
- Multi-tenancy:
  - `TenantInterceptor` sets `TenantId` on `Added` entries implementing `IHasTenantId` from `IMultiTenantContextAccessor<CenterixTenantInfo>`.
  - `AppDbContext.OnModelCreating` only calls `ApplyConfigurationsFromAssembly`; **no `HasQueryFilter`**.
  - Configurations set `TenantId` as a property only (`TenantPlanConfiguration`, `TenantBillingConfiguration`, `TenantCRMLeadConfiguration`, `PlatformAuditLogConfiguration`); Plans/Features have no `TenantId`.
  - `CenterixTenantInfo` carries `IsActive` and `ValidUpTo` but nothing enforces them per request.
- Domain:
  - `Entity` has `DomainEvents`/`AddDomainEvent`/`ClearDomainEvents` (unused). `AuditableEntity<TId>` exposes `Id` (private setter) and `TenantId` (public setter, from `IHasTenantId`).
  - `Plan`, `TenantPlan`, `TenantBilling`, `TenantCRMLead` are all public get/set with no behavior. `AppDbContext.SaveChangesAsync` already dispatches domain events via MediatR.
- Services / API:
  - `PlatformService` depends on concrete `AppDbContext` (not `IAppDbContext`), uses `DateTime.UtcNow` directly (has `TimeProvider` available), and takes `tenantId` as a method arg for tenant-scoped reads.
  - `PlansController` correctly uses the `Result.Match` + `Problem` pattern; `TenantsController` returns raw `Ok/NotFound`.
  - `ApiController.Problem` maps `ErrorKind.Unauthorized` to `403` (should be `401`); `ErrorKind.Forbidden` exists in `Error` but is not mapped.
- Config / build:
  - JWT `Secret` committed in `appsettings.json`.
  - `Directory.Build.props` targets `net9.0` while packages are `10.0.9` (out of scope here but noted).
- Tests: only placeholder `UnitTest1.cs`; Testcontainers/xUnit/NSubstitute referenced.

## Proposed Changes

### Phase A — Ambient Tenant & User Context (foundation for filter, audit, authorization)
1. **Application: define abstractions** (`src/Centerix.Application/Common/Interfaces/`)
   - `ICurrentUser` — `UserId`, `UserName`, `IsAuthenticated`, `IsPlatformAdmin`, `Roles`.
   - `ICurrentTenant` — `TenantId`, `IsResolved`, `IsActive`, `ValidUpTo`.
   - Why: Domain/Application must not depend on Finbuckle or `HttpContext`; the query filter, audit interceptor, and handlers consume these instead of concrete accessors.
2. **Infrastructure: implement abstractions** (`src/Centerix.Infrastructure/Tenancy/` or a new `Common/`)
   - `CurrentTenant` — adapts `IMultiTenantContextAccessor<CenterixTenantInfo>`.
   - `CurrentUser` — adapts `IHttpContextAccessor` (claims: `NameIdentifier`, `Name`, roles). Register `AddHttpContextAccessor()`.
   - Register both scoped in `Infrastructure/DependencyInjection.cs`.
   - Why: Provides the single source of truth for who/what tenant is executing.

### Phase B — Global Tenant Query Filter + Correct Write Stamping
3. **AppDbContext: apply global query filter** (`src/Centerix.Infrastructure/Data/AppDbContext.cs`)
   - Inject `ICurrentTenant` (or the multitenant accessor) into `AppDbContext`.
   - In `OnModelCreating`, after `ApplyConfigurationsFromAssembly`, iterate the model and add a filter `e => e.TenantId == currentTenantId` for every entity type implementing `IHasTenantId` (generic helper to build the lambda), or add it explicitly in each tenant-scoped `IEntityTypeConfiguration`.
   - Capture the tenant id via a field read at query time (EF evaluates the filter expression against the current field value) so it reflects the resolved tenant per scoped context instance.
   - Why: Enforces isolation at the data layer so no caller can accidentally read another tenant's rows.
4. **PlatformService: remove manual tenant `Where` and arg-based tenantId** (`src/Centerix.Infrastructure/Platform/PlatformService.cs`)
   - Once the filter is in place, drop `.Where(x => x.TenantId == tenantId)` for `TenantPlan`/`TenantBilling`/`TenantCRMLead` and derive tenant from context instead of the `tenantId` parameter (update `IPlatformService` signatures accordingly, and their controllers `TenantPlansController`, `TenantBillingsController`, `TenantCRMLeadsController`).
   - Replace `DateTime.UtcNow` with injected `TimeProvider`.
   - Switch dependency from concrete `AppDbContext` to `IAppDbContext` (extend `IAppDbContext` to expose the needed `DbSet`s) for testability.
   - Why: Removes duplicated/forgettable isolation logic and the risk that a caller passes a spoofed `tenantId`.
5. **Tenant activation/expiry guard** (new middleware in `src/Centerix.API/Infrastructure/`, wired in `UseCoreMiddlewares` after `UseMultiTenant()`)
   - When a tenant is resolved but `IsActive == false` or `ValidUpTo < now`, short-circuit with `403`/`402` problem details. Skip for platform-admin/root routes.
   - Why: `IsActive`/`ValidUpTo` are stored but never enforced.

### Phase C — Enforced Authorization (separate platform-admin from tenant users)
6. **Define roles & policies** (`src/Centerix.Infrastructure/DependencyInjection.cs` + a constants file)
   - Add role constants (e.g., `PlatformAdmin`, `TenantAdmin`, `TenantUser`) in a `Centerix.Infrastructure.Tenancy` or shared constants type.
   - `AddAuthorization` with policies: `PlatformAdminOnly` (requires `PlatformAdmin` role, no tenant required) and `TenantMember` (requires resolved active tenant).
   - Why: SaaS separation between platform operators and tenant users.
7. **Apply authorization to controllers**
   - `PlansController`, `FeaturesController`, `TenantsController` → `[Authorize(Policy = "PlatformAdminOnly")]` (platform-global management).
   - `TenantPlansController`, `TenantBillingsController`, `TenantCRMLeadsController` → `[Authorize(Policy = "TenantMember")]`.
   - Consider a global fallback authorization policy so endpoints are secure-by-default, with explicit `[AllowAnonymous]` where needed (e.g., health).
   - Why: Nothing is currently protected.
8. **Platform-admin bypass of the tenant filter**
   - For platform-global entities (`Plan`, `Feature`, `PlanFeature`) — these have no `TenantId`, so no filter applies; no change needed beyond authorization.
   - For any cross-tenant admin read of tenant-scoped data, use `IgnoreQueryFilters()` explicitly in dedicated admin methods only (gated by `PlatformAdminOnly`).
   - Why: Root can administer globally without weakening per-tenant isolation for normal flows.
9. **Fix `ApiController` status mapping** (`src/Centerix.API/Controllers/ApiController.cs`)
   - Map `ErrorKind.Unauthorized` → `401`, add `ErrorKind.Forbidden` → `403`.
   - Why: Correct HTTP semantics; `Forbidden` is defined but unmapped.

### Phase D — Rich Domain + One CQRS Vertical Slice
10. **Refactor entities to rich model** (`src/Centerix.Domain/`)
    - `AuditableEntity<TId>` / `Entity`: keep `Id` and `TenantId` mutable only where the infrastructure needs it (interceptor sets `TenantId`); make business fields settable only through methods.
    - For each entity (`Plan`, `TenantPlan`, `TenantBilling`, `TenantCRMLead`): private setters, a static `Create(...)` factory that validates invariants and raises a creation `DomainEvent`, and intent-named `Update...`/state-transition methods (e.g., `TenantPlan.Renew()`, `TenantBilling.MarkPaid()`, `TenantCRMLead.MoveToStage()`).
    - Define concrete `DomainEvent` types (e.g., `PlanCreatedEvent`, `TenantPlanRenewedEvent`) under `Centerix.Domain/Platform/Events/`.
    - Why: Move logic into the domain; activate the already-wired domain-event dispatch in `AppDbContext.SaveChangesAsync`.
11. **Build one real CQRS slice to justify the pipeline** (`src/Centerix.Application/Platform/...`)
    - Choose one flow (recommended: **Plans**): add `Commands/CreatePlan`, `Queries/GetPlans` (as `ICachedQuery` to exercise `CachingBehaviour`), with handlers, `FluentValidation` validators, and Mapster mappings returning `Result<T>`.
    - Update `PlansController` to send MediatR requests instead of calling `IPlatformService` directly.
    - Why: Demonstrates the intended architecture end-to-end; proves logging/performance/validation/caching behaviours actually run. Other flows can follow the same template later.
12. **Fix `CachingBehaviour` correctness** (`src/Centerix.Application/Common/Behaviours/CachingBehaviour.cs`)
    - Include tenant id in the cache key (from `ICurrentTenant`) to prevent cross-tenant cache bleed.
    - Do not cache failed `Result`s.
    - Ensure pipeline ordering places caching outermost (adjust registration order in `Application/DependencyInjection.cs`).
    - Why: Current behaviour risks serving one tenant's cached data to another and caches errors.
13. **Fix audit user stamping** (`src/Centerix.Infrastructure/Data/Interceptors/AuditableEntityInterceptor.cs`)
    - Replace hardcoded `"System"` with `ICurrentUser.UserName` (fallback `"System"` for background/seed operations).
    - Why: Completes the outstanding TODO now that `ICurrentUser` exists.

## Assumptions & Decisions
- **Admin scope = Separate roles + bypass filter** (user-selected). Plans/Features stay platform-global (no `TenantId`); `PlatformAdmin` manages them; tenant-scoped entities are filtered and derive `TenantId` from context.
- **Rich domain = Full rich domain + one CQRS vertical slice** (user-selected). Plans flow is the reference slice; remaining flows migrate incrementally later.
- Tenant identity is trusted only from the authenticated/resolved Finbuckle context — never from request body/route `tenantId` args for tenant-scoped operations.
- Secret management and the `net9.0` vs `net10.0` framework mismatch are noted but **out of scope** for this plan unless requested.
- Root/seed operations (`ApplicationDbContextInitialiser`, `TenantDbSeeder`) run without a resolved tenant; the filter/guard must not break seeding (use `IgnoreQueryFilters`/`"System"` fallback there).

## Verification
- **Build**: solution compiles on the chosen target framework.
- **Isolation (integration test, Testcontainers.MsSql)**: seed rows for tenant A and tenant B; resolve context as A; assert queries return only A's rows; assert `IgnoreQueryFilters` (admin path) returns both.
- **Write stamping**: create a tenant-scoped entity as tenant A; assert stored `TenantId == A` even if a different `tenantId` is supplied in the request.
- **Authorization**: unauthenticated request to a protected endpoint → `401`; tenant user hitting `PlatformAdminOnly` → `403`; platform admin hitting admin endpoints → `200`.
- **Tenant guard**: request against an inactive/expired tenant → `403`/`402`.
- **Domain events**: creating an entity via its factory raises the expected event and it is dispatched on `SaveChangesAsync` (assert via a test notification handler).
- **CQRS slice**: `GetPlans`/`CreatePlan` flow works through MediatR; caching returns cached result on second call and is keyed per tenant; validation failures return `400` via `Result`→`Problem`.
- **Regression**: existing `PlansController` responses unchanged in shape after the MediatR migration.
