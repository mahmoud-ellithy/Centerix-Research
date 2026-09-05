# CENTERIX — PHASE 6
# BILLING & INVOICING — PRODUCTION-GRADE FINANCIAL CORRECTNESS & SECURITY AUDIT

You are acting as a Principal .NET Architect, Senior Backend Engineer, Security Engineer, and Financial Systems Auditor.

Your task is to perform a DEEP, INDEPENDENT, EVIDENCE-BASED AUDIT of the IMPLEMENTED Billing / Invoicing functionality in the current Centerix repository.

This is NOT a design exercise.
This is NOT an implementation task.
This is NOT a refactoring task.

DO NOT MODIFY ANY SOURCE CODE, TEST CODE, MIGRATIONS, DATABASE CONFIGURATION, OR DOCUMENTATION.

Your job is to inspect the CURRENT WORKING TREE and produce a rigorous audit report.

The current source code is the source of truth.

============================================================
1. PROJECT CONTEXT
============================================================

Centerix is a production-oriented multi-tenant SaaS platform for educational centers.

Architecture:

- ASP.NET Core / .NET 10
- Clean Architecture
- Domain / Application / Infrastructure / API
- EF Core 10
- SQL Server
- ASP.NET Identity
- JWT authentication
- Finbuckle.MultiTenant
- MediatR / CQRS
- FluentValidation
- Shared database with tenant isolation
- Tenant-scoped authorization
- Tenant-scoped roles and permissions
- Feature-based authorization
- Subscription / Plan / Billing architecture
- Soft deletion
- Auditing
- Optimistic concurrency where required

Important established architectural rules:

1. Tenant isolation is mandatory.
2. Tenant context comes from the established tenant-resolution pipeline.
3. Authenticated users must be authorized members of the resolved tenant.
4. Tenant permissions are resolved server-side.
5. Permissions must NOT be trusted from JWT claims.
6. Financial mutations must be protected by the appropriate permission and feature gates.
7. Financial state transitions must be enforced by the domain/application layer, not merely by controllers.
8. Client input must never be allowed to bypass financial invariants.
9. Transactions must protect multi-record financial operations.
10. Concurrency must be handled wherever concurrent financial mutations can produce incorrect results.
11. Monetary values must use appropriate precision and never rely on floating-point arithmetic.
12. Database constraints must reinforce critical financial invariants.
13. Auditability is required for financial mutations.
14. Existing architecture and business rules must be preserved.
15. Do NOT invent business requirements that are not supported by the code or existing project documentation.

Reference documents to inspect if present:

- ARCHITECTURE-BASELINE.md
- PHASE-3-VERIFICATION-REPORT.md
- MODULE-INVENTORY-REPORT.md
- Any existing Billing / Subscription / Invoice documentation
- Existing audit reports related to tenancy, authorization, subscriptions, or payments

Treat these documents as architectural context, but ALWAYS verify their claims against the CURRENT source code.

============================================================
2. AUDIT SCOPE
============================================================

Identify ALL implemented functionality related to:

- Billing
- Invoicing
- Payments
- Platform payments
- Tenant subscription billing
- Subscription charges
- Invoice generation
- Invoice numbering
- Invoice lifecycle
- Payment lifecycle
- Payment records
- Refunds, if implemented
- Discounts, if implemented
- Taxes, if implemented
- Add-ons / plan charges, if implemented
- Pricing tiers, if implemented
- Bonuses/free months, if financially relevant
- Renewal billing
- Financial adjustments
- Credit/debit operations, if implemented
- Any entity that represents money or a financial obligation

Do NOT assume the module boundaries from folder names.

Trace the actual dependency graph:

Controller
→ Application command/query
→ Validator
→ Handler
→ Domain entity / aggregate
→ Repository / DbContext
→ EF configuration
→ Database migration
→ authorization
→ feature/subscription checks
→ transaction/concurrency behavior
→ tests.

Search the entire repository for financial entities and concepts.

At minimum investigate terms such as:

Invoice
Payment
PlatformPayment
Billing
Subscription
Amount
Price
Total
Subtotal
Tax
Discount
Paid
Pending
Failed
Cancelled
Refund
Due
Outstanding
Balance
Currency
InvoiceNumber
PaymentReference
TransactionId
ExternalReference
AddOn
PricingTier
Renewal
Charge

Also inspect indirect financial behavior even if it does not contain these names.

============================================================
3. PRIMARY OBJECTIVE
============================================================

Determine whether Billing / Invoicing is safe for PRODUCTION.

Specifically answer:

Can a malicious or buggy client:

- access another tenant's invoices?
- create financial records for another tenant?
- modify another tenant's payment?
- mark an unpaid invoice as paid?
- mark a payment as successful without legitimate evidence?
- pay the same invoice twice?
- create duplicate payments?
- replay the same payment request?
- bypass a subscription restriction?
- manipulate amounts?
- manipulate currency?
- manipulate invoice status?
- manipulate payment status?
- modify historical financial data?
- delete financial records?
- cancel records that should be immutable?
- cause inconsistent invoice/payment state?
- exploit concurrent requests?
- exploit transaction boundaries?
- exploit missing unique constraints?
- exploit missing authorization?
- exploit missing feature gates?
- exploit soft-delete?
- exploit query-filter bypass?
- exploit tenant resolution?
- exploit client-supplied identifiers?
- cause money to be lost or duplicated?

Also determine whether normal legitimate operations can accidentally create inconsistent financial state.

============================================================
4. CRITICAL AREA — MONEY CORRECTNESS
============================================================

Audit EVERY monetary field.

For each:

- Type
- Precision
- Scale
- Database column type
- Default value
- Nullable/non-nullable
- Validation
- Range restrictions
- Rounding behavior
- Currency relationship
- Calculation source

Determine whether:

- decimal is used appropriately
- SQL decimal precision/scale is sufficient
- values can overflow
- negative values are possible
- unexpected zero values are possible
- rounding is deterministic
- calculations are duplicated across layers
- client can provide calculated totals that should be server-derived
- subtotal + tax - discount = total invariants are enforced where applicable
- payment amount can exceed invoice amount
- payment amount can become negative
- financial values can be modified after posting

DO NOT invent tax/discount rules.

If the existing implementation has no such concept, report that only if relevant.

============================================================
5. CLIENT-SUPPLIED FINANCIAL VALUES
============================================================

For every financial command, determine which values come from the client.

Pay special attention to:

- Amount
- Total
- Price
- Discount
- Tax
- Currency
- Invoice status
- Payment status
- PaidAt
- Payment reference
- External transaction ID
- Invoice ID
- Subscription ID
- Tenant ID

Determine whether each value SHOULD be client-controlled according to the existing architecture.

Flag any case where:

- the client can set a server-owned financial value
- the client can set a lifecycle state
- the client can set PaidAt
- the client can mark a record Paid
- the client can alter an amount after creation
- the client can select another tenant's financial object
- the client can select an unrelated subscription/payment/invoice

============================================================
6. INVOICE LIFECYCLE AUDIT
============================================================

Reverse-engineer the actual Invoice state machine from the code.

Do NOT invent states.

Document:

- all states
- allowed transitions
- forbidden transitions
- who can perform each transition
- whether transitions are domain-enforced
- whether controllers can bypass them
- whether handlers directly mutate state

Test conceptually for:

Created → Paid
Created → Cancelled
Created → Overdue
Paid → Cancelled
Paid → Unpaid
Cancelled → Paid
Cancelled → Created
etc.

Only evaluate transitions that exist or are implied by the implementation.

Look for:

- terminal state bypass
- repeated transition bugs
- state mutation without validation
- invalid timestamps
- status/time inconsistency
- status/amount inconsistency
- deletion of invoices
- mutation of historical invoices

IMPORTANT:

If an endpoint uses HTTP DELETE but semantically performs Cancel, determine whether that is consistent with the project's API conventions and financial semantics.

Do NOT automatically call it a defect merely because DELETE is used.

============================================================
7. PAYMENT LIFECYCLE AUDIT
============================================================

Reverse-engineer the actual Payment state machine.

Identify:

- Pending
- Paid/Successful
- Failed
- Cancelled
- Refunded
- Other existing states

Only use states actually found.

Audit:

- creation rules
- transition rules
- repeated operations
- terminal states
- timestamps
- external references
- amount consistency
- invoice relationship
- subscription relationship
- payment provider relationship, if any

Critical questions:

Can the client create a Payment as already Paid?

Can a client set PaidAt?

Can Cancelled become Paid?

Can Failed become Paid without provider confirmation?

Can Paid become Cancelled?

Can the same external transaction be processed twice?

Can the same invoice receive duplicate successful payments?

Can two concurrent requests both succeed?

============================================================
8. IDEMPOTENCY / REPLAY SAFETY
============================================================

This is a financial system.

Determine whether payment creation or financial commands are idempotent.

Search for:

- IdempotencyKey
- ExternalTransactionId
- ProviderTransactionId
- PaymentReference
- unique external reference
- request deduplication
- provider callback deduplication

Determine whether retries can cause:

- duplicate payments
- duplicate invoices
- duplicate charges
- duplicate subscription renewals

Distinguish between:

A) explicitly supported idempotency
B) database uniqueness accidentally providing partial protection
C) no idempotency protection

Do NOT invent a requirement for a specific payment provider.

============================================================
9. DUPLICATE FINANCIAL OPERATIONS
============================================================

Inspect every unique index and database constraint related to:

- Invoice number
- Invoice per subscription
- Payment reference
- External transaction
- Payment per invoice
- Payment period
- Renewal period
- Pricing tier
- Subscription period

Determine whether uniqueness is:

- global
- tenant-scoped
- aggregate-scoped
- time-period scoped

Verify whether that matches the architecture.

Pay special attention to:

- unique constraints containing TenantId
- invoice numbering collisions between tenants
- duplicate payment period records
- cancelled records participating in uniqueness constraints
- soft-deleted records participating in uniqueness constraints
- unique indexes with nullable columns

Do not call a uniqueness rule wrong unless the intended business scope can be established from the code/documentation.

============================================================
10. TENANT ISOLATION — CRITICAL
============================================================

Treat cross-tenant financial access as CRITICAL.

For EVERY Billing/Invoicing entity determine:

- TenantId presence
- tenant ownership
- query filters
- authorization
- ownership checks
- navigation-based tenant leakage
- direct ID access
- update ownership
- delete/cancel ownership
- create ownership

Test scenarios conceptually:

Tenant A user knows Tenant B InvoiceId.

Can they:

GET it?
UPDATE it?
CANCEL it?
PAY it?
DELETE it?
Use it to create a payment?
Use it in another financial command?

Also inspect whether tenant IDs are accepted from the client.

A client-supplied TenantId must NEVER override the resolved tenant context.

Verify:

TenantGuard
CurrentTenant
CurrentUser
TenantMembership
authorization handlers
repository/query filters
DbContext filters

Do not rely on query filters alone for sensitive mutation paths.

============================================================
11. AUTHORIZATION
============================================================

Audit ALL Billing/Invoicing endpoints.

For each endpoint record:

- Authentication
- Permission
- Feature gate
- Tenant context
- Ownership enforcement
- PlatformAdmin requirements if applicable

Verify permissions are meaningful and correctly scoped.

Look for:

- missing [HasPermission]
- missing [RequireFeature]
- wrong permission
- wrong feature
- GET endpoints exposing data without authorization
- mutation endpoints without authorization
- authorization applied only at controller level when specific endpoints differ
- platform-only operations accidentally exposed to tenant users
- tenant users accessing platform-level financial data

IMPORTANT:

Do not merely verify the attribute exists.

Trace the actual authorization pipeline and determine whether it really blocks unauthorized access.

============================================================
12. SUBSCRIPTION / PLAN / FEATURE / LIMIT INTERACTION
============================================================

Billing is tightly coupled to subscriptions.

Determine how Billing interacts with:

- TenantPlan
- Subscription
- Plan
- Feature
- Feature access
- Limits
- Renewal
- Expiration
- Bonus months
- Add-ons
- pricing tiers

Determine:

- Which object is authoritative for current subscription state?
- Is the subscription snapshot immutable?
- Can billing mutate historical plan terms?
- Are charges calculated from the correct snapshot?
- Can expired tenants create financial operations?
- Can suspended tenants create financial operations?
- Are financial records still readable after expiration?
- Are renewal operations transactional?

Do NOT invent business rules.

If the code does not establish a rule, explicitly report:

"Business rule not established by implementation."

============================================================
13. TRANSACTIONAL INTEGRITY
============================================================

Trace every operation involving multiple writes.

Examples:

- Create invoice
- Create payment
- Mark invoice paid
- Renew subscription
- Apply payment
- Cancel invoice
- Refund payment
- Create subscription + invoice
- Payment + invoice status update
- Renewal + new subscription period
- Any Identity + tenant membership + billing combination

Determine whether the operation is atomic.

Look for:

- multiple SaveChangesAsync calls
- transaction scopes
- explicit DbTransaction
- shared transaction infrastructure
- external provider calls inside DB transactions
- side effects before transaction commit
- side effects after transaction failure

Identify partial-failure scenarios.

Example:

Payment record saved successfully
BUT invoice status update fails.

What happens?

Or:

Invoice created
BUT subscription renewal fails.

What happens?

Do NOT demand distributed transactions unless the architecture actually requires one.

============================================================
14. CONCURRENCY
============================================================

Financial operations are concurrency-sensitive.

Audit:

- RowVersion
- concurrency tokens
- optimistic concurrency
- database constraints
- atomic update patterns
- transaction isolation
- duplicate requests

Focus on:

- MarkPaid
- Cancel
- Payment creation
- Invoice creation
- Renewal
- Subscription modification

Construct race scenarios:

Request A marks invoice paid.
Request B marks invoice paid simultaneously.

Request A creates payment.
Request B creates same payment simultaneously.

Request A cancels.
Request B pays.

Determine whether the database/application guarantees correctness.

If concurrency is not protected, classify severity based on actual financial impact.

============================================================
15. SOFT DELETE / QUERY FILTERS
============================================================

Inspect all Billing/Invoicing entities for:

- DeletedAtUtc
- DeletedBy
- IsDeleted
- HasQueryFilter

Determine:

- whether financial records should be soft deleted according to existing design
- whether deleted records remain visible through navigations
- whether unique indexes interact with soft deletion
- whether production code uses IgnoreQueryFilters
- whether deleted financial records can still be mutated
- whether deleted invoices can receive payments

Pay special attention to the shared AppDbContext query-filter implementation.

Verify that global filter composition does NOT accidentally remove:

- TenantId filter
- DeletedAtUtc filter

Also verify whether historical financial records can disappear from normal queries.

Do NOT invent a retention policy.

============================================================
16. AUDIT TRAIL
============================================================

Financial mutations must be traceable.

Inspect:

- CreationDate
- LastUpdateDate
- CreatorUserName
- LastUpdateUserName
- DeleteUserName
- audit logs
- domain events
- payment logs
- transaction references

Determine whether important transitions are auditable.

Especially:

- Invoice creation
- Invoice amount changes
- Payment creation
- Payment success
- Payment cancellation
- Refund
- subscription renewal
- financial adjustments

Flag cases where sensitive financial state changes without an adequate audit trail, but distinguish:

- missing technical audit fields
- missing business-level transition history

============================================================
17. EF CORE / DATABASE AUDIT
============================================================

For every financial entity verify:

- PK
- FK
- Tenant FK
- Delete behavior
- indexes
- unique indexes
- precision
- nullable fields
- concurrency tokens
- check constraints
- default values
- enum/status storage
- migration consistency

Compare:

Domain model
vs
EF configuration
vs
snapshot
vs
latest migration.

Run/read:

dotnet ef migrations has-pending-model-changes

if available.

DO NOT modify migrations.

Look for:

- model/migration drift
- missing columns
- incorrect precision
- missing indexes
- missing unique constraints
- incorrect FK delete behavior
- nullable financial values that should not be nullable according to existing rules
- missing concurrency columns

============================================================
18. CQRS / APPLICATION LAYER
============================================================

For every command/query verify:

- authorization
- validation
- tenant resolution
- entity ownership
- business invariants
- transaction
- concurrency
- domain behavior
- audit behavior

Look for handlers that:

- directly mutate entity state
- bypass domain methods
- trust client status
- trust client totals
- trust client TenantId
- call SaveChanges multiple times unnecessarily
- swallow exceptions
- convert financial errors to generic 500
- expose implementation details

Do not demand domain methods where the existing architecture intentionally uses application services.

Judge consistency with the actual architecture.

============================================================
19. VALIDATION
============================================================

Inspect all Billing/Invoicing validators.

Check:

- required IDs
- amounts
- precision
- ranges
- strings
- references
- dates
- status inputs
- currency
- external references

Most importantly:

Verify validators are actually executed.

Do not assume the existence of a validator means validation occurs.

Trace the MediatR/FluentValidation pipeline.

Look for:

- validators not registered
- commands bypassing pipeline
- controller manually constructing invalid commands
- validators inconsistent with EF limits

============================================================
20. API CONTRACT
============================================================

Audit every Billing/Invoicing endpoint.

For each:

METHOD
ROUTE
AUTH
PERMISSION
FEATURE
INPUT
OUTPUT
STATUS CODES
SIDE EFFECTS

Look for semantic inconsistencies such as:

- DELETE performing Cancel
- PUT allowing state transitions that should be commands
- POST allowing client-selected status
- GET exposing internal payment/provider data
- incorrect HTTP status codes
- generic 500 for known financial conflicts
- missing 404/403/409/422 handling
- endpoints that reveal existence of another tenant's financial object

Do not enforce REST ideology over existing business requirements.

Focus on correctness and security.

============================================================
21. ERROR HANDLING
============================================================

Inspect GlobalExceptionHandler and related middleware.

Determine how these are surfaced:

- validation failure
- unauthorized
- forbidden
- not found
- concurrency conflict
- unique constraint violation
- invalid state transition
- payment conflict
- duplicate transaction
- database failure

Financial conflicts must not silently become ambiguous 500 responses when the architecture has an established conflict/error convention.

Also verify sensitive information is not leaked.

============================================================
22. PAYMENT PROVIDER / EXTERNAL SYSTEM INTEGRATION
============================================================

If an external payment provider exists, audit:

- outbound request
- callback/webhook
- signature verification
- transaction reference
- amount verification
- currency verification
- invoice matching
- tenant matching
- replay protection
- duplicate callback handling
- timeout handling
- failure handling
- eventual consistency

CRITICAL:

Never trust a callback merely because it contains:

PaymentId
InvoiceId
Amount
Status

Verify whether the implementation cryptographically authenticates or otherwise validates the provider response.

If no provider integration exists, report:

"No external payment provider integration found."

Do NOT invent missing infrastructure as a defect.

============================================================
23. SECURITY ATTACK SCENARIOS
============================================================

Explicitly evaluate at least these attacks:

A. Cross-tenant Invoice ID enumeration
B. Cross-tenant Payment ID enumeration
C. Client-supplied TenantId
D. Client-supplied Invoice status
E. Client-supplied Payment status
F. Client-supplied PaidAt
G. Client-supplied amount manipulation
H. Duplicate payment replay
I. Concurrent payment race
J. Cancel-after-paid
K. Paid-after-cancelled
L. Payment against another tenant's invoice
M. Payment against deleted invoice
N. Unauthorized invoice cancellation
O. Unauthorized refund
P. Subscription billing after expiration/suspension
Q. Duplicate renewal
R. Invoice-number collision
S. External payment callback replay
T. Forged external payment success
U. Unique constraint bypass through soft delete
V. Query-filter bypass
W. Platform-vs-tenant financial data exposure

For each, state:

PASS / FAIL / NOT APPLICABLE / NOT DETERMINABLE

and evidence.

============================================================
24. TEST QUALITY AUDIT
============================================================

Inspect ALL existing Billing/Invoicing tests.

Do not only count tests.

Determine whether tests actually protect against the important failure modes.

Required coverage categories:

1. Tenant isolation
2. Authorization
3. Feature gating
4. Invoice creation
5. Invoice lifecycle
6. Payment lifecycle
7. Invalid state transitions
8. Amount validation
9. Duplicate payment
10. Idempotency if implemented
11. Concurrency
12. Transaction rollback
13. EF relational constraints
14. Migration/schema consistency
15. Soft-delete behavior
16. Subscription interaction
17. Cross-tenant mutation
18. Unauthorized mutation

Identify tests that are:

- vacuous
- testing only happy paths
- using InMemory where SQL Server behavior matters
- relying on unfiltered SingleAsync
- not asserting HTTP status
- not asserting persisted state
- not testing actual authorization
- not testing actual database constraints

Do NOT modify tests.

============================================================
25. TEST INFRASTRUCTURE
============================================================

Determine whether the test suite uses:

- InMemory
- SQLite
- SQL Server
- Testcontainers
- real migrations

For financial correctness, distinguish what InMemory can prove from what it cannot prove.

Especially verify:

- unique indexes
- precision
- concurrency
- FK behavior
- transaction behavior
- query filters

If SQL Server tests are missing, report the exact risk rather than automatically marking the whole module FAIL.

============================================================
26. BUSINESS RULE AMBIGUITY
============================================================

Separate actual defects from unspecified business rules.

Examples:

- Should an invoice be editable after issuance?
- Can an invoice be cancelled after payment?
- Can multiple payments partially settle an invoice?
- Can a payment exceed invoice amount?
- Should cancelled payments count toward uniqueness?
- How are refunds represented?
- How is tax calculated?
- How are currencies handled?
- When is an invoice considered overdue?

If implementation/documentation does not establish the answer:

DO NOT INVENT IT.

Record:

"Business decision required."

Do not classify an ambiguity as a security defect unless the current implementation creates an objectively unsafe condition.

============================================================
27. SEVERITY CLASSIFICATION
============================================================

Use:

CRITICAL
HIGH
MEDIUM
LOW
INFO

CRITICAL examples:

- Cross-tenant financial access
- Unauthorized payment manipulation
- Ability to create fake successful payments
- Financial duplication causing real monetary loss
- Race condition that can double-charge or double-credit
- Broken transaction causing materially inconsistent financial state

HIGH examples:

- Missing authorization on financial mutation
- Missing concurrency where financial corruption is possible
- Missing unique constraint enabling duplicate transactions
- Client-controlled financial state
- Incorrect money precision
- Payment replay vulnerability
- Invoice/payment state-machine bypass

MEDIUM examples:

- Incorrect API semantics
- Weak validation
- Missing audit detail
- Missing indexes with performance/security implications
- Ambiguous non-critical business behavior

LOW:

- Minor consistency issues
- Documentation
- Naming
- Non-critical test gaps

============================================================
28. EVIDENCE RULE
============================================================

EVERY finding must include concrete evidence.

For each finding provide:

ID
Severity
Title
Status
File
Class/Method/Property
Exact behavior
Why it matters
Attack/failure scenario
Recommended remediation

Do NOT report generic statements like:

"Authorization should be improved."

Instead:

"InvoiceController.Update lacks X permission and handler accepts invoiceId without ownership validation, allowing Y scenario."

Do not speculate.

If you cannot prove a finding from source:

Mark it:

NOT PROVEN

and explain what evidence is missing.

============================================================
29. DO NOT RE-AUDIT CLOSED PHASES
============================================================

Students and Teachers have already passed their independent verification.

Do NOT perform a full re-audit of:

- Students
- Teachers

unless Billing changes interact with shared infrastructure and a narrow regression check is necessary.

If you discover a shared infrastructure regression:

Report ONLY the regression and evidence.

Do not reopen closed modules without concrete evidence.

============================================================
30. REQUIRED OUTPUT
============================================================

Produce a formal report:

# PHASE 6 — BILLING & INVOICING
# PRODUCTION FINANCIAL CORRECTNESS & SECURITY AUDIT

## 1. Executive Summary

Verdict:

- APPROVED
- APPROVED WITH CONDITIONS
- NOT APPROVED

Explain why.

## 2. Scope

List exactly what was audited.

## 3. Inventory

List:

- Entities
- Aggregates
- Commands
- Queries
- Validators
- Controllers
- Services
- EF configurations
- DbSets
- Migrations
- Permissions
- Features
- Tests

## 4. Financial Domain Model

Explain the actual financial model implemented.

Do not invent missing concepts.

## 5. Invoice State Machine

Provide actual states and transitions.

## 6. Payment State Machine

Provide actual states and transitions.

## 7. Critical Findings

Table:

| ID | Severity | Finding | Evidence | Impact |
|----|----------|---------|----------|--------|

## 8. Tenant Isolation

Detailed PASS/FAIL evidence.

## 9. Authorization & Feature Gating

Detailed endpoint-level findings.

## 10. Money Correctness

Precision, scale, calculations, validation, ownership.

## 11. Idempotency & Duplicate Protection

Detailed findings.

## 12. Transactions

Detailed findings and failure scenarios.

## 13. Concurrency

Detailed findings.

## 14. Database / EF

Model vs configuration vs migration.

## 15. Soft Delete

Detailed findings.

## 16. Auditability

Detailed findings.

## 17. API Contract

Endpoint-level issues.

## 18. External Payment Integration

Only if implemented.

## 19. Test Coverage & Test Quality

Explain what is genuinely covered.

## 20. Business Decisions Required

ONLY genuine ambiguities.

## 21. Complete Findings Register

Separate:

CRITICAL
HIGH
MEDIUM
LOW
INFO

## 22. Recommended Remediation Plan

Order fixes by risk.

For each remediation:

- Finding ID
- Exact files likely affected
- Required behavior
- Required tests
- Migration requirement if any
- Whether production data migration is required

Do NOT write code.

## 23. Final Verdict

Use exactly one:

APPROVED

APPROVED WITH CONDITIONS

NOT APPROVED

============================================================
31. FINAL RULES
============================================================

1. DO NOT MODIFY ANYTHING.
2. DO NOT CREATE MIGRATIONS.
3. DO NOT FIX TESTS.
4. DO NOT "CLEAN UP" CODE.
5. DO NOT redesign the Billing architecture.
6. DO NOT invent business rules.
7. DO NOT assume previous reports are still correct.
8. CURRENT SOURCE CODE IS THE SOURCE OF TRUTH.
9. Existing documentation is context, not proof.
10. Prove every finding from source.
11. Financial correctness has priority over stylistic concerns.
12. Tenant isolation and payment integrity have the highest priority.
13. Treat client-controlled financial state as highly suspicious.
14. Treat duplicate payments and concurrency as high-risk.
15. Treat database constraints as part of the financial security boundary.
16. Distinguish implementation defects from business decisions.
17. Distinguish missing tests from proven production defects.
18. Do not mark something PASS merely because an attribute exists; trace its execution.
19. Do not mark something FAIL merely because a preferred architecture is absent; prove that the absence creates an actual risk.
20. Do not modify source code even if a critical vulnerability is discovered.

At the end provide a concise:

### FINAL DECISION

with:

VERDICT:
CRITICAL FINDINGS:
HIGH FINDINGS:
MEDIUM FINDINGS:
LOW FINDINGS:
TEST STATUS:
MIGRATION STATUS:
PRODUCTION READINESS:

The purpose of this audit is to determine whether the IMPLEMENTED Billing/Invoicing module is financially safe, tenant-safe, authorization-safe, concurrency-safe, transactionally correct, and production-ready.