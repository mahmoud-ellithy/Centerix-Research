You are a Senior .NET Architect, Backend Engineer, Security Engineer, Database Architect, and Code Auditor.

You are working on the Centerix project, an ASP.NET Core 10 multi-tenant SaaS platform.

Your task is to perform a deep, evidence-based audit of the IMPLEMENTED repository and produce/update the project's architecture and verification documentation.

============================================================
SOURCE OF TRUTH
============================================================

The repository itself is the ultimate source of truth.

The following documentation is existing project documentation and must be treated as architectural context, NOT as proof that the implementation actually behaves as described:

- ARCHITECTURE-BASELINE.md
- PHASE-3-VERIFICATION-REPORT.md

Read both files first.

Then inspect the actual implementation under:

- src/Centerix.API
- src/Centerix.Application
- src/Centerix.Domain
- src/Centerix.Infrastructure
- tests/

Do NOT assume anything is implemented merely because it appears in either MD file.

Whenever documentation conflicts with code, the actual code wins and the discrepancy must be reported.

Do not invent implementation details.

============================================================
PRIMARY OBJECTIVE
============================================================

Determine the REAL current state of Centerix.

The audit must answer:

1. What architecture is actually implemented?
2. Which architectural rules are actually enforced?
3. Which security boundaries are actually secure?
4. Is tenant isolation actually enforced?
5. Is authentication/authorization actually correct?
6. Is the database model synchronized with migrations?
7. Are transactions actually atomic?
8. Are subscription, feature, and limit rules actually enforced?
9. Are auditing and caching correctly isolated?
10. Are validations actually executed?
11. Do tests prove the claims they are supposed to prove?
12. Is the application production-ready?
13. What remains before production?

This is an implementation audit, not a theoretical architecture exercise.

============================================================
IMPORTANT AUDIT RULES
============================================================

1. Inspect actual source code.

2. Follow execution paths, not only class names.

3. Trace requests from:
   HTTP endpoint
   → middleware
   → authentication
   → tenant resolution
   → tenant authorization
   → authorization policies
   → controller
   → MediatR
   → handler
   → domain
   → EF Core
   → database.

4. For security-sensitive behavior, identify the exact trust boundary.

5. Never mark something PASS because a class or interface exists.

6. A feature is PASS only when its runtime behavior is actually verified.

7. A database constraint is PASS only when the actual migration/schema supports it.

8. InMemory EF tests do NOT prove SQL Server schema correctness.

9. A registered FluentValidation validator does NOT prove validation executes.

10. A transaction abstraction does NOT prove atomicity. Trace the actual DbContext/transaction usage.

11. Do not modify production code during the audit unless explicitly instructed.

12. Do not "fix" tests merely to make them pass.

13. If a test is wrong, identify it as a test defect.

14. If a test passes but does not actually prove the intended behavior, identify the coverage gap.

15. Do not downgrade security findings because they are unlikely.

16. Do not introduce architectural changes that are not justified by evidence.

17. Preserve existing valid architectural decisions.

============================================================
PROJECT ARCHITECTURE TO VERIFY
============================================================

Expected architecture:

Centerix.API
    ↓
Centerix.Application
    ↓
Centerix.Domain

Centerix.Infrastructure implements Application abstractions and provides:

- EF Core
- SQL Server
- Identity
- JWT
- refresh tokens
- authorization
- multi-tenancy
- caching
- auditing
- external services

Verify dependency direction.

Verify that:

- Domain does not depend on Application/Infrastructure/API.
- Application does not depend on Infrastructure/API.
- Infrastructure implements Application abstractions.
- API does not access DbContext directly.
- Controllers dispatch application requests rather than implementing business logic.

Report any violation.

============================================================
MULTI-TENANCY AUDIT
============================================================

Inspect Finbuckle configuration and determine exactly how tenant resolution works.

Verify:

- tenant header strategy
- host/subdomain strategy
- claim strategy if any
- tenant resolution order
- unresolved tenant behavior
- tenant authorization
- tenant membership validation
- inactive tenant handling
- expired tenant handling
- suspended membership
- revoked membership
- invited membership
- multi-tenant users
- same user having different roles in different tenants

Determine whether resolving a tenant is correctly separated from authorizing access to that tenant.

The client-provided tenant identifier must NEVER by itself grant access.

Verify the complete chain:

Client tenant identifier
→ Finbuckle resolution
→ current tenant
→ authenticated user
→ membership
→ membership status
→ tenant state
→ authorized tenant context
→ query filters
→ persistence.

Pay special attention to:

- CurrentTenant
- ICurrentTenant
- TenantGuardMiddleware
- ICurrentUser
- TenantMembership
- TenantRegistry
- TenantRegistrySyncService
- TenantInterceptor
- AppDbContext query filters.

Test conceptually and, where possible, through actual tests:

- Tenant A user requesting Tenant B
- Tenant A resource ID submitted while Tenant B is selected
- cross-tenant GET
- cross-tenant POST
- cross-tenant PUT
- cross-tenant DELETE
- same user in multiple tenants
- different roles for the same user in different tenants.

============================================================
EF CORE TENANT ISOLATION
============================================================

Inspect every entity expected to be tenant-scoped.

Determine:

- whether it implements IHasTenantId
- which base class it inherits
- whether a tenant query filter is applied
- whether the filter evaluates the current authorized tenant dynamically
- whether Added entities receive TenantId from trusted server context
- whether client-provided TenantId can override it
- whether Update/Delete operations can escape the tenant filter.

Inspect:

- AppDbContext
- TenantInterceptor
- entity configurations
- global query filters
- IgnoreQueryFilters() usages.

Every IgnoreQueryFilters() usage must be inspected individually.

If IgnoreQueryFilters() is used, verify that an explicit tenant restriction is applied whenever tenant isolation is required.

============================================================
IDENTITY AUDIT
============================================================

Inspect the actual Identity architecture.

Verify:

- IdentityUser
- ApplicationRole
- AppDbContext
- UserManager
- SignInManager if used
- password configuration
- lockout
- normalization
- username/email uniqueness
- multi-tenant user requirements
- platform users
- PlatformUser
- platform authentication
- separation between tenant users and platform staff.

Determine whether a legitimate user can belong to multiple tenants.

Determine whether Identity's global uniqueness constraints conflict with the business requirement.

Do not assume the existing design is correct.

============================================================
TENANT MEMBERSHIP
============================================================

Inspect TenantMembership completely.

Verify:

- primary key
- UserId
- TenantId
- RoleName
- Status
- lifecycle transitions
- FK relationships
- indexes
- tenant scope
- persistence
- migration
- model snapshot
- SQL Server compatibility.

Verify whether the runtime model matches the migration snapshot.

Any mismatch must be treated as a database correctness issue.

============================================================
INVITATIONS
============================================================

Audit the complete invitation lifecycle.

Verify:

- token generation
- entropy
- token hashing
- raw token storage
- expiration
- duplicate prevention
- revoke
- accept
- replay
- existing user flow
- new user flow
- authenticated requirements
- anonymous requirements
- wrong-user protection
- membership creation
- membership reactivation
- invitation state transitions.

Trace the actual HTTP endpoints.

Pay special attention to fallback authorization policies.

If a new user is expected to register using an invitation token, verify that the HTTP endpoint can actually be reached without authentication.

Do not rely only on handler behavior.

============================================================
AUTHENTICATION / JWT
============================================================

Audit:

- JWT generation
- signing algorithm
- secret validation
- issuer/audience if configured
- expiration
- claims
- role claims
- tenant claims
- permission claims
- feature claims
- refresh tokens
- token storage
- hashing
- rotation
- reuse detection
- replay protection
- revocation.

Verify whether permissions are intentionally resolved server-side.

Verify that tenant authorization is not based solely on a tenant claim supplied by the token.

============================================================
AUTHORIZATION
============================================================

Audit:

- fallback authorization policy
- [Authorize]
- [AllowAnonymous]
- HasPermission
- PermissionPolicyProvider
- PermissionAuthorizationHandler
- FeatureAuthorizationHandler
- PlatformAdminGuard
- platform scope classification
- tenant scope classification
- role resolution
- role-permission mapping.

Verify that:

- TenantUser cannot perform TenantAdmin operations.
- TenantAdmin cannot perform platform operations.
- Platform operations cannot accidentally become tenant operations.
- platform bypasses are explicit and limited.
- authorization failures fail closed.

Inspect exception handling in authorization code.

An authorization handler that silently swallows exceptions must be reported.

============================================================
PERMISSIONS
============================================================

Inspect:

- Permissions
- PermissionCatalog
- ApplicationRole
- RolePermission
- tenant roles
- platform roles.

Verify that permissions are:

- defined
- catalogued
- persisted
- mapped to roles
- resolved for the current tenant
- actually enforced.

Verify that permission changes take effect according to the intended architecture.

============================================================
PLATFORM VS TENANT SCOPE
============================================================

Every endpoint that accesses platform-level data must be classified correctly.

Verify platform scope allow-lists and metadata.

Look for:

- platform endpoints accidentally requiring tenant membership
- tenant endpoints accidentally bypassing tenant checks
- missing PlatformAdminGuard
- overly broad platform bypasses
- endpoints without permission metadata.

============================================================
SUBSCRIPTIONS
============================================================

Inspect TenantPlan and related subscription implementation.

Verify:

- lifecycle
- status transitions
- expiration
- suspension
- cancellation
- renewal
- bonus months
- price snapshot
- feature snapshot
- limits
- historical records
- concurrency
- unique active subscription constraints.

Verify the actual date calculations.

Verify renewal anchoring.

Verify that Plan changes do not mutate historical subscription snapshots.

============================================================
FEATURES
============================================================

Inspect:

- Feature
- PlanFeature
- TenantPlanFeature
- FeatureAuthorizationHandler
- RequireFeature
- IFeatureAccessService.

Verify that feature authorization represents subscription entitlement and is not confused with user permission.

Verify PlatformAdmin behavior.

============================================================
LIMITS
============================================================

Inspect:

- LimitService
- TenantUsageCounter
- TenantLimitOverride
- LimitTypeCodes.

Verify:

- limit precedence
- reservation
- release
- atomicity
- concurrency
- SQL Server implementation
- InMemory behavior
- failure handling
- race conditions.

Determine whether limits are actually enforced or only modeled.

============================================================
TRANSACTIONS
============================================================

Trace all multi-entity and cross-context operations.

Pay particular attention to:

- Identity + Application DbContext
- TenantDbContext + AppDbContext
- invitation registration
- invitation acceptance
- tenant lifecycle synchronization
- subscription operations
- limit reservation + business writes.

For each operation determine:

- context(s) involved
- transaction boundary
- isolation behavior
- rollback behavior
- failure behavior
- compensating operations
- possibility of orphaned records.

Never claim atomicity unless the actual database transaction guarantees it.

============================================================
CONCURRENCY
============================================================

Inspect:

- RowVersion
- DbUpdateConcurrencyException
- unique constraints
- filtered indexes
- ExecuteUpdateAsync
- check-then-insert logic
- retry behavior.

Identify race conditions.

Pay special attention to:

- subscription activation
- renewal
- invitation consumption
- membership creation
- limits
- duplicate records.

============================================================
AUDITING
============================================================

Verify:

- CreatedAtUtc
- CreatedBy
- LastModifiedUtc
- LastModifiedBy
- AuditLog
- PlatformAuditLog
- tenant/platform decision logic
- old/new values
- failure handling.

Determine whether audit failures are intentionally non-blocking.

Identify any possibility of tenant audit records being written into the wrong scope.

============================================================
CACHING
============================================================

Inspect HybridCache and caching behavior.

Verify:

- cache key structure
- tenant identity in keys
- authorization before caching
- unauthorized requests
- cross-tenant leakage
- invalidation
- permission-sensitive data.

Caching must never allow one tenant to receive another tenant's response.

============================================================
VALIDATION
============================================================

Inspect FluentValidation registration.

Determine whether validators are actually executed.

Trace:

request
→ MediatR
→ validation behavior
→ handler.

If validators are registered but no ValidationBehavior exists, report this explicitly.

Do not assume registration means automatic execution.

============================================================
ERROR HANDLING
============================================================

Inspect:

- Result
- Error
- ErrorKind
- ApiController.Problem
- GlobalExceptionHandler.

Verify HTTP mappings.

Verify that security-sensitive exceptions do not leak internal information.

Verify that authorization failures fail closed.

============================================================
DATABASE / MIGRATIONS
============================================================

Inspect every AppDbContext migration and snapshot.

Verify:

- migration chain
- model snapshot
- current entity configurations
- indexes
- foreign keys
- delete behavior
- unique constraints
- filtered indexes
- column types
- nullable/non-nullable properties
- defaults
- raw SQL migrations
- cross-context relationships.

The following condition is mandatory:

CURRENT EF MODEL == LATEST MIGRATION MODEL

If not, report:

- exact entity
- exact property
- exact migration
- snapshot location
- runtime impact.

If possible, use EF tooling to detect pending model changes.

============================================================
SQL SERVER REALISM
============================================================

Determine whether the tests actually exercise SQL Server.

InMemory tests are useful for application behavior but do not prove:

- SQL schema correctness
- FK behavior
- unique constraints
- filtered indexes
- rowversion
- transaction semantics
- ExecuteUpdateAsync behavior
- relational query behavior
- migration correctness.

If Testcontainers.MsSql exists but is unused, report the gap.

============================================================
TEST AUDIT
============================================================

Run the complete test suite.

Do not merely report the number of passing tests.

For every important security/architecture claim determine whether a test actually proves it.

Inspect tests for false confidence.

Look specifically for:

- tests that never reach the intended endpoint
- tests using the wrong authentication state
- tests using InMemory for relational claims
- tests that only test invalid cases
- tests missing successful flows
- tests missing cross-tenant mutation attempts
- tests missing permission denial
- tests missing refresh-token replay
- tests missing transaction rollback.

For failures, classify:

- production defect
- test defect
- environment issue
- expected/pre-existing issue.

Never modify assertions simply to make tests green.

============================================================
SECURITY REVIEW
============================================================

Perform an independent security review in addition to verifying the documented findings.

Look for:

- IDOR
- cross-tenant access
- privilege escalation
- broken authorization
- tenant spoofing
- insecure invitation capability
- token replay
- token leakage
- plaintext token storage
- secret leakage
- hardcoded credentials
- sensitive information disclosure
- missing authorization metadata
- unsafe IgnoreQueryFilters
- unsafe raw SQL
- mass assignment
- over-posting
- insecure defaults
- race conditions
- missing concurrency controls.

Severity:

CRITICAL
HIGH
MEDIUM
LOW
INFO

Do not exaggerate severity.

============================================================
KNOWN PHASE-3 FINDINGS
============================================================

Explicitly re-verify these previously reported findings:

C1:
TenantMembership.RoleName missing from migrations/snapshot/schema.

C2:
POST /api/invitations/register blocked by fallback authentication policy.

H1:
Invitation email hardcodes localhost URL.

H2:
Identity user creation and domain changes are not atomically transactional.

H3:
Integration tests lack relational SQL Server coverage and critical registration-flow coverage.

Also re-check:

- missing IDesignTimeDbContextFactory
- FluentValidation pipeline
- PermissionAuthorizationHandler exception swallowing
- null ValidUpTo semantics
- OpenAPI dependency vulnerability
- JWT secret provisioning
- documented test failures
- platform/tenant scope classification.

For every previous finding classify:

RESOLVED
PARTIALLY RESOLVED
STILL PRESENT
NO LONGER APPLICABLE
FALSE FINDING

Provide evidence.

============================================================
ARCHITECTURE BASELINE
============================================================

After completing the audit, update:

ARCHITECTURE-BASELINE.md

This document must describe the ACTUAL CURRENT ARCHITECTURE.

It must contain, where applicable:

1. System purpose
2. Technology stack
3. Architecture overview
4. Project structure
5. Dependency rules
6. Domain boundaries
7. Multi-tenancy architecture
8. Identity architecture
9. Authorization architecture
10. Platform vs tenant operations
11. Subscription architecture
12. Feature architecture
13. Limit architecture
14. Database architecture
15. EF Core rules
16. Transaction rules
17. Concurrency rules
18. Auditing
19. Caching
20. Validation
21. API conventions
22. Error handling
23. Testing architecture
24. Security rules
25. Canonical implementation patterns
26. Forbidden patterns
27. Technical debt
28. Business decisions
29. Rules for future modules
30. Definition of Done

Do not document intended behavior as implemented behavior.

If something is planned but not implemented, clearly label it.

============================================================
VERIFICATION REPORT
============================================================

Create/update:

PHASE-3-VERIFICATION-REPORT.md

The report must be evidence-based.

Include:

# VERIFICATION REPORT — Centerix Multi-Tenant Identity & Authorization System

Date:
Scope:
Method:

Then provide:

## Verification Evidence Summary

Include actual:

- build result
- test result
- migration result
- model/snapshot result
- relevant security evidence.

## 1. Identity Architecture
PASS / PARTIAL / FAIL

## 2. TenantMembership Architecture
PASS / PARTIAL / FAIL

## 3. TenantInvitation Lifecycle
PASS / PARTIAL / FAIL

## 4. Existing-User Invitation
PASS / PARTIAL / FAIL

## 5. New-User Registration
PASS / PARTIAL / FAIL

## 6. Tenant-Scoped Roles
PASS / PARTIAL / FAIL

## 7. Tenant-Scoped Permissions
PASS / PARTIAL / FAIL

## 8. JWT and Tenant Context
PASS / PARTIAL / FAIL

## 9. Cross-Tenant Isolation
PASS / PARTIAL / FAIL

## 10. Database Constraints
PASS / PARTIAL / FAIL

## 11. Transactions and Concurrency
PASS / PARTIAL / FAIL

## 12. Security Vulnerabilities
PASS / PARTIAL / FAIL

## 13. Unit Tests
PASS / PARTIAL / FAIL

## 14. Integration Tests
PASS / PARTIAL / FAIL

## 15. Build Status
PASS / PARTIAL / FAIL

## 16. Migration Status
PASS / PARTIAL / FAIL

For every section include concrete evidence.

============================================================
FINAL VERDICT
============================================================

Produce a final table:

| # | Area | Verdict | Evidence |
|---|------|---------|----------|

Then:

## What Is Production-Ready

Only include functionality that is actually verified.

## What Is Not Production-Ready

Only include verified blockers/gaps.

## Critical Issues

ID | Issue | File | Impact | Required Fix

## High-Priority Issues

ID | Issue | File | Impact | Required Fix

## Medium/Low Technical Debt

ID | Issue | Recommendation

## Remaining Work

Order remaining work by priority.

============================================================
PRODUCTION READINESS CRITERIA
============================================================

Do NOT declare the project production-ready if any of the following remain:

- exploitable cross-tenant access
- broken tenant authorization
- broken authentication
- broken critical onboarding flow
- database model/migration mismatch affecting runtime
- unrecoverable transaction inconsistency in critical workflows
- critical security vulnerability
- authorization bypass
- missing mandatory database constraint
- critical feature that only appears implemented but is unreachable.

A green test suite alone is NOT sufficient.

A successful build alone is NOT sufficient.

Documentation alone is NOT evidence.

============================================================
CODE MODIFICATION RULE
============================================================

For this audit/documentation task:

DO NOT modify production source code.

You may modify only:

- ARCHITECTURE-BASELINE.md
- PHASE-3-VERIFICATION-REPORT.md

Do not create fake migrations.

Do not modify tests merely to make them pass.

Do not silently fix implementation problems.

============================================================
OUTPUT QUALITY
============================================================

Be precise.

Use exact file paths.

Use exact class/method/property names.

Use line numbers when available.

Explain the execution path when relevant.

Separate:

FACT
from
INFERENCE
from
RECOMMENDATION.

Never claim that something was tested if it was only inspected.

Never claim SQL Server behavior based solely on EF InMemory.

Never claim transaction atomicity based solely on multiple SaveChanges calls.

Never claim validation execution based solely on validator registration.

Never claim tenant isolation based solely on query filters without checking authorization and write paths.

The final documentation must be internally consistent.

If the old documentation says PASS but the repository proves FAIL, change the documentation and explain why.

If the old documentation says FAIL but the repository proves RESOLVED, update it and provide evidence.

The repository is the final authority.