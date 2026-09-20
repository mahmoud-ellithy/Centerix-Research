# TASK 16 - PRODUCTION READINESS, END-TO-END BUSINESS INTEGRITY AND ADVERSARIAL SECURITY AUDIT

**Audit Date:** 2026-09-20
**Auditor:** Senior Business / Security / QA / Architecture Review
**Scope:** Complete Centerix multi-tenant education center management system
**Repository State:** Current HEAD (all phases 1-15 implemented)

---

# 1. Executive Summary

## Overall Production Readiness: **NOT READY**

The Centerix system demonstrates **strong architectural foundations** in several areas: the multi-tenant authorization pipeline, payment/refund concurrency protection, and domain event architecture are notably well-implemented. However, the system contains **critical gaps** that must be resolved before production deployment.

### Classification: NOT READY

**Evidence for NOT READY:**

- CRITICAL-01: Contract to Subscription snapshot gap - commercial terms (price, currency, duration, benefits) are discarded when creating a subscription from a contract
- CRITICAL-02: Upgrade/downgrade does not handle unused subscription period - customers lose prepaid time with no refund or credit
- CRITICAL-03: TenantAdmin cannot manage subscriptions (Subscriptions.Manage is platform-only) yet TenantPlans controller endpoints require Subscriptions.Manage - blocking tenant admins from self-service
- CRITICAL-04: Contract and Offer entities lack RowVersion - no optimistic concurrency on high-value financial entities
- CRITICAL-05: Multiple financial entities lack FK constraints - referential integrity depends entirely on application code

### Areas of Genuine Strength

- TenantGuardMiddleware pipeline is well-designed with fail-closed defaults
- AllocatePayment, ExecuteRefund, and ApplyCreditToInvoice handlers have enterprise-grade concurrency protection
- Permission system correctly separates platform-scoped vs tenant-scoped operations
- Domain entities use private constructors + factory methods + Result monad consistently
- Financial entities are fully immutable after creation
- Domain events enable audit trail and decoupled workflows

---

# 2. Current End-to-End Business Flow

Tenant (created by PlatformAdmin) -> Plan (global catalog) -> Promotion (global catalog) -> Offer (tenant-scoped, calculated by platform) -> Contract (tenant-scoped, created from Offer) -> Subscription/TenantPlan (tenant-scoped, created from Contract) -> BillingCycle -> Invoice -> Payment -> PaymentAllocation -> CreditApplication -> CustomerLedgerEntry -> Refund -> RefundAllocation -> Installment -> Cancellation / Settlement / Renewal / Upgrade / Downgrade

**Known gap:** The flow from Contract to Subscription discards commercial terms (see CRITICAL-01).

---

# 3. Actor and Authorization Model

## Discovered Actors

| Actor | Auth | Tenant Required | Role | Scope |
|-------|------|-----------------|------|-------|
| Anonymous | None | No | None | Public endpoints only |
| Authenticated (no membership) | JWT | No | Any | Denied at authorization |
| TenantUser | JWT | Yes (active) | TenantUser | Read-only tenant resources |
| TenantAdmin | JWT | Yes (active) | TenantAdmin | Read/write tenant (no billing) |
| PlatformAdmin | JWT | No (bypasses) | PlatformAdmin | Full cross-tenant access |

## Authorization Pipeline (in order)

1. Finbuckle MultiTenant - resolves tenant from request
2. Authentication - JWT validation
3. TenantGuardMiddleware - validates tenant membership, loads permissions, checks expiry
4. Fallback Policy - RequireAuthenticatedUser
5. [HasPermission] - policy-based permission check (PlatformAdmin bypasses)
6. [RequireFeature] - subscription feature entitlement check
7. PlatformAdminGuard - imperative guard for platform operations

## Key Design Properties

- JWT is tenant-agnostic and permission-free
- Permissions resolved per-request from database
- Fail-closed: Empty permissions = denied
- PlatformAdmin bypass is total via JWT claims

---

# 4. Authorization Matrix

## Platform-Scoped Operations

| Operation | Anon | TenantUser | TenantAdmin | PlatformAdmin | Evidence |
|-----------|------|------------|-------------|---------------|----------|
| Create Plan | DENY | DENY | DENY | ALLOW | Permissions.Plans.Create (PlatformScope) |
| Modify Plan | DENY | DENY | DENY | ALLOW | Permissions.Plans.Update (PlatformScope) |
| Delete Plan | DENY | DENY | DENY | ALLOW | Permissions.Plans.Delete (PlatformScope) |
| Create Feature | DENY | DENY | DENY | ALLOW | Permissions.Features.Create (PlatformScope) |
| Create Promotion | DENY | DENY | DENY | ALLOW | Permissions.Promotions.Create (PlatformScope) |
| Create Tenant | DENY | DENY | DENY | ALLOW | Permissions.Tenants.Create (PlatformScope) |
| Approve Tenant | DENY | DENY | DENY | ALLOW | Permissions.Subscriptions.Manage (PlatformScope) |
| Suspend Tenant | DENY | DENY | DENY | ALLOW | Permissions.Tenants.Update (PlatformScope) |
| Manage PlatformUsers | DENY | DENY | DENY | ALLOW | Permissions.PlatformUsers.* (PlatformScope) |

## Tenant-Scoped Operations

| Operation | Anon | TenantUser | TenantAdmin | PlatformAdmin | Evidence |
|-----------|------|------------|-------------|---------------|----------|
| View Branches | DENY | ALLOW | ALLOW | ALLOW | Permissions.Branches.Read |
| Create Branch | DENY | DENY | ALLOW | ALLOW | Permissions.Branches.Create |
| View Students | DENY | ALLOW | ALLOW | ALLOW | Permissions.Students.Read |
| Create Student | DENY | DENY | ALLOW | ALLOW | Permissions.Students.Create |
| View Teachers | DENY | ALLOW | ALLOW | ALLOW | Permissions.Teachers.Read |
| Create Teacher | DENY | DENY | ALLOW | ALLOW | Permissions.Teachers.Create |
| View Subscriptions | DENY | ALLOW | ALLOW | ALLOW | Permissions.TenantPlans.Read |
| Manage Subscriptions | DENY | DENY | DENY | ALLOW | Permissions.Subscriptions.Manage (PlatformScope) |
| Create Invoice | DENY | DENY | DENY | ALLOW | Permissions.Invoices.Create |
| Create Payment | DENY | DENY | DENY | ALLOW | Permissions.Payments.Create |
| Allocate Payment | DENY | DENY | DENY | ALLOW | Permissions.Payments.Manage |
| Create Refund | DENY | DENY | DENY | ALLOW | Permissions.Refunds.Create |
| Approve Refund | DENY | DENY | DENY | ALLOW | Permissions.Refunds.Approve |
| Execute Refund | DENY | DENY | DENY | ALLOW | Permissions.Refunds.Execute |
| Create Credit | DENY | DENY | DENY | ALLOW | Permissions.TenantCredits.Create |
| Apply Credit | DENY | DENY | DENY | ALLOW | Permissions.TenantCredits.Apply |
| Create Contract | DENY | DENY | DENY | ALLOW | Permissions.Contracts.Create |
| Accept Offer | DENY | DENY | ALLOW | ALLOW | Permissions.Offers.Accept |
| View Invoices | DENY | DENY | DENY | ALLOW | Permissions.Invoices.Read |
| Create Invitation | DENY | DENY | ALLOW | ALLOW | Permissions.Invitations.Create |

## CRITICAL NOTE on TenantAdmin Billing Access

TenantAdmin has **NO access** to: Invoices, Payments, Refunds, Credits, Installments, Contracts, or Subscriptions.Manage. All financial operations are PlatformAdmin-only.

---

# 5. Cross-Module Business Invariants

## Invariant 1: Subscription Active Limit
**Status:** ENFORCED (DB-level)
- Filtered unique index on TenantId with filter Status IN (1, 4, 5)
- At most one non-terminal subscription per tenant

## Invariant 2: PaymentAllocation Idempotency
**Status:** ENFORCED (DB-level)
- Filtered unique index on (TenantId, PaymentId, InvoiceId, InstallmentId, AllocatedAmount) with filter Status = Active

## Invariant 3: Refund Number Uniqueness
**Status:** ENFORCED (DB-level)
- Tenant-scoped unique index on RefundNumber
- One cancellation refund per subscription (filtered)

## Invariant 4: Refund Idempotency
**Status:** ENFORCED (DB-level)
- Unique index on (TenantId, IdempotencyKey) (filtered, IS NOT NULL)

## Invariant 5: Credit Application Idempotency
**Status:** ENFORCED (DB-level)
- Unique index on (TenantId, IdempotencyKey) (filtered, non-empty)

## Invariant 6: Customer Ledger Deduplication
**Status:** ENFORCED (DB-level)
- Three filtered unique indexes prevent duplicate settlement/usage entries

## Invariant 7: Invoice Immutability After Issue
**Status:** ENFORCED (Domain-level)
- Financial fields are private set, set once at creation
- Issue() requires Draft status; Cancel() only from Draft

## Invariant 8: Payment Immutability After Completion
**Status:** ENFORCED (Domain-level)
- All financial fields are private set
- Complete() requires Pending/Processing status

## Invariant 9: Contract Commercial Snapshot Immutability
**Status:** ENFORCED (Domain-level)
- All commercial fields are private set, set only in factory method
- Modifying original Plan does NOT change existing Contract

## Invariant 10: Subscription Commercial Snapshot Immutability
**Status:** ENFORCED (Domain-level)
- SnapshotPrice, DurationMonths, limit snapshots are private set
- Modifying original Plan does NOT change existing Subscription

---

# 6. Contract / Subscription Snapshot Integrity

## CRITICAL GAP: Contract to Subscription Data Loss

**Evidence:**
- CreateSubscriptionFromContractCommand.cs:59-64 calls subscriptionFactory.CreateActivatedAsync(contract.TenantId, contract.PlanId, ...)
- SubscriptionFactory.CreateActivatedAsync() reads ALL commercial terms from the Plan catalog entity (mutable):
  - plan.MonthlyPrice (not contract.MonthlyListPrice)
  - plan.CurrencyCode (not contract.CurrencyCode)
  - plan.DurationMonths (not contract.DurationMonths)
  - plan.BonusMonths (not from contract)
  - plan.MaxStudents/MaxUsers/... (not from contract)
- Features resolved from plan.PlanFeatures (not from contract.Benefits)

**Impact:** A tenant with a negotiated discount will have a subscription reflecting the full Plan catalog price. The Contract commercial terms are effectively invisible to the Subscription.

**What's preserved:** PlanId, ContractId (FK link)
**What's lost:** MonthlyListPrice, ContractedAmount, DiscountAmount, ChargedMonths, PromotionReference, PricingTiers, Benefits

**Assessment:** INFERENCE -- The CreateFromSnapshotAsync factory method exists and accepts explicit commercial parameters, but CreateSubscriptionFromContractCommand does not use it.

---

# 7. Subscription / Billing Cycle / Invoice

## Subscription State Machine
Pending -> Active -> PastDue -> Suspended -> Active (recovery)
Pending/Active/PastDue/Suspended -> Cancelled
Active/PastDue/Suspended -> Expired

## Billing Cycle State Machine
Draft -> Invoiced -> Paid
Draft/Invoiced -> Cancelled

## Invoice State Machine
Draft -> Issued -> (Sent) -> PartiallyPaid -> Paid
Draft -> Cancelled

## Evidence
- Invoice immutability: TotalAmount, Subtotal, DiscountAmount, TaxAmount are private set
- Invoice status derived from payments: UpdatePaymentStatus() computes from allocations/credits
- Invoice cannot be modified after issue: Issue() only from Draft

## Gap: No RowVersion on BillingCycle
- BillingCycle lacks RowVersion - concurrent updates could silently last-write-wins

---

# 8. Payment / Allocation / Credit

## Payment Concurrency Protection
- RowVersion: Yes
- AllocatePayment handler: Serializable isolation + deadlock retry (3x) + UPDLOCK/ROWLOCK/HOLDLOCK + idempotency
- Idempotency: Exact-match allocation lookup
- Lock ordering: Payment first, then Invoice

## Credit Application Concurrency Protection
- RowVersion: Yes
- ApplyCreditToInvoice handler: Serializable isolation + deadlock retry (3x) + UPDLOCK/ROWLOCK/HOLDLOCK + idempotency via IdempotencyKey

## Overpayment Handling
- Payment allocation capped at invoice remaining amount
- Excess automatically creates TenantCredit (overpayment credit)
- CustomerLedgerEntry (CreditCreation) recorded

## Gap: FK Constraints Missing
- TenantCredit.SourceId and ReversalOfCreditId are bare GUIDs without FK constraints
- CreditApplication.CreditId and InvoiceId lack FK constraints
- CustomerLedgerEntry has 6 FK-like columns without referential integrity enforcement
- Installment.ContractId not a declared FK
- Contract.PlanId not a declared FK

---

# 9. Refund / Cancellation / Settlement

## Refund State Machine
Pending -> Approved -> Processing -> Completed
Pending -> Approved -> Cancelled
Pending -> Rejected
Pending -> Processing -> Failed
Pending -> Cancelled
Pending -> Completed (direct, optional approval)

## Refund Execution Concurrency Protection
- RowVersion: Yes
- ExecuteRefund handler: Serializable isolation + deadlock retry (3x) + UPDLOCK/ROWLOCK/HOLDLOCK on Payment rows + idempotency via IdempotencyKey
- Lock scope: Each referenced Payment locked via raw SQL

## Refund Calculation
- Uses Contract pricing tiers for elapsed period
- Benefit consumption: day-based pro-ratio
- Only Completed payments counted; only allocations where Invoice.ContractId == contract.Id
- Formula: RefundableAmount = AmountActuallyPaid - (UsedSubscriptionAmount + RemainingBenefitValue)

## Gap: CreateRefund/ApproveRefund Lack Concurrency Protection
- No transaction isolation or deadlock handling on these handlers

---

# 10. Renewal / Upgrade / Downgrade

## Renewal (Legacy)
- Mutates existing subscription in-place
- Appends months; anchor = max(EffectiveEndsAtUtc, utcNow)
- No new commercial snapshot created

## Renewal (Commercial)
- Creates new Offer, Contract, Subscription chain
- Fresh commercial calculation on current Plan/Promotion
- Old subscription cancelled

## Upgrade/Downgrade
- Creates new Offer, Contract, Subscription, BillingCycle, Invoice
- Old subscription cancelled
- No unused-period refund or credit - customer loses remaining time
- SERIALIZABLE transaction with overlap detection and deadlock retry (3x)

## Known Limitation
No automatic unused-subscription refund/credit on upgrade/downgrade.

---

# 11. Installments

## Installment State Machine
Pending -> PartiallyPaid -> Paid
Pending -> Overdue -> PartiallyPaid -> Paid
Pending -> Cancelled

## Evidence
- Status is server-derived from DueDateUtc, SettledAmount, and current time
- SettledAmount derived from allocations, never set by clients
- DB-level uniqueness: (TenantId, ContractId, SequenceNumber)
- RowVersion: Yes
- Contract FK: Not enforced at DB level (bare GUID)
- SettledAmount invariant: Not enforced at DB level

---

# 12. Tenant Isolation

## Tenant Resolution Flow
1. Finbuckle resolves tenant from request header/host
2. TenantGuardMiddleware validates active membership in that tenant
3. CurrentTenant.AuthorizeTenant() locks the verified tenant ID
4. TenantInterceptor stamps authorized tenant ID on new entities
5. Global query filter on IHasTenantId entities ensures isolation

## Fail-Closed Design
- CurrentTenant.TenantId returns empty string until AuthorizeTenant() is called
- TenantInterceptor stamps nothing when TenantId is empty
- All IHasTenantId queries return nothing when TenantId is empty

## Cross-Tenant Protection Evidence
- TenantGuardMiddleware explicitly checks TenantMemberships table
- AllocatePayment: explicit payment.TenantId != invoice.TenantId check
- ExecuteRefund: payments filtered by TenantId
- ApplyCreditToInvoice: explicit credit.TenantId != invoice.TenantId check
- CreateRefund: subscription/invoice validated against contract.TenantId

## Gap: No Cross-Tenant Tests for Billing/Financial Entities
- No HTTP-level cross-tenant tests for Invoices, Payments, Refunds, Credits

---

# 13. Authentication / Authorization / Privilege Escalation

## JWT Configuration
- HMAC-SHA256 symmetric signing
- Claims: NameIdentifier, Name, Email, Role
- No TenantId claim (tenant resolved from header)
- No Permission claim (resolved per-request from database)
- Expiry: 60 minutes, ClockSkew: Zero

## Refresh Token Security
- Tokens stored as SHA-256 hash
- Token rotation on refresh (old consumed, new pair issued)
- Reuse detection: replaying rotated token revokes entire chain
- Single-token and global revocation supported

## PlatformAdmin Bypass
- Both PermissionAuthorizationHandler and FeatureAuthorizationHandler grant unconditional success for PlatformAdmin
- JWT compromise = full platform takeover
- No additional DB verification after JWT validation

## Privilege Escalation Analysis
- TenantAdmin CANNOT elevate to PlatformAdmin
- TenantAdmin CANNOT access Subscriptions.Manage (PlatformScope)
- TenantAdmin CANNOT access billing operations
- PlatformAdmin bypass is via JWT claims, not DB verification

## Gap: Permission Loading Exception Swallowing
- TenantGuardMiddleware catches permission loading exceptions and continues
- User proceeds with zero permissions
- Downstream PermissionAuthorizationHandler compensates (fail-closed)

---

# 14. IDOR / BOLA / Mass Assignment

## IDOR Analysis
- Global query filters provide first-level isolation
- TenantGuardMiddleware provides membership verification
- Financial handlers add explicit cross-tenant checks
- Combination is robust but relies on every entity implementing IHasTenantId

## Mass Assignment Analysis
| Sensitive Property | Client-Settable? | Server Override? |
|-------------------|-----------------|-------------------|
| TenantId | No | TenantInterceptor stamps |
| CreatorUserName | No | Set from ICurrentUser |
| Status | No | Domain methods only |
| ApprovedBy | No | Set from ICurrentUser |
| Amount (Refund) | No | Calculated by service |
| Currency | No | Derived from Contract/Plan |
| Role | No | Server-controlled |

No mass assignment vulnerabilities found.

---

# 15. Concurrency / Race Conditions

## Heavy Concurrency Protection (Enterprise-Grade)
| Handler | Isolation | Retry | Locking | Idempotency |
|---------|-----------|-------|---------|-------------|
| AllocatePayment | Serializable | 3x deadlock retry | UPDLOCK/ROWLOCK/HOLDLOCK | Exact-match |
| ExecuteRefund | Serializable | 3x deadlock retry | UPDLOCK/ROWLOCK/HOLDLOCK | IdempotencyKey |
| ApplyCreditToInvoice | Serializable | 3x deadlock retry | UPDLOCK/ROWLOCK/HOLDLOCK | IdempotencyKey |
| ChangeSubscriptionPlan | Serializable | 3x deadlock retry | Overlap detection | None |

## No Concurrency Protection
| Handler | Risk |
|---------|------|
| CreatePayment | Low (create-only) |
| CreateRefund | Medium (concurrent creates) |
| ApproveRefund | Medium (concurrent approve) |
| IssueInvoice | Medium (concurrent issue) |
| MarkInvoicePaid | Medium (concurrent status) |
| CancelInvoice | Medium (concurrent cancel) |

---

# 16. EF / Database Integrity

## RowVersion Coverage
| Entity | RowVersion | Assessment |
|--------|-----------|------------|
| Invoice | Yes | Protected |
| Payment | Yes | Protected |
| PaymentAllocation | Yes | Protected |
| Refund | Yes | Protected |
| TenantCredit | Yes | Protected |
| CreditApplication | Yes | Protected |
| CustomerLedgerEntry | Yes | Protected |
| Installment | Yes | Protected |
| TenantPlan | Yes | Protected |
| **Contract** | **NO** | **GAP** |
| **Offer** | **NO** | **GAP** |
| **BillingCycle** | **NO** | **GAP** |

## FK Constraint Coverage
| Entity | Missing FKs | Risk |
|--------|-------------|------|
| TenantCredit | SourceId, ReversalOfCreditId | MEDIUM |
| CreditApplication | CreditId, InvoiceId | MEDIUM |
| CustomerLedgerEntry | 6 FK-like columns | HIGH |
| Installment | ContractId | MEDIUM |
| Contract | PlanId | MEDIUM |
| Offer | PlanId | LOW |

## Amount Precision Inconsistency
- Invoice and TenantCredit: HasPrecision(10,2) - max ~99M
- Payment and Refund: HasPrecision(18,2) - max ~999T

---

# 17. Test Suite Assessment

## Test Inventory Summary
| Category | Count | Coverage |
|----------|-------|----------|
| Cross-tenant isolation (HTTP) | 7 | Good for education module only |
| Cross-tenant (handler) | 29 | Excellent |
| TenantGuardMiddleware | 13 | Excellent |
| Invitation flow (SQL Server) | 12 | Excellent |
| Authorization matrix (HTTP) | 20+ | Good |
| Feature gating (HTTP) | 15+ | Excellent |
| Subscription lifecycle | 16+ | Good |
| Financial ledger hardening | 26 | Good |
| Invoice financial integrity | 33 | Good |
| Credit application | 24 | Good |
| SQL Server concurrency | 15+ | Good |
| Contract domain | 21+ | Good |

## What Is Tested
- TenantGuardMiddleware behavior (bypass paths, membership, expiry, cross-tenant)
- Education module authorization (Students, Teachers, Branches)
- Subscription state machine (Pending/Active/PastDue/Suspended/Cancelled/Expired)
- Financial ledger hardening (payment amounts, allocation bounds, invoice settlement)
- Invoice financial integrity (amount immutability, credit applications, ledger entries)
- SQL Server concurrency (payment allocation, refund execution, deadlock scenarios)
- Invitation flow end-to-end (register, accept, revoke, cross-tenant rejection)

## What Is NOT Tested
- HTTP-level cross-tenant access for billing entities (Invoices, Payments, Refunds, Credits)
- Anonymous access to financial endpoints
- IDOR via ID manipulation on billing endpoints
- Concurrent refund creation/approval
- Contract snapshot integrity (Contract -> Subscription data flow)
- Upgrade/downgrade without unused period handling
- Mass assignment attempts on financial DTOs
- Token tampering / JWT validation edge cases
- Plan mutation affecting existing subscriptions (snapshot isolation proof)
- Refund after full refund (double refund prevention at HTTP level)
- Credit application after refund
- Concurrent credit application to same invoice

---

# 18. Documentation vs Implementation

## Implemented + Documented
- Multi-tenant authorization pipeline
- Permission system (Platform vs Tenant scoped)
- Subscription state machine
- Invoice lifecycle
- Payment allocation with concurrency protection
- Refund calculation engine
- Contract commercial snapshot
- Installment management
- Credit application with idempotency
- Feature gating (Students, Teachers)

## Implemented + NOT Fully Documented
- TenantGuardMiddleware pipeline and bypass logic
- Refund execution enterprise concurrency (UPDLOCK + deadlock retry)
- CustomerLedgerEntry deduplication indexes
- PlatformAdminGuard imperative boundary
- Permission loading exception handling

## Documented + Known Gaps
- Upgrade/downgrade unused period handling (documented as limitation)
- Contract -> Subscription snapshot gap (not previously identified)
- Cross-tenant financial entity test coverage (identified gap)

---

# 19. Critical Findings

| ID | Finding | Type | Severity | Evidence | Business/Security Impact |
|----|---------|------|----------|----------|--------------------------|
| CF-01 | Contract commercial terms lost when creating Subscription | GAP | CRITICAL | CreateSubscriptionFromContractCommand.cs:59-64, SubscriptionFactory.cs:85-101 | Negotiated discounts, promotions, and custom pricing are invisible to the subscription. Tenant pays full catalog price. |
| CF-02 | Upgrade/downgrade discards unused subscription period | GAP | CRITICAL | ChangeSubscriptionPlanHandler.cs:326 | Customer loses prepaid time with no refund or credit on plan change. |
| CF-03 | TenantAdmin blocked from all billing operations | GAP | HIGH | Permissions.GetTenantAdminPermissions() excludes Invoices/Payments/Refunds/Credits | TenantAdmin cannot view invoices, make payments, or manage any financial aspect of their subscription. All billing requires PlatformAdmin intervention. |
| CF-04 | Contract entity lacks RowVersion | GAP | HIGH | ContractConfiguration.cs - no IsRowVersion() | Concurrent contract modifications (e.g., dual renewal race) can silently last-write-wins on a financial entity. |
| CF-05 | Offer entity lacks RowVersion | GAP | MEDIUM | OfferConfiguration.cs - no IsRowVersion() | Concurrent offer state transitions could race. |
| CF-06 | BillingCycle lacks RowVersion | GAP | MEDIUM | BillingCycleConfiguration.cs - no IsRowVersion() | Concurrent billing cycle updates could race. |
| CF-07 | CustomerLedgerEntry has 6 FK-like columns without FK constraints | GAP | HIGH | CustomerLedgerEntryConfiguration.cs | Orphaned ledger entries possible if application code has bugs. Ledger is financial source of truth. |
| CF-08 | TenantCredit remaining-amount invariant not enforced at DB level | GAP | MEDIUM | TenantCreditConfiguration.cs - no CHECK constraint | RemainingAmount could exceed Amount or go negative without DB-level protection. |
| CF-09 | Installment SettledAmount invariant not enforced at DB level | GAP | MEDIUM | InstallmentConfiguration.cs - no CHECK constraint | SettledAmount could exceed Amount without DB-level protection. |
| CF-10 | Amount precision inconsistency across entities | GAP | LOW | Invoice: 10,2 vs Payment: 18,2 | High-value operations could be truncated on Invoice side. |

---

# 20. Security Findings

| ID | Finding | Actor | Attack Surface | Evidence | Severity |
|----|---------|-------|----------------|----------|----------|
| SF-01 | PlatformAdmin JWT bypass grants unconditional cross-tenant access | PlatformAdmin | JWT token compromise | PermissionAuthorizationHandler.cs:76-81 | HIGH |
| SF-02 | Permission loading exception swallowing in middleware | Any authenticated | DB transient fault | TenantGuardMiddleware.cs:94-98 | MEDIUM |
| SF-03 | No HTTP-level cross-tenant tests for financial entities | Tenant A | Billing endpoints | No test evidence | MEDIUM |
| SF-04 | Contract lacks RowVersion enabling lost-update on financial data | PlatformAdmin | Concurrent contract modification | ContractConfiguration.cs | HIGH |
| SF-05 | CreateRefund/ApproveRefund lack concurrency protection | PlatformAdmin | Concurrent refund operations | CreateRefundCommand.cs, ApproveRefundCommand.cs | MEDIUM |
| SF-06 | No DB-level referential integrity on CustomerLedgerEntry | Any | Application bug | CustomerLedgerEntryConfiguration.cs | HIGH |
| SF-07 | Unauthenticated requests bypass TenantGuardMiddleware entirely | Anonymous | Missing [Authorize] on endpoint | TenantGuardMiddleware.cs logic | LOW |
| SF-08 | Entities not implementing IHasTenantId would leak across tenants | Developer | New entity addition | IHasTenantId.cs + TenantInterceptor.cs | MEDIUM |

---

# 21. Business Integrity Findings

| ID | Finding | Business Area | Evidence | Severity |
|----|---------|---------------|----------|----------|
| BF-01 | Contract -> Subscription snapshot gap | Subscription/Contract | CreateSubscriptionFromContractCommand.cs | CRITICAL |
| BF-02 | No unused period refund on upgrade/downgrade | Upgrade/Downgrade | ChangeSubscriptionPlanHandler.cs | CRITICAL |
| BF-03 | TenantAdmin cannot access any billing operations | Billing/RBAC | Permissions.cs GetTenantAdminPermissions() | HIGH |
| BF-04 | Renewal (legacy) does not create new commercial snapshot | Renewal | RenewSubscriptionHandler.cs | MEDIUM |
| BF-05 | ChangeSubscriptionPlan uses current Plan price, not tier-based | Upgrade/Downgrade | ChangeSubscriptionPlanHandler.cs:180 | MEDIUM |
| BF-06 | InvoiceNumber globally unique (not tenant-scoped) | Invoicing | InvoiceConfiguration.cs | LOW |
| BF-07 | PaymentNumber globally unique (not tenant-scoped) | Payments | PaymentConfiguration.cs | LOW |

---

# 22. Production Readiness Gaps

1. **Contract -> Subscription snapshot:** Commercial terms from Contract are not carried to Subscription
2. **Upgrade/downgrade proration:** No unused period refund/credit
3. **TenantAdmin billing access:** All financial operations require PlatformAdmin
4. **Missing RowVersion:** Contract, Offer, BillingCycle lack optimistic concurrency
5. **Missing FK constraints:** 10+ FK-like columns lack referential integrity
6. **Missing DB invariants:** TenantCredit.RemainingAmount, Installment.SettledAmount
7. **Missing cross-tenant financial tests:** No HTTP-level IDOR tests for billing
8. **Amount precision inconsistency:** Mixed 10,2 and 18,2 across entities

---

# 23. Recommended Target Model

Only where strictly necessary:

1. **Fix SubscriptionFactory usage:** Change CreateSubscriptionFromContractCommand to call CreateFromSnapshotAsync with Contract commercial terms
2. **Add RowVersion:** To Contract, Offer, and BillingCycle entities
3. **Add FK constraints:** To CustomerLedgerEntry, TenantCredit, CreditApplication, Installment, Contract.PlanId
4. **Add DB CHECK constraints:** TenantCredit.RemainingAmount >= 0 AND <= Amount; Installment.SettledAmount >= 0 AND <= Amount
5. **Unify amount precision:** Standardize on HasPrecision(18,2) across all monetary fields
6. **Add cross-tenant HTTP tests:** For Invoices, Payments, Refunds, Credits endpoints
7. **Add upgrade/downgrade proration:** Refund or credit for unused subscription period

---

# 24. Prioritized Backlog

| Priority | ID | Problem | Proposed Solution | Modules |
|----------|-----|---------|-------------------|---------|
| P0 | CF-01 | Contract commercial terms lost at Subscription creation | Fix SubscriptionFactory call in CreateSubscriptionFromContractCommand | Application/Subscriptions |
| P0 | CF-02 | No unused period refund on upgrade/downgrade | Add proration logic to ChangeSubscriptionPlanHandler | Application/Commands |
| P1 | CF-04 | Contract lacks RowVersion | Add RowVersion property + EF configuration | Domain/Contracts, Infrastructure |
| P1 | CF-07 | CustomerLedgerEntry missing FK constraints | Add FK relationships in EF configuration | Infrastructure/Configurations |
| P1 | CF-08 | TenantCredit remaining-amount invariant | Add CHECK constraint or domain validation | Domain/Infrastructure |
| P2 | CF-03 | TenantAdmin billing access design decision | Document decision or adjust role permissions | Application/Auth |
| P2 | CF-05 | Offer lacks RowVersion | Add RowVersion property + EF configuration | Domain/Promotions, Infrastructure |
| P2 | CF-06 | BillingCycle lacks RowVersion | Add RowVersion property + EF configuration | Domain/Billing, Infrastructure |
| P2 | SF-03 | Missing cross-tenant financial tests | Add HTTP-level IDOR tests | Tests |
| P2 | CF-09 | Installment SettledAmount invariant | Add CHECK constraint | Infrastructure |
| P3 | CF-10 | Amount precision inconsistency | Standardize to HasPrecision(18,2) | Infrastructure/Configurations |
| P3 | SF-05 | CreateRefund/ApproveRefund concurrency | Add transaction isolation | Application/Billing |

---

# 25. Evidence Index

| Finding | File | Class | Method | Behavior |
|---------|------|-------|--------|----------|
| CF-01 | src/Centerix.Application/Platform/Contracts/Commands/CreateSubscriptionFromContractCommand.cs | CreateSubscriptionFromContractHandler | Handle() | Calls CreateActivatedAsync instead of CreateFromSnapshotAsync |
| CF-01 | src/Centerix.Application/Platform/Subscriptions/SubscriptionFactory.cs | SubscriptionFactory | CreateActivatedAsync() | Reads pricing from Plan, not Contract |
| CF-02 | src/Centerix.Application/Platform/Commands/ChangeSubscriptionPlanCommand.cs | ChangeSubscriptionPlanHandler | Handle() | Calls oldSubscription.Cancel() without proration |
| CF-03 | src/Centerix.Infrastructure/Auth/Permissions.cs | Permissions | GetTenantAdminPermissions() | Excludes billing permissions |
| CF-04 | src/Centerix.Infrastructure/Data/Configurations/ContractConfiguration.cs | ContractConfiguration | Configure() | No IsRowVersion() call |
| CF-07 | src/Centerix.Infrastructure/Data/Configurations/CustomerLedgerEntryConfiguration.cs | CustomerLedgerEntryConfiguration | Configure() | No FK relationships defined |
| SF-01 | src/Centerix.Infrastructure/Auth/PermissionPolicyProvider.cs | PermissionAuthorizationHandler | HandleRequirementAsync() | PlatformAdmin unconditional bypass |
| SF-02 | src/Centerix.API/Infrastructure/TenantGuardMiddleware.cs | TenantGuardMiddleware | InvokeAsync() | Exception swallowing in permission loading |

---

# 26. Testers Attack Scenarios

## Security Tests
1. Anonymous access to GET /api/Invoices
2. Anonymous access to POST /api/Payments
3. Anonymous access to POST /api/Refunds
4. TenantUser accessing POST /api/Invoices (Create)
5. TenantAdmin accessing POST /api/Refunds/{id}/execute
6. Tenant A accessing GET /api/Invoices/{tenant-b-invoice-id}
7. Tenant A creating Payment for Tenant B invoice
8. Tenant A creating Refund for Tenant B payment
9. Client-supplied TenantId in CreatePayment DTO
10. JWT with PlatformAdmin role from untrusted source

## Business Tests
11. Contract -> Subscription preserves negotiated price
12. Upgrade/downgrade creates credit for unused period
13. Plan catalog change does not affect existing Contract
14. Plan catalog change does not affect existing Subscription
15. Plan catalog change does not affect existing Invoice
16. Renewal creates new commercial snapshot (commercial path)
17. Renewal does not inherit old promotion (commercial path)
18. Cancellation uses Contract pricing tiers, not Plan
19. Refund cannot exceed original payment amount
20. Double refund on same payment is prevented

## Financial Tests
21. Invoice total = sum of line items
22. Invoice remaining = total - allocations - credit applications
23. Payment allocation cannot exceed invoice remaining
24. Overpayment creates TenantCredit
25. Credit application cannot exceed credit remaining
26. Ledger running balance is consistent
27. Installment SettledAmount matches allocation sum

## Concurrency Tests
28. Two concurrent allocations to same invoice
29. Two concurrent credit applications to same invoice
30. Two concurrent refund executions on same payment
31. Two concurrent subscription cancellations
32. Two concurrent offer acceptances

## Cross-Tenant Tests
33. HTTP GET /api/Invoices from Tenant A with Tenant B token
34. HTTP POST /api/Payments from Tenant A for Tenant B invoice
35. HTTP POST /api/Refunds from Tenant A for Tenant B payment
36. HTTP POST /api/TenantCredits from Tenant A applying to Tenant B credit

---

# 27. Final Verdict

## Classification: **NOT READY**

The Centerix system has **strong architectural foundations** in its multi-tenant authorization pipeline, financial concurrency protection, and domain modeling. The TenantGuardMiddleware, permission system, and payment/refund concurrency handlers represent genuine production-quality engineering.

However, the system has **critical business integrity gaps** that would cause financial harm in production:

1. **Contract commercial terms are lost** when creating Subscriptions, meaning negotiated prices, promotions, and discounts are invisible to the billing system
2. **Upgrade/downgrade silently discards** unused subscription value with no refund or credit
3. **TenantAdmin cannot access any billing operations**, making all financial management dependent on PlatformAdmin

Additionally, **database integrity gaps** (missing FK constraints, missing RowVersion on financial entities, missing DB-level invariants) increase the risk of data corruption from application bugs.

**These issues are individually resolvable** but collectively they mean the system cannot be deployed to production as-is. The architectural patterns are sound, but the business flow has断裂 at critical points.

**UNKNOWN - Insufficient evidence:**
- Whether the CreateContractFromOfferCommand path (which IS documented as using the Offer flow) properly reaches the Subscription (the handler calls CreateSubscriptionFromContract which has the bug)
- Whether any external integration or background job compensates for the TenantAdmin billing access gap
- Whether any additional security hardening exists in deployment configuration not visible in the repository
