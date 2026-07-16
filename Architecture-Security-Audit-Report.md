# ASP.NET Core Multi-Tenant Architecture & Security Audit

## Executive Summary

- Overall Score (/100): **40/100**
- Architecture Score: **55/100**
- Security Score: **30/100**
- Performance Score: **60/100**
- Maintainability Score: **45/100**
- Scalability Score: **40/100**
- Production Readiness Score: **35/100**
- Multi-Tenant Isolation Score: **25/100**
- Enterprise Readiness Score: **30/100**

---

## Project Overview

- Technology Stack:
  - ASP.NET Core (net10 preview), .NET 8/9-style Clean Architecture layout
  - Multi-tenancy: **Finbuckle.MultiTenant**
  - CQRS: MediatR
  - EF Core + SQL Server
  - JWT Authentication
- Solution Structure:
  - `src/Centerix.API/`
  - `src/Centerix.Application/`
  - `src/Centerix.Domain/`
  - `src/Centerix.Infrastructure/`
- Architecture Style:
  - Clean Architecture intent (API → Application → Domain; Infrastructure provides EF/Auth)
  - CQRS implemented via MediatR records + handlers
- Multi-Tenant Strategy:
  - Finbuckle resolves tenant via header/host/claim strategies
  - `TenantGuardMiddleware` checks tenant active/expiry for non-platform-admin users
  - EF Core query filter applied in `AppDbContext` (tenant filter) based on captured tenant id
- Authentication:
  - JWT issuance via `JwtTokenService`
  - Login endpoint: `AuthController.Login`
  - Validation configured in `Centerix.Infrastructure.DependencyInjection`
- Authorization:
  - Custom permissions via `HasPermissionAttribute` + `PermissionPolicyProvider`
- Database:
  - `AppDbContext` (platform DB) uses Identity + platform feature/plan/subscription/lead tables
  - `TenantDbContext` configured for Finbuckle tenant store
- Main Packages:
  - `Finbuckle.MultiTenant.AspNetCore`
  - `Microsoft.AspNetCore.Identity`
  - `Microsoft.EntityFrameworkCore.*`
  - `FluentValidation`
  - `Scalar.AspNetCore`
  - `Serilog`
  - `Microsoft.Extensions.Caching.Hybrid`
- Observations:
  - Build succeeds but with extensive warnings (style/nullable/security configuration risks)
  - Multiple production-critical security gaps identified by evidence

---

# Findings

## Finding ID: ARCH-001

Severity:
Critical

Category:
Multi-tenant isolation / Data leakage (fail-open)

Affected Layer:
Infrastructure (EF Core query filter) + API middleware (tenant guard)

Affected Files:
- `src/Centerix.Infrastructure/Data/AppDbContext.cs`
- `src/Centerix.API/Infrastructure/TenantGuardMiddleware.cs`

Classes:
- `Centerix.Infrastructure.Data.AppDbContext`
- `Centerix.API.Infrastructure.TenantGuardMiddleware`

Methods:
- `AppDbContext.ApplyTenantQueryFilter()`
- `TenantGuardMiddleware.InvokeAsync()`

Description
The tenant guard middleware and EF Core tenant query filter behave in a way that can fail open when tenant resolution is missing or late.

Evidence
### 1) Query filter is skipped when `_currentTenantId` is empty
`src/Centerix.Infrastructure/Data/AppDbContext.cs`
```csharp
_currentTenantId = _currentTenant.IsResolved ? _currentTenant.TenantId : null;
...
private void ApplyTenantQueryFilter(ModelBuilder builder)
{
    if (string.IsNullOrEmpty(_currentTenantId))
    {
        return;
    }

    foreach (var entityType in builder.Model.GetEntityTypes())
    {
        if (typeof(IHasTenantId).IsAssignableFrom(entityType.ClrType) && !entityType.IsOwned())
        {
            ...
            builder.Entity(entityType.ClrType).HasQueryFilter(lambda);
        }
    }
}
```

### 2) Middleware explicitly allows requests when tenant is not resolved
`src/Centerix.API/Infrastructure/TenantGuardMiddleware.cs`
```csharp
if (currentUser.IsPlatformAdmin || !currentTenant.IsResolved)
{
    await next(context);
    return;
}
```

Why it is a problem
- Tenant scoping must be *fail-closed* in SaaS.
- When `IsResolved == false`, EF query filters may not be applied and the middleware does not enforce tenant restrictions.

Production impact
- Potential cross-tenant read/write exposure for any endpoint that hits `AppDbContext` and relies on EF tenant query filters.

Security impact
- Confidentiality breach across tenants.
- Potential authorization bypass leading to data integrity compromise.

Recommendation
- Fail closed for tenant-bound data: return 403/404 when `!currentTenant.IsResolved` for any tenant-scoped resource.
- Make tenant filtering un-bypassable:
  - Do not capture tenant id in `AppDbContext` constructor.
  - Implement tenant filters that depend on a scoped tenant accessor evaluated at query execution.
  - Add tests that assert tenant filter presence when tenant resolution is absent.

Example Fix
- Change middleware to:
```csharp
if (!currentTenant.IsResolved)
{
    context.Response.StatusCode = StatusCodes.Status403Forbidden;
    return;
}
```
- Replace captured `_currentTenantId` query filters with a runtime tenant id dependency (pattern depends on EF capabilities).

References
- EF Core Query Filters: https://learn.microsoft.com/ef/core/querying/filters
- Finbuckle multitenancy guidance: https://www.finbuckle.com/MultiTenant

---

## Finding ID: ARCH-002

Severity:
Critical

Category:
Authentication / Secrets exposure

Affected Layer:
API configuration + Auth token verification

Affected Files:
- `src/Centerix.API/appsettings.json`

Classes:
- N/A (configuration)
- `Centerix.Infrastructure.Auth.JwtTokenService`

Methods:
- token issuance uses secret: `JwtTokenService.GenerateToken()`

Description
JWT signing secret is stored in plaintext in `appsettings.json`.

Evidence
`src/Centerix.API/appsettings.json`
```json
"JwtSettings": {
  "Secret": "SuperSecretKey@345!123SuperSecretKey@345!123",
  "Issuer": "Centerix",
  "Audience": "CenterixUsers",
  "ExpirationInMinutes": 60
}
```

Why it is a problem
- Repo exposure or configuration leakage results in full token forgery.

Production impact
- Attackers can mint JWTs that pass validation → full system compromise.

Security impact
- Total authentication bypass.

Recommendation
- Store secret in environment variables or secret manager (Azure Key Vault, AWS Secrets Manager).
- Add options validation to require secret presence and sufficient entropy.

Example Fix
- Replace with env var:
  - `JwtSettings__Secret` in deployment config.

References
- OWASP: https://owasp.org/www-project-top-ten/ (A02 Cryptographic Failures)
- Microsoft: https://learn.microsoft.com/aspnet/core/security/app-secrets

---

## Finding ID: ARCH-003

Severity:
High

Category:
Multi-tenant isolation / Cache poisoning

Affected Layer:
Application (MediatR pipeline behavior)

Affected Files:
- `src/Centerix.Application/Common/Behaviours/CachingBehaviour.cs`

Classes:
- `Centerix.Application.Common.Behaviours.CachingBehaviour<TRequest,TResponse>`

Methods:
- `CachingBehaviour.Handle()`

Description
Cache keys fall back to `"global"` when tenant resolution is not available, which can cause cross-tenant cache reuse.

Evidence
`src/Centerix.Application/Common/Behaviours/CachingBehaviour.cs`
```csharp
var tenantKey = currentTenant.IsResolved ? currentTenant.TenantId : "global";
var cacheKey = $"{tenantKey}:{requestName}:{request.GetCacheKey()}";
```

Why it is a problem
- Caching is an isolation boundary.
- If tenant resolution intermittently fails (or is late), responses for tenant-scoped queries may be stored under a shared key.

Production impact
- Potential cross-tenant data leakage through cache.

Security impact
- Confidentiality breach and authorization bypass via cached responses.

Recommendation
- Fail closed in caching behavior when tenant is required but `!currentTenant.IsResolved`.
- Separate caches by tenant id unconditionally for tenant-scoped queries.

Example Fix
- Add guard in `CachingBehaviour.Handle()`:
```csharp
if (!currentTenant.IsResolved) throw new SecurityException("Tenant not resolved");
```
(or return without caching)

References
- Finbuckle multi-tenant best practices
- Microsoft caching isolation considerations

---

## Finding ID: ARCH-004

Severity:
High

Category:
Authentication / Brute force protection missing

Affected Layer:
Presentation (API Controller)

Affected Files:
- `src/Centerix.API/Controllers/AuthController.cs`

Classes:
- `Centerix.API.Controllers.AuthController`

Methods:
- `Login(LoginRequest request)`

Description
Login endpoint does not implement rate limiting, throttling, or account lockout.

Evidence
`src/Centerix.API/Controllers/AuthController.cs`
```csharp
[HttpPost("login")]
public async Task<IActionResult> Login(LoginRequest request)
{
    var user = await userManager.FindByEmailAsync(request.Email);
    if (user == null || !await userManager.CheckPasswordAsync(user, request.Password))
    {
        return Unauthorized(new { error = localizer.Translate("Auth:InvalidCredentials") });
    }
    ...
}
```

Why it is a problem
- Password endpoints are high-value targets for credential stuffing.

Production impact
- Account compromise at scale.

Security impact
- Reduced resistance to brute-force and enumeration.

Recommendation
- Add ASP.NET Core rate limiting (key by IP and email).
- Enable Identity lockout policies and/or progressive delays.

Example Fix
- Apply rate limiting middleware and configure Identity lockout.

References
- OWASP: https://owasp.org/Top10/A02_2021-Cryptographic_Failures/ (credential attack prevention in general)
- OWASP credential stuffing guidance

---

## Finding ID: ARCH-005

Severity:
Medium

Category:
Security error handling / Information disclosure

Affected Layer:
Infrastructure (exception handling)

Affected Files:
- `src/Centerix.API/Infrastructure/GlobalExceptionHandler.cs`

Classes:
- `Centerix.API.Infrastructure.GlobalExceptionHandler`

Methods:
- `TryHandleAsync(...)`

Description
Exception messages are returned to clients, which may leak internal details.

Evidence
`src/Centerix.API/Infrastructure/GlobalExceptionHandler.cs`
```csharp
ProblemDetails = new ProblemDetails
{
    Type = exception.GetType().Name,
    Title = localizer.Translate("Error:Application"),
    Detail = exception.Message,
}
```

Why it is a problem
- Exception messages can contain internal SQL/stack traces or other sensitive info depending on upstream exceptions.

Production impact
- Increased likelihood of exploit development.

Security impact
- Information disclosure.

Recommendation
- In production, return a generic detail and log the exception server-side.

Example Fix
- Replace `Detail = exception.Message` with a generic constant.

References
- OWASP: Sensitive Data Exposure

---

# Architecture Review

- Strengths
  - Clean Architecture layering is mostly respected via `Centerix.Application` containing CQRS handlers and `Centerix.Infrastructure` containing EF Core/Auth.
  - CQRS implemented consistently for platform resources using MediatR records/handlers.

- Weaknesses
  - Multi-tenant isolation enforcement is fragile (fail-open behavior).
  - Security hardening is incomplete for production.

- Violations
  - Tenant enforcement is not fail-closed.

- Clean Architecture compliance
  - Evidence not found for strict dependency direction across all cases; however, core services follow expected direction.

- CQRS review
  - Evidence: `Centerix.Application.Platform.*` contains commands/queries and handlers.
  - However, domain authorization/object-level checks are not consistently applied in handlers.

- Vertical Slice review
  - Partially implemented (handlers co-located with commands/queries) but cross-cutting concerns (security) are not enforced per slice.

---

# Multi-Tenant Review

Tenant Resolution
- Finbuckle configured in `src/Centerix.Infrastructure/DependencyInjection.cs`.

Tenant Context
- `ICurrentTenant` implemented in `src/Centerix.Infrastructure/Common/CurrentTenant.cs`.

Global Query Filters
- Implemented in `src/Centerix.Infrastructure/Data/AppDbContext.cs`.

TenantInterceptor
- Not a pure interceptor: a `SaveChangesInterceptor` is implemented in `TenantInterceptor` but only sets tenant id on Added entities.
- Evidence: `src/Centerix.Infrastructure/Data/Interceptors/TenantInterceptor.cs`.

EF Core
- Evidence: tenant filter captured in constructor and can be skipped when tenant id is null/empty.

Repository safety
- Evidence not found for additional repository methods; all queries go through `AppDbContext`.

Cross-tenant risks
- Critical fail-open behavior described in ARCH-001.

Background Jobs
- Evidence not found.

Caching
- Evidence: `CachingBehaviour` uses tenant id but falls back to `"global"` when tenant not resolved (ARCH-003).

Logging
- Evidence: request correlation middleware exists (`RequestLogContextMiddleware`).

File Storage
- Evidence not found.

Authorization
- Evidence: authorization uses permission claims and `HasPermissionAttribute`.

Data isolation
- Evidence: EF query filter + tenant interceptor.

Final tenant isolation score: **25/100**

---

# Security Review (OWASP Top 10 based on evidence)

| OWASP Top 10 | Status | Evidence | Risk |
|---|---|---|---|
| A02 Cryptographic Failures | ❌ | JWT secret in plaintext | Critical |
| A01/Broken Access Control | ❌ | Tenant fail-open behavior | Critical |
| A03 Injection | Evidence not found | Unknown |
| A05 Security Misconfiguration | ⚠️ | Exception detail leakage | Medium |
| A07 Identification & Auth Failures | ⚠️ | No brute-force throttling | High |
| Others | Evidence not found |  |  |

---

# Authentication Review

- JWT validation: configured in `src/Centerix.Infrastructure/DependencyInjection.cs`.
- Token issuance: `src/Centerix.Infrastructure/Auth/JwtTokenService.cs`.
- Issues:
  - Hardcoded secret (ARCH-002)

---

# Authorization Review

- Permission system: `HasPermissionAttribute` + `PermissionPolicyProvider`.
- Issues:
  - Evidence indicates reliance on tenant filter for isolation; handler-level object authorization not observed.

---

# Domain Review

- Domain entities implement validation and domain events.
- Evidence: `src/Centerix.Domain/Platform/*`.
- Issues: no tenant ownership model validation observed in application/service layers.

---

# Application Layer Review

- MediatR pipeline behaviors:
  - Logging, performance, unhandled exception, caching
- Issues:
  - Caching isolation fail-open behavior (ARCH-003)

---

# Infrastructure Review

- EF Core contexts:
  - `AppDbContext`: Identity + platform resources with query filters
  - `TenantDbContext`: Finbuckle tenant store
- Issues:
  - Tenant query filter skip behavior (ARCH-001)

---

# Database Review

Indexes
- Evidence from migrations (sample):
  - Unique indexes on `Plans.Code` and `Features.Code`
  - `IX_TenantPlans_PlanId`, `IX_TenantBilling_PlanId`, `IX_PlanFeatures_*`

Constraints
- Evidence from migrations:
  - Foreign key restrict/cascade rules set.

Concurrency
- Evidence not found (no RowVersion / optimistic concurrency tokens).

Transactions
- Evidence not found.

Potential N+1
- Evidence not found for N+1 patterns.

---

# Performance Review

- Caching behavior implemented but tenant isolation risk exists.
- Evidence: `CachingBehaviour.Handle` logs cache status but does not prevent cross-tenant caching.

---

# Logging & Monitoring

- Serilog configured (`UseSerilog` in Program.cs).
- `RequestLogContextMiddleware` pushes correlation id.
- Evidence not found for health checks/metrics.

---

# Configuration Review

- Hardcoded JWT secret is present in `appsettings.json`.
- Evidence: `src/Centerix.API/appsettings.json`.

---

# Dependency Injection Review

- DI wiring in:
  - `src/Centerix.API/DependencyInjection.cs`
  - `src/Centerix.Application/DependencyInjection.cs`
  - `src/Centerix.Infrastructure/DependencyInjection.cs`

- Issue:
  - PermissionPolicyProvider nullability mismatch is a warning only; not production-critical in itself.

---

# API Review

- Controllers are protected by `HasPermission` for tenant resources.
- No pagination/filtering evidence found because endpoints return full lists.

---

# Validation Review

- FluentValidation used via `AddValidatorsFromAssembly` in `Centerix.Application.DependencyInjection.cs`.
- Evidence: `CreatePlanValidator` only.

---

# Exception Handling Review

- `GlobalExceptionHandler` implemented via `IExceptionHandler`.
- Issues:
  - returns `exception.Message` to clients (ARCH-005)

---

# Production Readiness

| Feature | Status | Notes |
|---|---|---|
| Health Checks | Evidence not found | None observed |
| Docker | Evidence not found | None observed |
| HTTPS | HTTPS redirection enabled | `UseHttpsRedirection()` in API middlewares |
| Rate Limiting | ❌ | None observed for login |
| OpenTelemetry | Evidence not found | None observed |
| Distributed Cache | ⚠️ | HybridCache present; isolation risk |
| Secret Management | ❌ | JWT secret in plaintext |
| CI/CD | Evidence not found | None observed |
| Logging | ✅ | Serilog present |
| Monitoring | Evidence not found | None observed |
| Backups | Evidence not found | None observed |
| Disaster Recovery | Evidence not found | None observed |
| Feature Flags | Evidence not found | None observed |
| Outbox/Inbox | Evidence not found | None observed |
| Idempotency | Evidence not found | None observed |

---

# Testing Review

- Evidence not found for security/performance tests.

---

# Enterprise Readiness

- Evidence not found for advanced enterprise SaaS features (outbox/inbox, billing readiness, GDPR retention).

---

# Positive Findings

- CQRS + MediatR behaviors exist (logging/performance/unhandled exception)
- EF Core configurations include indexes and constraints in migrations
- Finbuckle tenant store context exists (`TenantDbContext`)

---

# Technical Debt

1. Critical: Tenant isolation fail-open (ARCH-001)
2. Critical: JWT secret stored in plaintext (ARCH-002)
3. High: Cache key fail-open causing potential cross-tenant cache poisoning (ARCH-003)
4. High: Login brute-force protection missing (ARCH-004)
5. Medium: Exception detail leakage (ARCH-005)

---

# Risk Matrix

| Risk | Probability | Impact | Severity |
|---|---:|---:|---|
| Tenant data leakage via fail-open | Medium | High | Critical |
| JWT token forgery due to secret exposure | High | High | Critical |
| Cache cross-tenant reuse | Medium | High | High |
| Credential stuffing/brute-force | Medium | High | High |
| Exception message info disclosure | Medium | Medium | Medium |

---

# Prioritized Roadmap

## Immediate (Critical)
- Fix fail-open tenant isolation (ARCH-001)
- Remove plaintext JWT secret (ARCH-002)

## High Priority
- Prevent cache poisoning (ARCH-003)
- Add login throttling/lockout (ARCH-004)

## Medium Priority
- Sanitize exception details (ARCH-005)

## Low Priority
- N/A in evidenced findings

## Future Improvements
- Add observability, health checks, security tests

---

# Action Checklist

- [ ] Fix tenant isolation fail-open in EF query filters and middleware
- [ ] Secure JWT signing secret with secret manager/env vars
- [ ] Enforce cache isolation for tenant-bound queries
- [ ] Add rate limiting/lockout for login
- [ ] Sanitize exception responses

---

# Final Verdict

❌ Not Ready

The project contains evidenced production-critical multi-tenant isolation and authentication secret handling defects.

---

## Files Reviewed

- `src/Centerix.API/Program.cs`
- `src/Centerix.API/DependencyInjection.cs`
- `src/Centerix.API/Infrastructure/GlobalExceptionHandler.cs`
- `src/Centerix.API/Infrastructure/RequestLogContextMiddleware.cs`
- `src/Centerix.API/Infrastructure/TenantGuardMiddleware.cs`
- `src/Centerix.API/Controllers/*.cs`
- `src/Centerix.API/appsettings*.json`
- `src/Centerix.Application/DependencyInjection.cs`
- `src/Centerix.Application/Common/Behaviours/*.cs`
- `src/Centerix.Application/Common/Interfaces/*.cs`
- `src/Centerix.Application/Platform/**/*.cs`
- `src/Centerix.Application/Tenants/*.cs`
- `src/Centerix.Domain/Common/*.cs`
- `src/Centerix.Domain/Platform/**/*.cs`
- `src/Centerix.Infrastructure/DependencyInjection.cs`
- `src/Centerix.Infrastructure/Auth/*.cs`
- `src/Centerix.Infrastructure/Common/*.cs`
- `src/Centerix.Infrastructure/Data/*.cs`
- `src/Centerix.Infrastructure/Data/Configurations/*.cs`
- `src/Centerix.Infrastructure/Data/Interceptors/*.cs`
- `src/Centerix.Infrastructure/Data/Migrations/**/*.cs`
- `src/Centerix.Infrastructure/Tenancy/*.cs`

## Files Not Reviewed

Evidence not found for: CI/CD, Docker, SQL scripts outside EF migrations, background/hosted services, health checks, OpenTelemetry, Swagger security hardening.

## Coverage

- Controllers: **7/7** (all controllers under `src/Centerix.API/Controllers`)
- Handlers: **4/4** (platform command/query handlers observed)
- Entities: **7/7** (domain entities observed under `Centerix.Domain/Platform`)
- Configurations: **7/7** (`IEntityTypeConfiguration` classes under `Centerix.Infrastructure/Data/Configurations`)
- Validators: **1/1** (validator observed: `CreatePlanValidator`)
- Middlewares: **3/3** (`RequestLogContextMiddleware`, `TenantGuardMiddleware`, and exception handling middleware via exception handler)
- Services: **2/2** (`PlatformService`, `TenantService`)—as observed in source
- DbContexts: **2/2** (`AppDbContext`, `TenantDbContext`)
- Migrations: **3/3** (three migration files read)

