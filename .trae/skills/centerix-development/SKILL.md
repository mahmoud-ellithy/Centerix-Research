---
name: "centerix-development"
description: "Guides development tasks in the Centerix multi-tenant SaaS platform. Invoke when adding features, modifying domain entities, writing CQRS handlers, touching multi-tenancy, auth, or EF Core migrations."
---

# Centerix Development

Centerix is a multi-tenant SaaS management platform built on .NET Clean Architecture with DDD, CQRS, Finbuckle multi-tenancy, EF Core (SQL Server), ASP.NET Core Identity + JWT, and HybridCache.

## Solution Layout

```
src/
  Centerix.API/            - Presentation (controllers, middlewares, localization, DI)
  Centerix.Application/    - Use cases: CQRS commands/queries, behaviors, DTOs, service interfaces
  Centerix.Domain/         - Entities, value objects, domain events, domain errors, constants
  Centerix.Infrastructure/ - EF Core, Identity, auth, Finbuckle tenancy, platform/tenant services
```

Dependency direction: `API -> Application -> Domain`, and `Infrastructure -> Application -> Domain`. Domain has zero external dependencies.

## Conventions to Follow

### Layering rules
- Domain (`Centerix.Domain`) must not reference EF Core, ASP.NET Core, or any other project.
- Application defines interfaces (`IAppDbContext`, `ICurrentUser`, `ICurrentTenant`, `ILocalizer`, `ITenantService`, `IPlatformService`); Infrastructure implements them.
- Controllers stay thin: dispatch MediatR commands/queries only.
- Never put EF Core queries or domain logic in controllers.

### Adding a new feature (CQRS)
1. Define/extend the aggregate in `Centerix.Domain/<Area>/` (entity + errors + events).
2. Add EF Core configuration in `Infrastructure/Data/Configurations/`.
3. Create command(s) and/or query(s) under `Application/<Area>/Commands/` or `Queries/` with validator(s).
4. Register handlers via MediatR assembly scanning (already wired in Application DI).
5. Expose via a thin controller in `Centerix.API/Controllers/`.
6. Add a migration if schema changed: `dotnet ef migrations add <Name> --project src/Centerix.Infrastructure --startup-project src/Centerix.API`.

### Domain modeling
- Entities derive from `Entity` (or `AuditableEntity` if auditable).
- Tenant-scoped entities implement `IHasTenantId`.
- Fail with typed domain errors: `<Entity>Errors.<Name>` returning `Result<T>`.
- Raise domain events via the entity's event collection (dispatched by infrastructure).

### Multi-tenancy
- Finbuckle resolves tenant via `__tenant__` header, host segment (`tenant.*`), and JWT claim.
- `TenantInterceptor` auto-stamps `TenantId` on save.
- `TenantGuardMiddleware` rejects requests lacking a resolved tenant on tenant-scoped endpoints.
- Tenant store lives in `TenantDbContext`; app data in `AppDbContext`. Both use SQL Server on `DefaultConnection`.

### Authentication & Authorization
- ASP.NET Core Identity (`IdentityUser` / `IdentityRole`) stored in `AppDbContext`.
- JWT issued by `JwtTokenService` (settings under `JwtSettings`, validated on startup).
- Permission-based auth: `Permissions` constants + `HasPermission` attribute + `PermissionPolicyProvider`.
- Fallback policy requires authenticated user on all endpoints unless `[AllowAnonymous]`.

### Pipeline behaviors (registered for every MediatR request)
- `UnhandledExceptionBehaviour`
- `LoggingBehaviour`
- `PerformanceBehaviour`
- `CachingBehaviour` (HybridCache)

### Localization
- `JsonLocalizer` reads `en.json` / `ar.json` in `Centerix.API/Localization/`.
- Supported cultures: `en` (default), `ar`.
- Add user-facing strings to both files; resolve via `ILocalizer`.

### API
- API versioning with default `v1`; URL substitution enabled.
- ProblemDetails customized with `requestId`.
- Rate limiter policy `LoginPolicy` (5 req/min per IP) on login.
- OpenAPI via `AddOpenApi()`.

## Common Tasks

### Add a new entity (e.g., `Product`)
1. `Domain/Products/Product.cs` (extend `Entity` / `AuditableEntity`, implement `IHasTenantId` if tenant-scoped).
2. `Domain/Products/ProductErrors.cs`.
3. `Infrastructure/Data/Configurations/ProductConfiguration.cs`.
4. Register `DbSet<Product>` in `AppDbContext`.
5. `dotnet ef migrations add AddProduct --project src/Centerix.Infrastructure --startup-project src/Centerix.API`.
6. Add commands/queries + validators under `Application/Products/`.
7. Add `ProductsController.cs`.

### Update a domain entity
- Keep `Result<T>` style for return values; never throw for domain invariants.
- Add domain events under `<Area>/Events/` when state transitions are meaningful.

## Build & Run

```pwsh
dotnet build Centerix.slnx
dotnet run --project src/Centerix.API
```

## Key Files (reference)

- `src/Centerix.API/Program.cs` — app entry & middleware order.
- `src/Centerix.API/DependencyInjection.cs` — presentation wiring (versioning, problem details, rate limiter, localization).
- `src/Centerix.Application/DependencyInjection.cs` — application services + MediatR + FluentValidation + AutoMapper + behaviors.
- `src/Centerix.Infrastructure/DependencyInjection.cs` — DbContexts, Finbuckle, Identity, JWT, HybridCache, scoped services.
- `src/Centerix.Infrastructure/Data/AppDbContext.cs` — app aggregates + Identity store.
- `src/Centerix.Infrastructure/Tenancy/TenantDbContext.cs` — tenant store.
- `src/Centerix.Infrastructure/Auth/Permissions.cs` — permission constants.

## Guardrails

- Do NOT reference `Microsoft.EntityFrameworkCore` from Domain or Application.
- Do NOT put domain logic or EF queries in controllers.
- Do NOT bypass tenant isolation by querying `AppDbContext` without a resolved tenant context.
- Do NOT log or serialize JWT secrets; keep `JwtSettings:Secret` in user-secrets / env.
- Do NOT skip validators for new commands; FluentValidation is expected.
