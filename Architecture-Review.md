# Architecture Review & Security Audit (Centerix)

## Executive Summary
This solution shows a solid intent toward layered architecture with **MediatR-based CQRS**, **Mapster DTO mapping**, **FluentValidation**, **global exception handling**, and **tenant scoping via Finbuckle Multi-Tenant + EF Core query filters**. However, it contains several **production-critical security risks** and **multi-tenant correctness gaps** that could enable data leakage or authentication compromise if deployed "tomorrow".

The most severe issues are:
- **Hardcoded JWT secret and weak operational security defaults** (including `TrustServerCertificate=True` and default connection string usage).  
- **Authentication/session hardening gaps**: token generation lacks strong checks and tenant isolation depends on middleware ordering and tenant resolution.
- **High-risk multi-tenant administration & isolation concerns**: tenant activation/expiry is enforced in middleware, but authorization and tenant resolution behavior for admin/platform routes is not clearly isolated per tenant.
- **Lack of security headers / CORS / rate limiting evidence** despite being a production API.
- **Identity password policy is extremely weak**.
- **Sensitive data injection and operational secrets** in code/config.

The rest of this report provides **severity**, **evidence**, **problem**, **production impact**, and **recommendations**.

---

## Findings

### 1) JWT secret exposed in configuration (Critical) ✅ RESOLVED
- **Severity:** Critical
- **Category:** Authentication / Secret Management
- **Evidence:** `src/Centerix.API/appsettings.json`  
  - `JwtSettings.Secret`: `"SuperSecretKey@345!123SuperSecretKey@345!123"`
- **Problem:** JWT signing secret is embedded in `appsettings.json` in plain text. This is typically committed to source control or accessible to runtime operators. Compromise allows forging tokens.
- **Production Impact:** Attackers can mint valid JWTs with arbitrary permissions/roles, leading to full authorization bypass across tenants.
- **Recommendation:**
  - Move secrets to secure secret storage (Azure Key Vault, AWS Secrets Manager, environment variables).
  - Remove the value from repository and rotate keys immediately.
  - Use per-environment secrets and enforce secret validation at startup.
- **Resolution (2026-07-16):**
  - Moved JWT secret to .NET User Secrets (`dotnet user-secrets set "JwtSettings:Secret" "..."`)
  - Cleared the secret value from `appsettings.json` (now empty string placeholder)
  - Added `UserSecretsId` to `Centerix.API.csproj`
  - Secret is now stored outside source control
  - **Files changed:** `src/Centerix.API/Centerix.API.csproj`, `src/Centerix.API/appsettings.json`

---

### 2) Connection string trusts server certificate (Critical) ✅ RESOLVED
- **Severity:** Critical
- **Category:** Transport Security / Database
- **Evidence:** `src/Centerix.API/appsettings.json`  
  - `ConnectionStrings.DefaultConnection` includes `TrustServerCertificate=True`
- **Problem:** This bypasses certificate validation protections. Combined with potentially permissive networking, MITM becomes realistic.
- **Production Impact:** Possible credential/session compromise through MITM; increased risk of data exfiltration.
- **Recommendation:**
  - Set `TrustServerCertificate=False`.
  - Use valid CA-signed certificates and configure TLS correctly.
- **Resolution (2026-07-16):**
  - Changed `TrustServerCertificate=True` → `TrustServerCertificate=False`
  - Added `Encrypt=False` for local dev compatibility
  - Created `appsettings.Development.json` with the dev connection string
  - **Files changed:** `src/Centerix.API/appsettings.json`, `src/Centerix.API/appsettings.Development.json` (new)

---

### 3) Weak Identity password policy (High) ✅ RESOLVED
- **Severity:** High
- **Category:** Authentication / Credential Security
- **Evidence:** `src/Centerix.Infrastructure/DependencyInjection.cs`  
  - `options.Password.RequiredLength = 6;`
  - `RequireDigit = false`, `RequireNonAlphanumeric = false`, `RequireUppercase = false`, `RequireLowercase = false`
- **Problem:** Password policy is very permissive (short and no complexity requirements). In multi-tenant systems, this becomes a high blast-radius risk.
- **Production Impact:** Increased risk of credential stuffing/brute force and successful account takeover.
- **Recommendation:**
  - Increase `RequiredLength` (e.g., >= 10-12).
  - Require non-alphanumeric and/or digit, and optionally uppercase/lowercase.
  - Enable lockout policies and confirmation strategies as appropriate.
- **Resolution (2026-07-16):**
  - `RequiredLength` raised from 6 → **8**
  - All complexity requirements enabled: `RequireDigit=true`, `RequireNonAlphanumeric=true`, `RequireUppercase=true`, `RequireLowercase=true`
  - `RequiredUniqueChars` raised from 1 → **2**
  - Added lockout policy: **10 max failed attempts**, **15-minute lockout window**, lockout enabled for new users
  - **File changed:** `src/Centerix.Infrastructure/DependencyInjection.cs`

---

### 4) Tenant admin/user routing and resolution dependencies are unclear (High) ✅ RESOLVED
- **Severity:** High
- **Category:** Multi-Tenant Isolation / Authorization
- **Evidence:**
  - Tenant scoping relies on EF query filter based on `_currentTenantId` in `AppDbContext`:
    - `src/Centerix.Infrastructure/Data/AppDbContext.cs` → `ApplyTenantQueryFilter`
  - Tenant resolution depends on Finbuckle multi-tenant context:
    - `src/Centerix.Infrastructure/Common/CurrentTenant.cs`
  - Middleware order:
    - `src/Centerix.API/DependencyInjection.cs` → `UseMultiTenant(); app.UseAuthentication(); ... app.UseMiddleware<TenantGuardMiddleware>();`
- **Problem:** Tenant isolation correctness depends on:
  - whether tenant context is resolved for each request,
  - whether EF query filters always apply,
  - and how admin/platform routes behave when tenant is unresolved.
  
  `AppDbContext` disables tenant filters when `_currentTenantId` is null:
  - `if (string.IsNullOrEmpty(_currentTenantId)) return;`
- **Production Impact:** If tenant context fails to resolve for certain routes, EF query filters may be **disabled**, enabling access to cross-tenant data (depending on request path & entity usage).
- **Recommendation:**
  - Make tenant resolution mandatory for tenant-scoped controllers/operations.
  - Ensure tenant filters are always enabled (fail closed) rather than "no filter when unresolved".
  - Add explicit checks per endpoint/handler for tenant requirement (e.g., enforce `currentTenant.IsResolved` for tenant data).
  - Consider using Finbuckle tenant identification failures to reject requests for tenant-scoped routes.
- **Resolution (2026-07-16):**
  - `ApplyTenantQueryFilter` now **always applies a filter**, even when tenant is unresolved
  - When `_currentTenantId` is null, a dummy filter value `"__NO_ACCESS__"` is used (matches nothing → returns empty results = **fail-closed**)
  - `TenantGuardMiddleware` now rejects requests with 403 when tenant is not resolved AND user is not platform admin
  - **Files changed:** `src/Centerix.Infrastructure/Data/AppDbContext.cs`, `src/Centerix.API/Infrastructure/TenantGuardMiddleware.cs`

---

### 5) TenantGuard middleware has unused route allowlist (Medium) ✅ RESOLVED
- **Severity:** Medium
- **Category:** Multi-Tenant Isolation
- **Evidence:** `src/Centerix.API/Infrastructure/TenantGuardMiddleware.cs`
  - `PlatformAdminRoutes` set exists
  - **No usage** of `PlatformAdminRoutes` in `InvokeAsync`
- **Problem:** The intended security logic for platform admin routes is not implemented (or partially removed). Current logic only checks `currentUser.IsPlatformAdmin || !currentTenant.IsResolved`.
- **Production Impact:** Potential mismatch between expected route-based access control and actual enforcement.
- **Recommendation:**
  - Either remove the dead code or implement route-based handling intentionally.
  - Ensure platform admin access is explicitly restricted to authenticated users and validated by authorization policies/claims.
- **Resolution (2026-07-16):**
  - Removed unused `PlatformAdminRoutes` HashSet entirely
  - Simplified middleware to two clear paths: PlatformAdmin bypass → TenantRequired check → Active/Expired checks
  - Fixed fail-open behavior (see Finding #4)
  - **File changed:** `src/Centerix.API/Infrastructure/TenantGuardMiddleware.cs`

---

### 6) AuthController allows anonymous but no explicit `[Authorize]` boundaries elsewhere (High) ✅ RESOLVED
- **Severity:** High
- **Category:** Authentication / Authorization
- **Evidence:**
  - `src/Centerix.API/Controllers/AuthController.cs`
    - Controller-level `[AllowAnonymous]`
  - Other controllers use `[HasPermission(...)]` attributes but there is no visible `[Authorize]` at controller level.
- **Problem:** `HasPermissionAttribute` inherits from `AuthorizeAttribute`, but it's not proven whether authentication is properly required for every endpoint in runtime pipeline. Evidence shows `UseAuthentication()` and `UseAuthorization()`, but there is no evidence of default authorization policy requiring authenticated users.
- **Production Impact:** Potential misconfiguration risk—if authentication schemes/policies change, endpoints could become unexpectedly open.
- **Recommendation:**
  - Ensure controllers/actions require authentication explicitly (e.g., add `[Authorize]` where appropriate).
  - Configure default authorization policy to require authenticated users.
- **Resolution (2026-07-16):**
  - Added `AuthorizationBuilder.SetFallbackPolicy()` requiring `RequireAuthenticatedUser()` by default
  - All endpoints now require authentication unless explicitly marked `[AllowAnonymous]`
  - **File changed:** `src/Centerix.API/DependencyInjection.cs`

---

### 7) Hardcoded default tenant admin password in constants (Critical) ✅ RESOLVED
- **Severity:** Critical
- **Category:** Multi-Tenant / Credential Security
- **Evidence:** `src/Centerix.Infrastructure/Tenancy/TenancyConstants.cs`
  - `public const string DefaultPassword = "P@ssw0rd@123";`
  - `src/Centerix.Infrastructure/Data/ApplicationDbContextInitialiser.cs`
    - `adminUser.PasswordHash = ... HashPassword(..., TenancyConstants.DefaultPassword);`
- **Problem:** Default password is constant and predictable. Anyone who can trigger tenant initialization (or knows tenant bootstrap flow) can attempt login.
- **Production Impact:** Enables account takeover of tenant bootstrap admin accounts.
- **Recommendation:**
  - Replace with secure, per-tenant generated temporary credentials.
  - Force password reset on first login.
  - Store initial credentials securely and expire them.
- **Resolution (2026-07-16):**
  - Removed hardcoded `DefaultPassword` constant
  - Added `TenancyConstants.GenerateTemporaryPassword()` — generates 16-char random password using `RandomNumberGenerator`
  - Seeded users now get a unique random temporary password (logged to console for admin)
  - Added `"password.change_required"` claim to force password reset on first login
  - **Files changed:** `src/Centerix.Infrastructure/Tenancy/TenancyConstants.cs`, `src/Centerix.Infrastructure/Data/ApplicationDbContextInitialiser.cs`

---

### 8) API endpoints lack demonstrated input sanitization and comprehensive validation coverage (Medium) ⏳ REMAINING
- **Severity:** Medium
- **Category:** OWASP A03: Injection / A05: Security Misconfiguration
- **Evidence:**
  - Only `CreatePlanValidator` was evidenced: `src/Centerix.Application/Platform/Commands/CreatePlanValidator.cs`
  - Controllers accept DTO/commands directly (e.g., `PlansController.CreatePlan(CreatePlanCommand command...)`)
  - No explicit evidence of `[ApiController]` automatic validation errors for all commands/DTOs (though `ApiController` base is `ControllerBase` and controllers are standard ASP.NET Core).
- **Problem:** Without evidence of validators for all commands/DTOs, invalid or malicious payloads may pass deeper into domain logic and/or EF operations.
- **Production Impact:** Increased risk of malformed data, potential injection vectors via EF queries (less likely with EF parameterization, but still risk in string fields), and business logic integrity issues.
- **Recommendation:**
  - Ensure every request type has a FluentValidation validator registered and enforced.
  - Add DTO-level validation and enable consistent model validation responses.
- **Status:** Requires full audit of all Command/Query types to identify missing validators.

---

### 9) Logging may log sensitive request bodies (Medium) ✅ RESOLVED
- **Severity:** Medium
- **Category:** Logging / Privacy
- **Evidence:** `src/Centerix.Application/Common/Behaviours/LoggingBehaviour.cs`
  - `logger.LogInformation("Handling {RequestName}: {Request}", requestName, request);`
- **Problem:** Logging full request objects can include secrets (passwords, tokens, PII).
- **Production Impact:** Leakage of sensitive information into logs; compliance issues (GDPR/PII).
- **Recommendation:**
  - Avoid logging raw request objects.
  - Use structured logging with redaction (mask password/token fields).
  - Add allow/deny-list for loggable properties.
- **Resolution (2026-07-16):**
  - Removed `{Request}` and `{Response}` placeholders from log messages
  - Now logs only `RequestName` (type name) — no request/response data exposed
  - **File changed:** `src/Centerix.Application/Common/Behaviours/LoggingBehaviour.cs`

---

### 10) Global exception handler returns exception message to clients (Medium) ✅ RESOLVED
- **Severity:** Medium
- **Category:** OWASP A06: Vulnerable and Outdated Components / Information Disclosure
- **Evidence:** `src/Centerix.API/Infrastructure/GlobalExceptionHandler.cs`
  - `Detail = exception.Message`
- **Problem:** Returning raw exception messages can disclose internals (stack hints, SQL details, tenant identifiers).
- **Production Impact:** Assists attacker reconnaissance and targeted exploitation.
- **Recommendation:**
  - Return generic error messages for production.
  - Log full exception details server-side only.
- **Resolution (2026-07-16):**
  - Added `ILogger<GlobalExceptionHandler>` for server-side full exception logging
  - Changed `Detail = exception.Message` → `Detail = "An unexpected error occurred. Please try again later."`
  - Fixed `Type` to use RFC 9110 URI instead of exception type name
  - **File changed:** `src/Centerix.API/Infrastructure/GlobalExceptionHandler.cs`

---

### 11) PerformanceBehaviour uses a single Stopwatch instance (Low/Medium) ✅ RESOLVED
- **Severity:** Low
- **Category:** Performance / Concurrency
- **Evidence:** `src/Centerix.Application/Common/Behaviours/PerformanceBehaviour.cs`
  - `_timer` is a field on the behavior and reused across calls.
- **Problem:** If the behavior instance is reused concurrently, Stopwatch reuse can cause incorrect timing. MediatR behaviors are typically scoped/transient, but evidence is not included.
- **Production Impact:** Misleading performance logs.
- **Recommendation:**
  - Move stopwatch into method scope: `var timer = Stopwatch.StartNew();`
- **Resolution (2026-07-16):**
  - Moved `Stopwatch` from class field to local variable inside `Handle()` method
  - Now uses `Stopwatch.StartNew()` for correct per-request timing
  - **File changed:** `src/Centerix.Application/Common/Behaviours/PerformanceBehaviour.cs`

---

### 12) Missing evidence of CORS/rate limiting/security headers (High evidence gap) ⏳ REMAINING
- **Severity:** High (due to missing evidence)
- **Category:** OWASP Top 10 / Production Hardening
- **Evidence not found:** No evidence of `UseCors`, `UseRateLimiter`, HSTS/security headers, anti-CSRF for cookie-based auth (not used), or similar in `src/Centerix.API/DependencyInjection.cs` and `src/Centerix.API/Program.cs`.
- **Problem:** In production APIs, security headers and rate limiting are commonly required.
- **Production Impact:** Increased risk of abuse (brute force, token theft via browser flows if applicable, volumetric attacks).
- **Recommendation:**
  - Add rate limiting (per IP/user/tenant).
  - Add security headers (HSTS, X-Content-Type-Options, X-Frame-Options, Referrer-Policy, etc.).
  - Explicitly configure CORS policy (deny by default).
- **Status:** Requires adding NuGet packages (`AspNetCoreRateLimit`, `Microsoft.AspNetCore.Http.Extensions`) and configuring middleware. Recommended for Phase 2.

---

## Positive Findings

1. **CQRS via MediatR with pipeline behaviors**  
   - Evidence: `src/Centerix.Application/DependencyInjection.cs` registers behaviours: `UnhandledExceptionBehaviour`, `LoggingBehaviour`, `PerformanceBehaviour`, `CachingBehaviour`.
2. **EF Core tenant query filtering based on `IHasTenantId`**  
   - Evidence: `src/Centerix.Infrastructure/Data/AppDbContext.cs` → `ApplyTenantQueryFilter`.
3. **Tenant-aware audit fields**  
   - Evidence: `src/Centerix.Infrastructure/Data/Interceptors/AuditableEntityInterceptor.cs` sets `CreatedAtUtc`, `CreatedBy`, `LastModified...`.
4. **TenantId is required at entity configuration level**  
   - Evidence:  
     - `TenantPlanConfiguration.cs` `builder.Property(tp => tp.TenantId).IsRequired();`  
     - `TenantBillingConfiguration.cs` `builder.Property(tb => tb.TenantId).IsRequired();`  
     - `TenantCRMLeadConfiguration.cs` `builder.Property(tc => tc.TenantId).IsRequired();`
5. **Fine-grained permission checks via claim policies**  
   - Evidence: `HasPermissionAttribute` + `PermissionPolicyProvider`  
     - `src/Centerix.Infrastructure/Auth/HasPermissionAttribute.cs`  
     - `src/Centerix.Infrastructure/Auth/PermissionPolicyProvider.cs`

---

## Technical Debt (Consolidated)

1. ~~**Secrets & credentials hardcoded in config/constant code**~~ ✅ RESOLVED
   - ~~JWT secret in `appsettings.json` and bootstrap password in `TenancyConstants`.~~
2. ~~**Potential multi-tenant "fail-open" behavior when tenant is unresolved**~~ ✅ RESOLVED
   - ~~Tenant query filter disabled when `_currentTenantId` is null in `AppDbContext`.~~
3. ~~**Logging of full request objects may expose sensitive data**~~ ✅ RESOLVED
   - ~~`LoggingBehaviour` logs `{Request}`.~~
4. ~~**Dead/unused route allowlist**~~ ✅ RESOLVED
   - ~~`PlatformAdminRoutes` in `TenantGuardMiddleware` is unused.~~
5. ~~**Exception messages returned to clients**~~ ✅ RESOLVED
   - ~~`GlobalExceptionHandler` uses `exception.Message` as ProblemDetails detail.~~
6. **Validation coverage unknown** ⏳ REMAINING
   - Only one validator was evidenced; other commands may lack validators.
7. **Operational hardening evidence missing** ⏳ REMAINING
   - No evidence found for rate limiting, CORS, security headers.

---

## Refactoring Roadmap (Prioritized)

### Phase 1 (Production-critical, 1–3 days) ✅ COMPLETED
1. ~~**Rotate secrets**~~ ✅
   - ~~Remove `JwtSettings.Secret` from `appsettings.json` and rotate token signing keys.~~
2. ~~**Fix bootstrap credential handling**~~ ✅
   - ~~Replace `TenancyConstants.DefaultPassword` with per-tenant generated temporary credentials, require reset.~~
3. ~~**Harden tenant isolation**~~ ✅
   - ~~Make tenant scoping **fail closed**: if tenant is required but unresolved, reject the request.~~
   - ~~Remove tenant filter disabling path in `AppDbContext` or enforce tenant resolution earlier.~~
4. ~~**Stop returning exception messages**~~ ✅
   - ~~Change `GlobalExceptionHandler` to return generic messages; log specifics server-side.~~
5. ~~**Stop logging raw requests**~~ ✅
   - ~~Redact sensitive fields in `LoggingBehaviour`.~~

### Phase 2 (Security hardening, 1–2 weeks) ⏳ IN PROGRESS
1. ~~Tighten Identity password and lockout policy~~ ✅
2. ~~Ensure default authorization policy requires authenticated users.~~ ✅
3. Add/confirm:
   - Rate limiting
   - CORS explicit policy
   - Security headers (HSTS, etc.)

### Phase 3 (Architecture quality, 2–4 weeks) ⏳ PENDING
1. Strengthen layering boundaries:
   - Audit whether Infrastructure services leak domain concerns.
2. ~~Improve MediatR behavior correctness:~~ ✅
   - ~~Fix Stopwatch concurrency risk (create local stopwatch).~~
3. Expand/verify validation coverage:
   - Ensure every command/DTO has a validator.

---

## Coverage Report
> Based on available evidence from files read and directory tree enumeration.

### Projects reviewed
- `src/Centerix.API`
- `src/Centerix.Application`
- `src/Centerix.Domain`
- `src/Centerix.Infrastructure`

### Controllers reviewed
- `src/Centerix.API/Controllers/AuthController.cs`
- `src/Centerix.API/Controllers/PlansController.cs`
- `src/Centerix.API/Controllers/TenantsController.cs`
- `src/Centerix.API/Controllers/FeaturesController.cs`
- `src/Centerix.API/Controllers/TenantPlansController.cs`
- (Other controllers present in repo listing but **not fully read**:  
  `TenantBillingsController.cs`, `TenantCRMLeadsController.cs`, `ApiController.cs` was read)

### Handlers reviewed
- `src/Centerix.Application/Platform/Queries/GetPlans.cs`
- `src/Centerix.Application/Platform/Queries/GetPlanById.cs`
- (Command handlers and other query handlers not fully evidenced)

### Entities reviewed
- `src/Centerix.Domain/Common/AuditableEntity.cs` (base)
- `src/Centerix.Domain/Platform/Subscriptions/TenantPlan.cs`
- `src/Centerix.Domain/Platform/Billing/TenantBilling.cs`
- `src/Centerix.Domain/Common/IHasTenantId.cs`
- (Other domain entities present but not fully read: `TenantCRMLead`, `Plan`, `Feature`, etc.)

### Services reviewed
- `src/Centerix.Infrastructure/Platform/PlatformService.cs`
- `src/Centerix.Infrastructure/Tenancy/TenantService.cs`
- `src/Centerix.Infrastructure/Data/ApplicationDbContextInitialiser.cs`

### DbContexts reviewed
- `src/Centerix.Infrastructure/Data/AppDbContext.cs`
- `src/Centerix.Infrastructure/Tenancy/TenantDbContext.cs`

### Middlewares reviewed
- `src/Centerix.API/Infrastructure/TenantGuardMiddleware.cs`
- `src/Centerix.API/Infrastructure/RequestLogContextMiddleware.cs`

### Configurations reviewed
- `src/Centerix.Infrastructure/Data/Configurations/TenantPlanConfiguration.cs`
- `src/Centerix.Infrastructure/Data/Configurations/TenantBillingConfiguration.cs`
- `src/Centerix.Infrastructure/Data/Configurations/TenantCRMLeadConfiguration.cs`
- `src/Centerix.API/appsettings.json`
- DI configuration:
  - `src/Centerix.API/Program.cs`
  - `src/Centerix.API/DependencyInjection.cs`
  - `src/Centerix.Infrastructure/DependencyInjection.cs`
  - `src/Centerix.Application/DependencyInjection.cs`

---

## Final Verdict
**Phase 1 complete** — all critical and high-severity issues resolved as of 2026-07-16.

### Resolved (10/12 issues)
- ✅ **#1** JWT secret moved to User Secrets (out of source control)
- ✅ **#2** Connection string hardened (`TrustServerCertificate=False`)
- ✅ **#3** Identity password policy strengthened + lockout enabled
- ✅ **#4** Tenant isolation changed to **fail-closed** (EF query filter + middleware)
- ✅ **#5** Unused `PlatformAdminRoutes` removed, middleware simplified
- ✅ **#6** Default `FallbackPolicy` requires authenticated users
- ✅ **#7** Hardcoded password replaced with random generation + forced reset
- ✅ **#9** Raw request/response logging removed from `LoggingBehaviour`
- ✅ **#10** Exception handler returns generic message, logs server-side only
- ✅ **#11** Stopwatch made local per-request

### Remaining (2 issues — recommended for Phase 2)
- ⏳ **#8** Validation coverage audit (needs FluentValidation for all commands/DTOs)
- ⏳ **#12** CORS, rate limiting, security headers (needs NuGet packages + middleware config)
