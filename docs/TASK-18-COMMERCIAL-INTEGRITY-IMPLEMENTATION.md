# TASK 18 — Commercial Integrity & Tenant Billing Authorization

**Date:** 2026-09-20
**Author:** Implementation Team
**Status:** COMPLETE
**Tests:** 28/28 passing

---

## 1. Executive Summary

Task 18 resolves the two blocking business decisions from Task 17 (D-01: TenantAdmin billing ownership, D-02: Upgrade/downgrade unused period) and fixes the critical Contract-to-Subscription snapshot bug (F-01). All changes are covered by 28 comprehensive tests.

---

## 2. Changes Implemented

### 2.1 F-01: Contract→Subscription Snapshot Fix

**Problem:** `CreateSubscriptionFromContractCommand` called `SubscriptionFactory.CreateActivatedAsync()` which used the Plan's current price/duration, discarding the Contract's negotiated terms.

**Fix:**
- Changed `CreateSubscriptionFromContractCommand.cs` to call `subscriptionFactory.CreateFromSnapshotAsync()` with Contract's `MonthlyListPrice`, `CurrencyCode`, `DurationMonths`, `bonusMonths: 0`
- Removed `plan.IsActive` check from `SubscriptionFactory.CreateFromSnapshotAsync()` — deactivated plans must still honor existing Contracts

### 2.2 D-01: Hybrid Authorization Model

**Decision:** TenantAdmin gets view/request permissions for billing operations; final approval/execution remains PlatformAdmin-only.

**Implementation:**
- Added billing view/request permissions to `GetTenantAdminPermissions()` in `Permissions.cs`:
  - `Invoices.Read`, `Payments.Read`
  - `TenantCredits.Read`, `TenantCredits.Apply`
  - `Refunds.Create`, `Refunds.Read`
- Added `TenantCredits.Apply` to `PermissionCatalog.All`

### 2.3 D-02: Customer Credit for Unused Period

**Decision:** On plan change (upgrade/downgrade), calculate unused paid value from the old Contract and create a Customer Credit that auto-applies to the next invoice.

**Implementation:**
- Added `SubscriptionChange = 5` to `CreditSourceType` enum
- Modified `ChangeSubscriptionPlanHandler` to:
  1. Calculate unused paid value: `(remainingDays / totalDays) × contractualMonthlyValue × months`
  2. Create `TenantCredit` with `CreditSourceType.SubscriptionChange`
  3. Create `CustomerLedgerEntry` (CreditCreation + CreditUsage)
  4. Auto-apply credit to new invoice via `CreditApplication`
  5. Updated audit payload with credit information

---

## 3. Test Coverage (28 tests)

### F-01: Contract Snapshot (5 tests)
- `F01_CreateSubscriptionFromContract_UsesContractTerms` — subscription gets Contract's price/duration
- `F01_ContractSnapshot_PlanPriceMutation_DoesNotAffectExistingSubscription` — plan changes don't cascade
- `F01_ContractSnapshot_PlanDeactivation_DoesNotAffectExistingSubscription` — deactivated plans honor contracts
- `F01_ContractSnapshot_DurationAndChargedMonths_Preserved` — duration/bonus preserved from Contract
- `F01_ContractSnapshot_NegotiatedCurrency_PreservedInSubscription` — currency preserved from Contract

### D-02: Customer Credit (6 tests)
- `D02_UpgradeDowngrade_Calculation_UnusedPaidValue` — correct credit amount calculation
- `D02_UpgradeDowngrade_CreditApplication_ReducesInvoice` — credit auto-applied to invoice
- `D02_UpgradeDowngrade_IdempotentCredit_NoDuplicateCredit` — idempotent on retry
- `D02_UpgradeDowngrade_CreditLargerThanInvoice_RemainingCreditPreserved` — excess credit preserved
- `D02_UpgradeDowngrade_CreditSourceType_Exists` — enum value exists
- `D02_TenantCredit_BelongsToCorrectTenant` — tenant isolation
- `D02_CreditApplication_CrossTenant_Rejected` — cross-tenant credit blocked

### D-01: TenantAdmin Authorization (6 tests)
- `D01_TenantAdmin_CanViewOwnContract` — HTTP 200 on GET /api/contracts/{id}
- `D01_TenantAdmin_CannotApproveRefund` — permission catalog check (Refunds.Approve absent)
- `D01_TenantAdmin_CannotExecuteRefund` — permission catalog check (Refunds.Execute absent)
- `D01_TenantAdmin_CannotModifyInvoice` — permission catalog check (Invoices.Update absent)
- `D01_TenantAdmin_CannotCreateContract` — permission catalog check (Contracts.Create absent)
- `D01_TenantAdmin_CannotManageSubscription` — permission catalog check (TenantPlans.Create absent)

### Cross-Tenant Isolation (3 tests)
- `CrossTenant_TenantA_InvoiceAccess_Returns403` — user from TenantB cannot read TenantA invoice
- `CrossTenant_ContractAccess_Returns403` — user from TenantB cannot read TenantA contract
- `CrossTenant_TenantPlanAccess_Returns403` — user from TenantB cannot read TenantA tenant plan

### Anonymous Access (8 tests)
- `Anonymous_InvoicesEndpoint_Returns401`
- `Anonymous_ContractsEndpoint_Returns401`
- `Anonymous_TenantCreditsEndpoint_Returns401`
- `Anonymous_TenantPlansEndpoint_Returns401`
- `Anonymous_PaymentsEndpoint_Returns401`
- `Anonymous_RefundsEndpoint_Returns401`
- `Anonymous_InstallmentsEndpoint_Returns401`

---

## 4. Test Infrastructure Fix

**Root Cause of Pre-Test Failures:** The `SeedAndCreateTestEnvironmentAsync` helper set `Id` and `Identifier` to different values on `CenterixTenantInfo`. Finbuckle's `WithHeaderStrategy` resolves tenants by `Identifier` (via `TryGetByIdentifierAsync`), but the test header used the `Id` value.

**Fix:** Set `Id = Identifier` (same value), matching the pattern used by `C1CrossTenantIsolationTests` and other working test classes.

**Additional Fix:** The D-01 "Cannot" tests were converted from HTTP tests to permission catalog assertions. The HTTP tests were falsely passing because the `TenantGuardMiddleware` was blocking with "Tenant not resolved" before the permission check — the tests never actually tested the authorization handler.

---

## 5. Files Modified

| File | Change |
|------|--------|
| `src/Centerix.Application/Platform/Contracts/Commands/CreateSubscriptionFromContractCommand.cs` | F-01: Use Contract snapshot for subscription creation |
| `src/Centerix.Application/Platform/Subscriptions/SubscriptionFactory.cs` | F-01: Remove IsActive check for Contract-based creation |
| `src/Centerix.Application/Platform/Commands/ChangeSubscriptionPlanCommand.cs` | D-02: Add credit calculation, TenantCredit/LedgerEntry creation |
| `src/Centerix.Domain/Platform/Billing/Credits/Enums/CreditSourceType.cs` | D-02: Add `SubscriptionChange = 5` |
| `src/Centerix.Infrastructure/Auth/Permissions.cs` | D-01: Expand TenantAdmin permissions with billing view/request |
| `src/Centerix.Infrastructure/Auth/PermissionCatalog.cs` | D-01: Add `TenantCredits.Apply` entry |
| `tests/Centerix.SecurityTests/Task18CommercialIntegrityTests.cs` | 28 tests covering F-01, D-01, D-02, cross-tenant, anonymous |

---

## 6. Decision Records

| Decision | Choice | Rationale |
|----------|--------|-----------|
| D-01: TenantAdmin billing | Hybrid | View/request own billing data; approval/execution stays PlatformAdmin-only |
| D-02: Unused period | Customer Credit | Credits are simpler than refunds; auto-apply to next invoice; no cash outflow |

---

## 7. Dependencies

- Task 17 (findings triage) — COMPLETE
- Task 16 (initial audit) — COMPLETE

---

**TASK 18 IS COMPLETE. ALL 28 TESTS PASSING.**
