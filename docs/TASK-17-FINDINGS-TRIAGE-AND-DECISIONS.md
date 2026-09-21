# TASK 17 - FINDINGS TRIAGE, BUSINESS DECISIONS AND SECURITY POLICY RESOLUTION

**Date:** 2026-09-20
**Author:** Senior Business / Security / QA / Architecture Review
**Scope:** Re-verification and classification of all Task 16 findings
**Repository Rule:** NO source code changes. ONLY this report.

---

# 1. Executive Summary

Task 16 produced 10 critical/high findings, 8 security findings, and 7 business integrity findings. This task re-verified every finding against actual source code and classified each into one of: REAL BUG, SECURITY HARDENING, BUSINESS DECISION, TEST/EVIDENCE GAP, or FALSE POSITIVE.

## Results

- **Confirmed Critical Bugs:** 1 (Contract-to-Subscription snapshot gap)
- **Business Decisions Required:** 2 (TenantAdmin billing ownership, Upgrade/downgrade unused period)
- **Security Hardening Items:** 3 (PlatformAdmin DB verification, permission loading resilience, cross-tenant financial tests)
- **Database Integrity Gaps:** 5 (missing FK constraints, missing RowVersion)
- **Test/Evidence Gaps:** 3 (anonymous billing access, PlatformAdmin bypass, upgrade proration)
- **False Positives:** 1 (unauthenticated bypass of TenantGuardMiddleware -- not a vulnerability)
- **Task 16 findings disproven:** 1

**Overall Classification: NOT READY** -- One confirmed critical bug plus two open business decisions block production.

---

# 2. Task 16 Findings Inventory

| ID | Task 16 Finding | Classification | Severity | Confirmed | Resolution |
|----|-----------------|---------------|----------|-----------|------------|
| CF-01 | Contract commercial terms lost at Subscription creation | REAL BUG | CRITICAL | YES | Fix: call CreateFromSnapshotAsync |
| CF-02 | Upgrade/downgrade discards unused period | BUSINESS DECISION | HIGH | YES | RESOLVED: Customer Credit (Task 18) |
| CF-03 | TenantAdmin blocked from billing | BUSINESS DECISION | HIGH | YES | RESOLVED: Hybrid auth (Task 18) |
| CF-04 | Contract lacks RowVersion | DATABASE INTEGRITY GAP | HIGH | YES | Add RowVersion |
| CF-05 | Offer lacks RowVersion | DATABASE INTEGRITY GAP | MEDIUM | YES | Add RowVersion |
| CF-06 | BillingCycle lacks RowVersion | DATABASE INTEGRITY GAP | MEDIUM | YES | Add RowVersion |
| CF-07 | CustomerLedgerEntry 6 FK-like columns without FK | DATABASE INTEGRITY GAP | HIGH | YES | Add FK constraints |
| CF-08 | TenantCredit remaining-amount invariant not at DB | DATABASE INTEGRITY GAP | MEDIUM | YES | Add CHECK constraint |
| CF-09 | Installment SettledAmount invariant not at DB | DATABASE INTEGRITY GAP | MEDIUM | YES | Add CHECK constraint |
| CF-10 | Amount precision inconsistency (10,2 vs 18,2) | LOW | LOW | YES | Standardize precision |
| SF-01 | PlatformAdmin JWT bypass | SECURITY HARDENING | HIGH | YES | Add DB verification option |
| SF-02 | Permission loading exception swallowing | SECURITY HARDENING | MEDIUM | YES | Improve resilience |
| SF-03 | No cross-tenant HTTP tests for billing | TEST/EVIDENCE GAP | MEDIUM | YES | Add tests |
| SF-04 | Contract RowVersion (dup of CF-04) | DATABASE INTEGRITY GAP | HIGH | YES | Same as CF-04 |
| SF-05 | CreateRefund/ApproveRefund no concurrency | SECURITY HARDENING | MEDIUM | YES | Add protection |
| SF-06 | No DB referential integrity on ledger | DATABASE INTEGRITY GAP | HIGH | YES | Same as CF-07 |
| SF-07 | Unauthenticated bypass of TenantGuardMiddleware | FALSE POSITIVE | LOW | NO | Fallback RequireAuthenticatedUser blocks at auth |
| SF-08 | Entities not implementing IHasTenantId risk | SECURITY HARDENING | MEDIUM | YES | Code review process |
| BF-01 | Contract snapshot gap (dup of CF-01) | REAL BUG | CRITICAL | YES | Same as CF-01 |
| BF-02 | No unused period refund (dup of CF-02) | BUSINESS DECISION | CRITICAL | YES | Same as CF-02 |
| BF-03 | TenantAdmin billing (dup of CF-03) | BUSINESS DECISION | HIGH | YES | Same as CF-03 |
| BF-04 | Legacy renewal no new snapshot | ACCEPTABLE | MEDIUM | YES | Legacy path, acceptable |
| BF-05 | ChangeSubscriptionPlan uses Plan price | REAL BUG | MEDIUM | YES | Fix: use Offer-derived values |
| BF-06 | InvoiceNumber globally unique | ACCEPTABLE | LOW | YES | System-generated, acceptable |
| BF-07 | PaymentNumber globally unique | ACCEPTABLE | LOW | YES | System-generated, acceptable |

---

# 3. Classification Matrix

## 3.1 REAL BUG (must fix)

| ID | Finding | Evidence | Impact |
|----|---------|----------|--------|
| F-01 | CreateSubscriptionFromContractCommand calls CreateActivatedAsync instead of CreateFromSnapshotAsync | CreateSubscriptionFromContractCommand.cs:59-64 | Contract negotiated price, currency, duration, discount are discarded. Subscription reflects Plan catalog price. |
| F-02 | ChangeSubscriptionPlanHandler uses current Plan price/duration for Offer | ChangeSubscriptionPlanCommand.cs:149-150 | Upgrade/downgrade pricing based on current Plan, not negotiated terms |

## 3.2 BUSINESS DECISION (requires policy)

| ID | Finding | Options |
|----|---------|---------|
| D-01 | Upgrade/downgrade unused period | **RESOLVED: Customer Credit** (Task 18) |
| D-02 | TenantAdmin billing ownership | **RESOLVED: Hybrid auth** (Task 18) |

## 3.3 SECURITY HARDENING

| ID | Finding |
|----|---------|
| H-01 | PlatformAdmin JWT bypass could benefit from DB verification |
| H-02 | Permission loading exception should not swallow silently |
| H-03 | CreateRefund/ApproveRefund need concurrency protection |
| H-04 | IHasTenantId compliance needs enforcement |

## 3.4 DATABASE INTEGRITY GAP

| ID | Finding |
|----|---------|
| G-01 | Contract missing RowVersion |
| G-02 | Offer missing RowVersion |
| G-03 | BillingCycle missing RowVersion |
| G-04 | CustomerLedgerEntry missing 6 FK constraints |
| G-05 | TenantCredit remaining-amount no DB invariant |
| G-06 | Installment SettledAmount no DB invariant |
| G-07 | Amount precision inconsistency |

## 3.5 TEST/EVIDENCE GAP

| ID | Finding |
|----|---------|
| T-01 | No cross-tenant HTTP tests for billing entities |
| T-02 | No anonymous access tests for billing endpoints |
| T-03 | No PlatformAdmin bypass behavior tests |
| T-04 | No upgrade/downgrade proration tests |

## 3.6 FALSE POSITIVE

| ID | Finding | Evidence |
|----|---------|----------|
| SF-07 | Unauthenticated bypass of TenantGuardMiddleware | Fallback policy RequireAuthenticatedUser blocks at UseAuthorization. Not a vulnerability. |

---

# 4. Contract to Subscription Snapshot Analysis

## 4.1 Verified Behavior

**FACT:** `CreateSubscriptionFromContractCommand.cs:59-64` calls:
```csharp
subscriptionFactory.CreateActivatedAsync(contract.TenantId, contract.PlanId, now, false, ct)
```

**FACT:** `SubscriptionFactory.CreateActivatedAsync()` reads ALL commercial terms from the **Plan catalog entity**:
- `plan.MonthlyPrice` (not `contract.MonthlyListPrice`)
- `plan.CurrencyCode` (not `contract.CurrencyCode`)
- `plan.DurationMonths` (not `contract.DurationMonths`)
- `plan.BonusMonths` (not from contract)
- `plan.MaxStudents/MaxUsers/...` (not from contract)

**FACT:** Features are resolved from `plan.PlanFeatures` (not from `contract.Benefits`).

**FACT:** The `CreateFromSnapshotAsync` factory method EXISTS and correctly accepts explicit commercial parameters (`snapshotPrice`, `snapshotCurrency`, `durationMonths`, `bonusMonths`), but is NOT called.

## 4.2 What Is Preserved

- `PlanId` (FK link)
- `ContractId` (linked via `LinkToContract()` after creation)

## 4.3 What Is Lost

- `MonthlyListPrice` (negotiated price)
- `ContractedAmount` (deal amount)
- `DiscountAmount` (negotiated discount)
- `DurationMonths` (contract duration)
- `ChargedMonths` (promotion charged months)
- `CurrencyCode` (could differ from Plan)
- `PromotionReference`, `PromotionId`, `PromotionType`
- `PricingTiers[]` (contract-specific tiers)
- `Benefits[]` (contract-specific benefits)

## 4.4 Conclusion

**Classification: REAL BUG**
**Severity: CRITICAL**
**Impact:** Every subscription created from a contract ignores the negotiated commercial terms. The customer pays the full Plan catalog price regardless of any discount, promotion, or custom pricing in the Contract.

**Evidence strength:** FACT -- directly proven by source code.

---

# 5. Commercial Ownership Decision

## 5.1 Current Behavior (Verified)

TenantAdmin has these billing-related permissions:
- `Contracts.Read` (read-only)
- `Installments.Read` (read-only)
- `Offers.Read`, `Offers.Calculate`, `Offers.Accept` (can view/calculate/accept offers)
- `Benefits.View` (read-only)

TenantAdmin does NOT have:
- `Invoices.*` (no read, create, update, delete)
- `Payments.*` (no create, manage)
- `Refunds.*` (no create, approve, execute)
- `TenantCredits.*` (no create, apply)
- `Contracts.Create` (cannot create contracts)
- `Subscriptions.Manage` (cannot manage subscriptions)
- `Installments.Create/Update/Cancel` (cannot manage installments)

## 5.2 Model A: Platform-Owned Commercial Operations

PlatformAdmin manages: Contract, Subscription, Billing, Invoices, Payments, Refunds, Credits, Installments.
TenantAdmin manages: Students, Teachers, Courses, Attendance, Reports.

**Pros:**
- Centralized financial control
- Single point of billing management
- Reduced attack surface for tenants
- Simpler audit trail

**Cons:**
- All billing requires PlatformAdmin intervention
- Tenant cannot self-serve billing operations
- Higher support overhead for PlatformAdmin
- Scales poorly with many tenants

## 5.3 Model B: Tenant-Owned Commercial Operations

TenantAdmin manages own: Contract, Subscription (view/request changes), Invoice (view), Payment (create), Refund (request), Credits (view/apply).

PlatformAdmin retains: Platform-level management, Tenant lifecycle, Plan catalog, Promotions.

**Pros:**
- Tenant self-service
- Reduced PlatformAdmin burden
- Better customer experience
- Scales better

**Cons:**
- Larger attack surface per tenant
- More complex authorization
- More complex audit requirements
- Financial operations require tenant-level trust

## 5.4 Resolution

**Classification: BUSINESS DECISION**
**Status: OPEN**
**Required input:** Business stakeholder decision on which model to implement.

---

# 6. Upgrade / Downgrade Unused Period Decision

## 6.1 Current Behavior (Verified)

**FACT:** `ChangeSubscriptionPlanCommand.cs:326` calls `oldSubscription.Cancel(now)` -- a simple status transition with no financial settlement.

**FACT:** The new subscription is created with full price from the Offer/Contract.

**FACT:** No refund, credit, or proration is calculated for the remaining period on the old subscription.

## 6.2 Options

### Option A: Refund
Unused amount returned to customer's original payment method.
- Financial: Payment reversal, accounting adjustment
- Complexity: HIGH (requires Refund workflow integration)
- Customer experience: BEST (money back)
- Concurrency: Needs ExecuteRefund-level protection

### Option B: Customer Credit
Unused amount becomes TenantCredit for future use.
- Financial: Credit issuance, future invoice offset
- Complexity: MEDIUM (TenantCredit.Create + ledger entry)
- Customer experience: GOOD (future value)
- Concurrency: Needs credit idempotency

### Option C: Transfer
Unused value applied as discount on new subscription invoice.
- Financial: Invoice adjustment before payment
- Complexity: MEDIUM (modify new invoice calculation)
- Customer experience: GOOD (immediate benefit)
- Concurrency: Needs invoice idempotency

### Option D: Contractual Forfeiture
Unused amount forfeited per contract terms.
- Financial: No action needed
- Complexity: LOW
- Customer experience: POOR (lost value)
- Concurrency: None needed

## 6.3 Resolution

**Classification: BUSINESS DECISION**
**Status: OPEN**
**Required input:** Business stakeholder decision on policy.

---

# 7. Refund / Payment Security Policy

## 7.1 Refund Calculation (Verified)

**FACT:** `RefundCalculationService.cs` uses Contract pricing tiers (not Plan) for elapsed period repricing.
**FACT:** `Contract.CalculateValueForElapsedMonths()` uses Contract's own `PricingTiers` snapshot.
**FACT:** Benefit recovery uses day-based pro-ratio on `Contract.Benefits`.
**FACT:** Only `IsGranted` benefits are recoverable.
**FACT:** Formula: `RefundableAmount = AmountActuallyPaid - (UsedSubscriptionAmount + RemainingBenefitValue)`.

## 7.2 Payment Method Integrity (Verified)

**FACT:** `RefundAllocation.PaymentMethod` is sourced from `Payment.Method` at creation time (`RefundCalculationService.cs:136-143`).
**FACT:** At execution time, `ExecuteRefundCommand.cs:257-259` verifies `allocationForPayment.PaymentMethod != payment.Method.ToString()` and returns `PaymentMethodMismatch` error.
**FACT:** No client-supplied payment method is accepted.

## 7.3 Refund Amount Integrity (Verified)

**FACT:** Refund amount is NOT client-supplied -- calculated by `RefundCalculationService`.
**FACT:** Per-payment refundable balance is validated: `payment.Amount - existingRefundedAmount`.
**FACT:** Cumulative refunded amounts across Processing/Completed refunds are subtracted.
**FACT:** `InsufficientPaymentSource` error returned if allocation exceeds refundable amount.

## 7.4 Refund Idempotency (Verified)

**FACT:** 5 layers of idempotency protection:
1. Completed + matching key = silent success
2. Processing + key mismatch = conflict
3. DB unique index catches duplicate keys
4. Concurrency race fallback on SaveChanges
5. Deadlock retry also checks idempotency

## 7.5 Conclusion

Refund/payment security is well-implemented. No bugs found.

---

# 8. PlatformAdmin Security Policy

## 8.1 Current Behavior (Verified)

**FACT:** `PermissionAuthorizationHandler.cs:76-81` grants unconditional success for PlatformAdmin role.
**FACT:** `FeatureAuthorizationHandler.cs:34-38` grants unconditional success for PlatformAdmin role.
**FACT:** JWT contains Role claim 'PlatformAdmin'. No DB verification occurs after JWT validation.
**FACT:** `PlatformAdminGuard.cs` provides imperative defense-in-depth for handler-level checks.
**FACT:** JWT claims are server-generated by `JwtTokenService.cs` using HMAC-SHA256.

## 8.2 Assessment

The PlatformAdmin bypass is a standard architectural pattern. The JWT is server-generated and signed with a secret key. Compromise requires either server secret key compromise, valid JWT theft (token expiry = 60 minutes), or insider threat.

This is NOT a privilege escalation vulnerability. It is an architectural design choice.

## 8.3 Recommendations (Security Hardening)

1. Consider DB-based PlatformAdmin verification for highest-security operations (optional, not required)
2. Ensure JWT secret is managed via secrets management (not appsettings)
3. Consider shortening PlatformAdmin token expiry
4. Ensure all PlatformAdmin operations are audit-logged

## 8.4 Conclusion

**Classification: SECURITY HARDENING** (not a bug)

---

# 9. Authorization Matrix

## 9.1 Verified TenantAdmin Permissions

| Operation | TenantAdmin | TenantUser | PlatformAdmin | Source |
|-----------|-------------|------------|---------------|--------|
| View own educational data | ALLOW | ALLOW | ALLOW | TenantPlans.Read, Students.Read, Teachers.Read |
| Manage students | ALLOW | DENY | ALLOW | Students.Create/Update/Delete |
| Manage teachers | ALLOW | DENY | ALLOW | Teachers.Create/Update/Delete |
| View own subscriptions | ALLOW | ALLOW | ALLOW | TenantPlans.Read |
| Accept offer | ALLOW | DENY | ALLOW | Offers.Accept |
| Calculate offer | ALLOW | DENY | ALLOW | Offers.Calculate |
| View contract | ALLOW | DENY | ALLOW | Contracts.Read |
| View installments | ALLOW | DENY | ALLOW | Installments.Read |
| View invoices | DENY | DENY | ALLOW | Invoices.Read (not in TenantAdmin) |
| Create payment | DENY | DENY | ALLOW | Payments.Create (not in TenantAdmin) |
| Request refund | DENY | DENY | ALLOW | Refunds.Create (not in TenantAdmin) |
| Approve refund | DENY | DENY | ALLOW | Refunds.Approve (not in TenantAdmin) |
| Execute refund | DENY | DENY | ALLOW | Refunds.Execute (not in TenantAdmin) |
| Create contract | DENY | DENY | ALLOW | Contracts.Create (not in TenantAdmin) |
| Manage subscription | DENY | DENY | ALLOW | Subscriptions.Manage (PlatformScope) |
| Change plan | DENY | DENY | ALLOW | Subscriptions.Manage (PlatformScope) |
| Suspend tenant | DENY | DENY | ALLOW | Tenants.Update (PlatformScope) |
| Platform configuration | DENY | DENY | ALLOW | PlatformUsers/Roles/Permissions (PlatformScope) |

## 9.2 Note on Missing Permissions

Several permissions exist in `Permissions.cs` but are NOT granted to ANY standard role (not even TenantAdmin): `Students.*`, `AttendanceLogs.*`, `Branches.*`, `AcademicStages.*`, `AcademicYears.*`, `TenantAddOns.*`, `TenantLimitOverrides.*`, `TenantReferralCodes.*`, `TenantReferrals.*`, `TenantProvisioningJobs.*`.

These require either PlatformAdmin role or custom DB-backed role assignment. The controllers have `[HasPermission]` attributes, so these endpoints return 403 for all standard roles.

**Assessment:** This appears to be an authorization configuration gap for education module operations. The controllers exist and have `[HasPermission]` but the permissions are not assigned to any standard role.

---

# 10. Cross-Tenant Security Policy

## 10.1 Protection Layers (Verified)

1. **Finbuckle MultiTenant** -- resolves tenant from request
2. **TenantGuardMiddleware** -- validates active membership, loads permissions
3. **CurrentTenant.AuthorizeTenant()** -- locks verified tenant ID
4. **TenantInterceptor** -- stamps authorized tenant on new entities
5. **Global query filter** -- filters IHasTenantId entities
6. **Explicit handler checks** -- AllocatePayment, ExecuteRefund, ApplyCreditToInvoice all verify TenantId matches

## 10.2 Assessment

Cross-tenant isolation is robust for billing entities with explicit handler-level checks. The combination of middleware + interceptor + global query filter + handler checks provides defense-in-depth.

## 10.3 Remaining Risk

Entities that lack explicit handler-level cross-tenant checks and rely solely on global query filters. If a developer forgets to implement IHasTenantId on a new entity, it would be visible across tenants.

---

# 11. Financial Concurrency Strategy

## 11.1 Current Protection (Verified)

| Operation | Invariant | RowVersion | SQL Lock | Transaction | Idempotency |
|-----------|-----------|-----------|----------|-------------|-------------|
| Payment Allocation | Allocation <= Payment amount | Yes | UPDLOCK/ROWLOCK/HOLDLOCK | Serializable | Exact-match |
| Credit Application | Credit consumed <= remaining | Yes | UPDLOCK/ROWLOCK/HOLDLOCK | Serializable | IdempotencyKey |
| Refund Execution | Refund <= payment - prior refunds | Yes | UPDLOCK/ROWLOCK/HOLDLOCK | Serializable | IdempotencyKey |
| Subscription Change | No overlap | Yes | Serializable query | Serializable | None |
| Payment Creation | Amount > 0 | Yes | None | Default | None |
| Invoice Issue | Status = Draft | Yes | None | Default | None |
| Refund Create | Amount calculated server-side | Yes | None | Default | None |
| Refund Approve | Status = Pending | Yes | None | Default | None |
| Contract | None | **NO** | None | Default | None |
| Offer | None | **NO** | None | Default | None |
| BillingCycle | None | **NO** | None | Default | None |

## 11.2 Assessment

High-value concurrent operations (allocation, credit, refund execution) have enterprise-grade protection. Lower-risk operations (create, status transitions) have basic RowVersion. The three entities without RowVersion (Contract, Offer, BillingCycle) need individual assessment.

---

# 12. Database / FK Integrity

## 12.1 Missing FK Constraints (Verified)

| Entity | Missing FKs | Risk | Rationale |
|--------|-------------|------|-----------|
| CustomerLedgerEntry | InvoiceId, PaymentId, PaymentAllocationId, RefundId, CreditId, CreditApplicationId | HIGH | Ledger is financial source of truth |
| CreditApplication | CreditId, InvoiceId | MEDIUM | Application references credit and invoice |
| TenantCredit | SourceId, ReversalOfCreditId | MEDIUM | SourceId is polymorphic; ReversalOfCreditId is self-ref |
| Installment | ContractId | MEDIUM | Installment must belong to a contract |
| Contract | PlanId | LOW | Plan is global catalog |
| Offer | PlanId, PromotionId | LOW | Snapshot references |
| Invoice | ContractId, SubscriptionId, BillingCycleId | MEDIUM | Invoice must link to commercial context |

## 12.2 Missing RowVersion (Verified)

| Entity | Impact | Justification |
|--------|--------|---------------|
| Contract | MEDIUM | Only PlatformAdmin modifies contracts, reducing race likelihood |
| Offer | LOW | Short-lived, single-tenant |
| BillingCycle | LOW | Created/updated sequentially per subscription |

## 12.3 DB Invariant Gaps (Verified)

| Entity | Invariant | Current Protection |
|--------|-----------|-------------------|
| TenantCredit | RemainingAmount >= 0 AND <= Amount | Domain code only |
| Installment | SettledAmount >= 0 AND <= Amount | Domain code only |

---

# 13. Subscription Snapshot Debt

## 13.1 CreateFromSnapshotAsync Limitations (Verified)

**FACT:** `SubscriptionFactory.CreateFromSnapshotAsync()` accepts explicit commercial parameters but still reads LIMITS from the Plan catalog: `plan.MaxStudents`, `plan.MaxUsers`, `plan.MaxBranches`, `plan.MaxTeachers`, `plan.StorageGB`, `plan.SMSQuota`.

**FACT:** Features are also resolved from `plan.PlanFeatures`, not from Contract/Offer benefits.

## 13.2 Separation of Concerns

Two distinct issues:

1. **REAL BUG (F-01):** `CreateSubscriptionFromContractCommand` calls wrong factory method -- Contract terms are completely discarded.
2. **SNAPSHOT DEBT:** Even when using `CreateFromSnapshotAsync`, limits and features come from current Plan catalog.

The snapshot debt means that even after fixing F-01, a Plan limit change would affect the limits of newly-created subscriptions (but NOT their price/duration). This is a separate, lower-priority issue.

---

# 14. Renewal Integrity

## 14.1 Legacy Renewal (Verified)

**FACT:** `RenewSubscriptionHandler` mutates existing subscription via `subscription.Renew()` -- appends months in-place.
**FACT:** No new commercial snapshot is created.
**FACT:** Old `SnapshotPrice` remains unchanged.
**Assessment:** Acceptable for legacy path.

## 14.2 Commercial Renewal (Verified)

**FACT:** `RenewSubscriptionOfferCommand` creates new Offer -> Contract -> Subscription chain.
**FACT:** Fresh promotion calculation from current Promotions catalog.
**FACT:** New `TenantPlanFeature` rows created from current Plan.
**FACT:** Old subscription cancelled.
**FACT:** Domain comment: 'The old subscription remains immutable. No old promotions, discounts, or benefits are inherited.'

**Conclusion:** Renewal correctly creates new commercial transaction without inheriting old incentives.

---

# 15. Cancellation / Expiration Integrity

## 15.1 Cancellation (Verified)

**FACT:** `CancelSubscriptionCommand` triggers `RefundCalculationService.Calculate()` when Contract exists.
**FACT:** Future unpaid installments are cancelled.
**FACT:** If no Contract exists, no refund calculation occurs (simple status change).
**FACT:** Cancellation rejects already-expired subscriptions.

## 15.2 Natural Expiration (Verified)

**FACT:** `TenantPlan.MarkExpired()` sets Status = Expired.
**FACT:** Does not trigger refund calculation.
**FACT:** Does not cancel future installments.
**FACT:** Established rule: Natural expiration = no refund.

**Conclusion:** Cancellation and expiration are correctly distinguished.

---

# 16. Test / Evidence Gaps

## 16.1 Existing Tests (Verified)

| Scenario | Status | Test File |
|----------|--------|-----------|
| Contract snapshot immutability | EXISTS | Phase7ContractHardeningTests.cs |
| Contract-Subscription date alignment | EXISTS | Phase9_3_3ContractSubscriptionAlignmentTests.cs |
| Cross-tenant refund rejection | EXISTS | Phase4_1_2RefundIntegrityTests.cs |
| Cross-tenant credit rejection | EXISTS | Phase10_1CreditConcurrencySqlServerTests.cs |
| Concurrent payment allocation | EXISTS | Phase9FinancialConcurrencySqlServerTests.cs |
| Concurrent refund execution | EXISTS | Phase13RefundAllocationSqlServerTests.cs |
| Refund payment method tamper | EXISTS | Phase13RefundAllocationSqlServerTests.cs |
| Refund amount exceeds payment | EXISTS | Phase13RefundAllocationSqlServerTests.cs |

## 16.2 Missing Tests (Verified)

| Scenario | Status | Impact |
|----------|--------|--------|
| Contract commercial terms preserved in Subscription | NOT FOUND | Cannot prove F-01 fix works |
| Upgrade/downgrade proration | NOT FOUND | No evidence of unused period handling |
| Anonymous access to billing endpoints | NOT FOUND | No evidence of auth boundary |
| PlatformAdmin bypass behavior | NOT FOUND | No evidence of cross-tenant admin access |

---

# 17. Confirmed Bugs

| ID | Finding | Classification | Severity | Evidence |
|----|---------|---------------|----------|----------|
| F-01 | CreateSubscriptionFromContractCommand discards Contract commercial terms | REAL BUG | CRITICAL | CreateSubscriptionFromContractCommand.cs:59-64 calls CreateActivatedAsync |
| F-02 | ChangeSubscriptionPlanHandler uses current Plan price/duration for Offer | REAL BUG | MEDIUM | ChangeSubscriptionPlanCommand.cs:149-150 reads plan.DurationMonths and plan.BonusMonths |

---

# 18. Business Decisions Still Open

| ID | Decision | Current Behavior | Options | Status |
|----|----------|-----------------|---------|--------|
| D-01 | TenantAdmin billing ownership | All billing is PlatformAdmin-only | Platform-owned / Tenant-owned / Hybrid | **RESOLVED: Hybrid** (Task 18) |
| D-02 | Upgrade/downgrade unused period | Forfeited (no refund/credit) | Refund / Credit / Transfer / Forfeiture | **RESOLVED: Customer Credit** (Task 18) |

---

# 19. Security Hardening Backlog

| ID | Item | Priority |
|----|------|----------|
| H-01 | Add DB-based PlatformAdmin verification for highest-security operations | P3 |
| H-02 | Improve permission loading error handling (fail-closed, not swallow) | P2 |
| H-03 | Add concurrency protection to CreateRefund/ApproveRefund | P2 |
| H-04 | Enforce IHasTenantId compliance via code review or Roslyn analyzer | P3 |

---

# 20. Implementation Backlog

| Priority | ID | Problem | Classification | Required Change | Dependencies |
|----------|-----|---------|---------------|-----------------|-------------|
| P0 | F-01 | Contract terms lost at Subscription creation | REAL BUG | Fix SubscriptionFactory call in CreateSubscriptionFromContractCommand | None |
| P0 | D-01 | TenantAdmin billing ownership | BUSINESS DECISION | RESOLVED: Hybrid (Task 18) | None |
| P0 | D-02 | Upgrade/downgrade unused period | BUSINESS DECISION | RESOLVED: Customer Credit (Task 18) | None |
| P1 | F-02 | ChangeSubscriptionPlan uses Plan price | REAL BUG | Use Offer-derived values for duration/bonus | None |
| P1 | G-01 | Contract missing RowVersion | DB INTEGRITY | Add RowVersion + EF config | None |
| P1 | G-04 | CustomerLedgerEntry missing FKs | DB INTEGRITY | Add FK constraints | None |
| P2 | G-02 | Offer missing RowVersion | DB INTEGRITY | Add RowVersion + EF config | None |
| P2 | G-03 | BillingCycle missing RowVersion | DB INTEGRITY | Add RowVersion + EF config | None |
| P2 | G-05 | TenantCredit remaining-amount invariant | DB INTEGRITY | Add CHECK constraint | None |
| P2 | G-06 | Installment SettledAmount invariant | DB INTEGRITY | Add CHECK constraint | None |
| P2 | H-02 | Permission loading error handling | SECURITY | Improve middleware resilience | None |
| P2 | H-03 | CreateRefund/ApproveRefund concurrency | SECURITY | Add transaction isolation | None |
| P2 | T-01 | Cross-tenant billing HTTP tests | TEST | Add HTTP-level IDOR tests | D-01 decision |
| P2 | T-02 | Anonymous billing endpoint tests | TEST | Add 401/403 tests | None |
| P3 | G-07 | Amount precision inconsistency | DB INTEGRITY | Standardize to HasPrecision(18,2) | None |
| P3 | H-01 | PlatformAdmin DB verification | SECURITY | Optional DB check | None |
| P3 | H-04 | IHasTenantId enforcement | SECURITY | Roslyn analyzer or code review | None |
| P3 | T-03 | PlatformAdmin bypass behavior tests | TEST | Add cross-tenant admin tests | None |

---

# 21. Dependency / Implementation Order

## Track 1: Critical Bug Fix
```
F-01: Fix SubscriptionFactory call (P0)
  -> No dependencies
  -> Add test: Contract terms preserved in Subscription
```

## Track 2: Business Decisions (blocking)
```
D-01: TenantAdmin billing ownership decision (P0)
  -> Required before: Authorization matrix finalization
  -> Required before: Cross-tenant billing tests
  -> Required before: Endpoint authorization adjustments

D-02: Upgrade/downgrade unused period decision (P0)
  -> Required before: ChangeSubscriptionPlanHandler implementation
  -> Required before: Financial integration tests
```

## Track 3: Database Integrity
```
G-01: Add Contract RowVersion (P1)
G-04: Add CustomerLedgerEntry FKs (P1)
G-02: Add Offer RowVersion (P2)
G-03: Add BillingCycle RowVersion (P2)
G-05: Add TenantCredit CHECK (P2)
G-06: Add Installment CHECK (P2)
G-07: Standardize precision (P3)
```

## Track 4: Security Hardening
```
H-02: Permission loading resilience (P2)
H-03: Refund concurrency protection (P2)
H-01: PlatformAdmin DB verification (P3)
H-04: IHasTenantId enforcement (P3)
```

## Track 5: Tests
```
Contract snapshot integrity test (after F-01 fix)
Anonymous billing access tests (after D-01 decision)
Cross-tenant billing HTTP tests (after D-01 decision)
Upgrade/downgrade proration tests (after D-02 decision)
PlatformAdmin bypass behavior tests (P3)
```

---

# 22. Evidence Index

| Finding | File | Class | Method | Line | Evidence |
|---------|------|-------|--------|------|----------|
| F-01 | src/Centerix.Application/Platform/Contracts/Commands/CreateSubscriptionFromContractCommand.cs | CreateSubscriptionFromContractHandler | Handle | 59-64 | Calls CreateActivatedAsync(contract.TenantId, contract.PlanId, ...) |
| F-01 | src/Centerix.Application/Platform/Subscriptions/SubscriptionFactory.cs | SubscriptionFactory | CreateActivatedAsync | 85-101 | Reads plan.MonthlyPrice, plan.CurrencyCode, plan.DurationMonths |
| F-02 | src/Centerix.Application/Platform/Commands/ChangeSubscriptionPlanCommand.cs | ChangeSubscriptionPlanHandler | Handle | 149-150 | Reads plan.DurationMonths and plan.BonusMonths from Plan catalog |
| D-01 | src/Centerix.Infrastructure/Auth/Permissions.cs | Permissions | GetTenantAdminPermissions | 324-339 | Excludes Invoices, Payments, Refunds, Credits, Subscriptions.Manage |
| D-02 | src/Centerix.Application/Platform/Commands/ChangeSubscriptionPlanCommand.cs | ChangeSubscriptionPlanHandler | Handle | 326 | Calls oldSubscription.Cancel(now) without proration |
| G-01 | src/Centerix.Infrastructure/Data/Configurations/ContractConfiguration.cs | ContractConfiguration | Configure | entire | No IsRowVersion() call |
| G-04 | src/Centerix.Infrastructure/Data/Configurations/CustomerLedgerEntryConfiguration.cs | CustomerLedgerEntryConfiguration | Configure | entire | 0 HasOne/HasForeignKey calls for 6 FK-like columns |
| H-02 | src/Centerix.API/Infrastructure/TenantGuardMiddleware.cs | TenantGuardMiddleware | InvokeAsync | 94-98 | Exception swallowing in permission loading |
| H-03 | src/Centerix.Application/Platform/Billing/Commands/CreateRefundCommand.cs | CreateRefundHandler | Handle | entire | No transaction isolation or deadlock handling |
| Refund calc | src/Centerix.Domain/Platform/Billing/Refunds/RefundCalculationService.cs | RefundCalculationService | Calculate | 52 | contract.CalculateValueForElapsedMonths(elapsedMonths) |
| PaymentMethod | src/Centerix.Application/Platform/Billing/Commands/ExecuteRefundCommand.cs | ExecuteRefundHandler | Handle | 257-259 | Verifies allocationForPayment.PaymentMethod != payment.Method.ToString() |
| Renewal | src/Centerix.Application/Platform/Commands/RenewSubscriptionOfferCommand.cs | RenewSubscriptionOfferHandler | Handle | 29 | Domain comment: old subscription remains immutable |
| Cancellation | src/Centerix.Application/Platform/Billing/Commands/CancelSubscriptionCommand.cs | CancelSubscriptionHandler | Handle | 190-241 | Triggers RefundCalculationService when Contract exists |

---

# 23. Final Task 17 Verdict

**Task 17 Status:** COMPLETE
**Implementation Changes:** NONE (analysis only)
**Business Decisions Resolved:** 2 (D-01: Hybrid auth model, D-02: Customer Credit for unused period)
**Business Decisions Open:** 0
**Confirmed Critical Bugs:** 1 (F-01: Contract-to-Subscription snapshot gap)
**Confirmed Medium Bugs:** 1 (F-02: ChangeSubscriptionPlan uses Plan price)
**Security Hardening Items:** 4
**Database Integrity Gaps:** 7
**Test/Evidence Gaps:** 4
**False Positives:** 1 (SF-07: unauthenticated bypass -- not a vulnerability)

## NEXT TASK

The recommended implementation sequence is:

### Phase 1: ~~Resolve Business Decisions~~ DONE
1. ~~Stakeholder meeting to decide D-01~~ -- RESOLVED: Hybrid (Task 18)
2. ~~Stakeholder meeting to decide D-02~~ -- RESOLVED: Customer Credit (Task 18)

### Phase 2: Critical Bug Fixes
3. Fix CreateSubscriptionFromContractCommand to call CreateFromSnapshotAsync
4. Fix ChangeSubscriptionPlanHandler to use Offer-derived duration/bonus

### Phase 3: Database Integrity
5. Add RowVersion to Contract, Offer, BillingCycle
6. Add FK constraints to CustomerLedgerEntry, CreditApplication, Invoice
7. Add CHECK constraints to TenantCredit, Installment
8. Standardize amount precision to HasPrecision(18,2)

### Phase 4: Security Hardening
9. Improve permission loading error handling
10. Add concurrency protection to CreateRefund/ApproveRefund

### Phase 5: Tests
11. Add Contract-to-Subscription snapshot integrity test
12. Add anonymous billing endpoint access tests
13. ~~Add cross-tenant billing HTTP tests (after D-01 decision)~~ -- DONE (Task 18)
14. ~~Add upgrade/downgrade proration tests (after D-02 decision)~~ -- DONE (Task 18)
15. Add PlatformAdmin bypass behavior tests

---

**TASK 17 IS COMPLETE. NO SOURCE CODE WAS MODIFIED.**
