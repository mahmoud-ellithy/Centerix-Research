# TASK 18 — Commercial Integrity & Tenant Billing Authorization

**Date:** 2026-09-20
**Author:** Implementation Team
**Status:** COMPLETE (Task 18.1.1 Corrections Applied)
**Tests:** 55/55 passing

---

## 1. Executive Summary

Task 18 resolves the two blocking business decisions from Task 17 (D-01: TenantAdmin billing ownership, D-02: Upgrade/downgrade unused period) and fixes the critical Contract-to-Subscription snapshot bug (F-01). Task 18.1.1 completes all Contract creation paths, fixes the ledger RunningBalance sequence, and adds a snapshot invariant. All changes are covered by 55 comprehensive tests.

---

## 2. Changes Implemented

### 2.1 F-01: Contract→Subscription Snapshot Fix

**Problem:** `CreateSubscriptionFromContractCommand` called `SubscriptionFactory.CreateActivatedAsync()` which used the Plan's current price/duration, discarding the Contract's negotiated terms.

**Fix (Task 18):**
- Changed `CreateSubscriptionFromContractCommand.cs` to call `subscriptionFactory.CreateFromSnapshotAsync()` with Contract's `MonthlyListPrice`, `CurrencyCode`, `DurationMonths`, `bonusMonths: 0`
- Removed `plan.IsActive` check from `SubscriptionFactory.CreateFromSnapshotAsync()` — deactivated plans must still honor existing Contracts

**Fix (Task 18.1 — Complete Contract Snapshot):**
- Added `BonusMonths`, `MaxStudents`, `MaxUsers`, `MaxBranches`, `MaxTeachers`, `StorageGb`, `SmsQuota` fields to `Contract` entity
- Created `ContractFeature` entity (immutable per-contract feature entitlement snapshot)
- Created `SubscriptionSnapshot` record (bundles all values for Subscription creation)
- Added `Contract.GetSubscriptionSnapshot()` domain method — builds snapshot from Contract's own data, **zero Plan queries**
- Refactored `ISubscriptionFactory` — new `CreateFromSnapshotAsync(..., SubscriptionSnapshot, ...)` overload that reads limits/features exclusively from the snapshot
- Updated all callers: `CreateSubscriptionFromContractHandler`, `RenewSubscriptionOfferCommand`, `ChangeSubscriptionPlanCommand`
- All callers now populate `Contract.AddContractFeature()` with feature snapshots from the Plan at contract creation time

**Fix (Task 18.1.1 — Complete Contract Snapshot Paths):**
- `CreateContractFromOfferCommand`: Now loads `Plan.PlanFeatures` and populates `BonusMonths`, `MaxStudents`, `MaxUsers`, `MaxBranches`, `MaxTeachers`, `StorageGb`, `SmsQuota`, and `ContractFeatures` from the authoritative Plan source at contract creation time
- `CreateContractCommand`: Now loads the Plan to populate limits and features instead of accepting them from the client. Documented as non-production creation path (no handler/API endpoint invokes it)
- `Contract.ValidateSnapshotCompleteness()`: New domain invariant that validates a Contract has a complete entitlement snapshot before creating a Subscription

**Critical invariant:** After (1) Contract created, (2) Subscription created, (3) Plan mutated, (4) Plan deactivated — the existing Subscription MUST retain its original contractual values.

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

**Implementation (Task 18.1 Corrections):**
- **Credit application cap:** Changed `ChangeSubscriptionPlanCommand` to apply `Math.Min(creditAmount, invoice.GetRemainingAmount())` instead of the full credit amount
- **Payment verification strengthened:** Changed payment query to filter on `Payment.Status == Completed` and `Payment.CurrencyCode == oldContract.CurrencyCode`
- **Idempotency constraint:** Added `IdempotencyKey` property to `TenantCredit` and unique filtered index on `(TenantId, SourceType, SourceId)`
- **Ledger balance helper:** Changed from `SUM(EntryType)` to `OrderByDescending(RecordedAtUtc).FirstOrDefault().RunningBalance` for correctness under concurrency

**Implementation (Task 18.1.1 — Ledger RunningBalance Fix):**
- Fixed `CustomerLedgerEntry` RunningBalance sequence in `ChangeSubscriptionPlanCommand`: CreditUsage now uses the balance resulting from CreditCreation, not the original `previousBalance`. The sequence is:
  1. `previousBalance` (queried from last ledger entry)
  2. `CreditCreation` → `balanceAfterCreditCreation = previousBalance - creditAmount`
  3. `CreditUsage` → uses `balanceAfterCreditCreation` as its `previousBalance`

**Implementation:**
- Added `SubscriptionChange = 5` to `CreditSourceType` enum
- Modified `ChangeSubscriptionPlanHandler` to:
  1. Calculate unused paid value: `(remainingDays / totalDays) × contractualMonthlyValue × months`
  2. Create `TenantCredit` with `CreditSourceType.SubscriptionChange`
  3. Create `CustomerLedgerEntry` (CreditCreation + CreditUsage)
  4. Auto-apply credit to new invoice via `CreditApplication`
  5. Updated audit payload with credit information

---

## 3. Contract Creation Paths

| Path | Source of Limits | Source of Features | Source of Pricing | Status |
|------|------------------|--------------------|-------------------|--------|
| `CreateContractFromOfferCommand` | Plan (loaded from DB) | PlanFeatures (loaded from DB) | Offer + Plan.PricingTiers | **Complete** |
| `RenewSubscriptionOfferCommand` | Plan (loaded from DB) | PlanFeatures (loaded from DB) | Offer + Plan.PricingTiers | **Complete** |
| `ChangeSubscriptionPlanCommand` | Plan (loaded from DB) | PlanFeatures (loaded from DB) | Offer + Plan.PricingTiers | **Complete** |
| `CreateSubscriptionFromContractCommand` | Contract snapshot | Contract snapshot | Contract snapshot | **Complete** |
| `CreateContractCommand` | Plan (loaded from DB) | PlanFeatures (loaded from DB) | Client-supplied | **Non-production path** (no handler/API endpoint) |

---

## 4. Contract Snapshot Fields

A complete Contract snapshot contains:

| Field | Source | Frozen at |
|-------|--------|-----------|
| MonthlyListPrice | Offer/Negotiated | Contract creation |
| ContractualMonthlyValue | Offer/Negotiated | Contract creation |
| CurrencyCode | Offer/Negotiated | Contract creation |
| DurationMonths | Offer | Contract creation |
| BonusMonths | Plan | Contract creation |
| ChargedMonths | Offer (PayForXMonths) | Contract creation |
| MaxStudents | Plan | Contract creation |
| MaxUsers | Plan | Contract creation |
| MaxBranches | Plan | Contract creation |
| MaxTeachers | Plan | Contract creation |
| StorageGb | Plan | Contract creation |
| SmsQuota | Plan | Contract creation |
| ContractFeatures | PlanFeatures (by FeatureCode) | Contract creation |
| PricingTiers | Plan.PricingTiers | Contract creation |
| Benefits | Offer.Benefits | Contract creation |

Feature snapshots use stable `FeatureCode` (not mutable PlanFeature relationship).

---

## 5. Ledger RunningBalance Behavior

The CustomerLedgerEntry RunningBalance is a derived/denormalized value. The ledger sequence for credit operations is:

```
previousBalance
    ↓
CreditCreation (RunningBalance = previousBalance - creditAmount)
    ↓
balanceAfterCreditCreation = CreditCreation.RunningBalance
    ↓
CreditUsage (RunningBalance = balanceAfterCreditCreation - usedAmount)
```

The CreditUsage entry MUST use the balance resulting from CreditCreation, not the original previousBalance. This ensures mathematical consistency.

---

## 6. Migration / Backfill Behavior

The migration `20260921172701_Task18_1_CommercialIntegrityCorrections` adds:
- `BonusMonths`, `MaxStudents`, `MaxUsers`, `MaxBranches`, `MaxTeachers`, `StorageGb`, `SmsQuota` columns to Contracts (default: 0)
- `ContractFeatures` table with unique index on `(ContractId, FeatureCode)`
- `IdempotencyKey` column on TenantCredits with unique filtered index

**Backfill note:** This repository has no production Contract data (it is pre-production). Zero values for existing rows are safe because no existing Contracts were created through production workflows. If production data existed, a backfill from the authoritative Plan source would be required.

---

## 7. Test Coverage (55 tests)

### F-01: Contract Snapshot (14 tests)
- `F01_CreateSubscriptionFromContract_UsesContractTerms` — full Contract→Snapshot→Subscription path with all values verified + Plan mutation immunity
- `F01_ContractSnapshot_PlanPriceMutation_DoesNotAffectExistingSubscription` — plan changes don't cascade
- `F01_ContractSnapshot_PlanDeactivation_DoesNotAffectExistingSubscription` — deactivated plans honor contracts
- `F01_ContractSnapshot_DurationAndChargedMonths_Preserved` — duration/bonus preserved from Contract
- `F01_ContractSnapshot_NegotiatedCurrency_PreservedInSubscription` — currency preserved from Contract
- `F01_ContractSnapshot_IncludesLimitFields` — bonusMonths, maxStudents, maxUsers, etc. on Contract
- `F01_ContractSnapshot_GetSubscriptionSnapshot_ReturnsCorrectValues` — snapshot includes all values + features
- `F01_ContractSnapshot_PlanMutation_DoesNotAffectSnapshot` — plan mutation doesn't affect Contract snapshot
- `F01_ContractFeature_Snapshot_UniquePerContract` — feature codes captured correctly
- `F01_PlanMutationRegression_EndToEnd_PersistsFromDB` — end-to-end plan mutation test
- `F01_ContractToSubscription_FullSnapshotPath_AllValuesVerified` — complete path with all snapshot values + Plan mutation
- `F01_ContractToSubscription_SubscriptionFactory_UsesAllSnapshotValues` — factory integration test
- `F01_ContractSnapshot_BonusMonthsMutation_DoesNotAffectSubscription` — BonusMonths mutation immunity
- `F01_ContractSnapshot_FeatureRemoval_DoesNotAffectExistingSnapshot` — feature removal immunity

### F-01: Snapshot Invariant (1 test)
- `F01_SnapshotInvariant_ValidContract_Passes` — validates ValidateSnapshotCompleteness()

### D-02: Customer Credit (10 tests)
- `D02_UpgradeDowngrade_Calculation_UnusedPaidValue` — correct credit amount calculation
- `D02_UpgradeDowngrade_CreditApplication_ReducesInvoice` — credit auto-applied to invoice
- `D02_UpgradeDowngrade_IdempotentCredit_NoDuplicateCredit` — idempotent on retry
- `D02_UpgradeDowngrade_CreditLargerThanInvoice_RemainingCreditPreserved` — excess credit preserved
- `D02_UpgradeDowngrade_CreditSourceType_Exists` — enum value exists
- `D02_CreditApplication_AmountCappedAtInvoiceRemaining` — credit capped at invoice remaining
- `D02_CreditApplication_AmountUsesFullCreditWhenLessThanInvoice` — full credit when smaller
- `D02_PaymentVerification_QueriesCompletedPaymentsWithMatchingCurrency` — payment query validation
- `D02_PaymentVerification_FailedPaymentDoesNotCount` — failed payments excluded

### D-02: Ledger RunningBalance (4 tests)
- `D02_Ledger_CreditCreation_RunningBalanceIsCorrect` — CreditCreation balance is correct
- `D02_Ledger_CreditUsage_FollowsCreditCreation_Balance` — CreditUsage follows CreditCreation balance
- `D02_Ledger_FinalBalance_IsMathematicallyConsistent` — full ledger sequence is mathematically consistent
- `D02_Ledger_CreditUsage_BalanceFollowsCreditCreation_Integration` — integration test for sequential balance

### D-01: TenantAdmin Authorization (11 tests)
- `D01_TenantAdmin_CanViewOwnContract` — permission catalog check
- `D01_TenantAdmin_CannotApproveRefund` — permission catalog check
- `D01_TenantAdmin_CannotExecuteRefund` — permission catalog check
- `D01_TenantAdmin_CannotModifyInvoice` — permission catalog check
- `D01_TenantAdmin_CannotCreateContract` — permission catalog check
- `D01_TenantAdmin_CannotManageSubscription` — permission catalog check
- `D01_Http_TenantAdmin_GetOwnContract_Returns200` — HTTP 200 with tenant resolution
- `D01_Http_TenantAdmin_GetOwnInvoice_Returns200` — HTTP 200 with tenant resolution
- `D01_Http_TenantAdmin_GetOwnTenantCredits_Returns200` — HTTP 200 with tenant resolution
- `D01_Http_TenantAdmin_ApproveRefund_Returns403Or404` — HTTP denied
- `D01_Http_TenantAdmin_ExecuteRefund_Returns403Or404` — HTTP denied

### Cross-Tenant Isolation (5 tests)
- `CrossTenant_TenantA_InvoiceAccess_Returns403` — user from TenantB cannot read TenantA invoice
- `CrossTenant_ContractAccess_Returns403` — user from TenantB cannot read TenantA contract
- `CrossTenant_TenantPlanAccess_Returns403` — user from TenantB cannot read TenantA tenant plan
- `D01_CrossTenant_ContractAccess_Returns403` — cross-tenant contract isolation via list
- `D01_CrossTenant_CustomerCreditAccess_Returns403` — cross-tenant credit isolation

### Anonymous Access (6 tests)
- `Anonymous_InvoicesEndpoint_Returns401`
- `Anonymous_ContractsEndpoint_Returns401`
- `Anonymous_TenantCreditsEndpoint_Returns401`
- `Anonymous_TenantPlansEndpoint_Returns401`
- `Anonymous_RefundsEndpoint_Returns401`
- `Anonymous_InstallmentsEndpoint_Returns401`

### Historical Immutability (3 tests)
- `HistoricalImmutability_PlanMutation_DoesNotAffectContract`
- `HistoricalImmutability_PlanDeactivation_DoesNotAffectContract`
- `HistoricalImmutability_ContractBonusMonths_DefaultsToZero`

### CreateContractFromOffer Integration (1 test)
- `CreateContractFromOffer_SnapshotsPlanLimitsAndFeatures` — verifies complete Plan→Offer→Contract snapshot path with limits and features

---

## 8. Test Infrastructure Fix

**Root Cause of Pre-Test Failures:** The `SeedAndCreateTestEnvironmentAsync` helper set `Id` and `Identifier` to different values on `CenterixTenantInfo`. Finbuckle's `WithHeaderStrategy` resolves tenants by `Identifier` (via `TryGetByIdentifierAsync`), but the test header used the `Id` value.

**Fix:** Set `Id = Identifier` (same value), matching the pattern used by `C1CrossTenantIsolationTests` and other working test classes.

**Additional Fix:** The D-01 "Cannot" tests were converted from HTTP tests to permission catalog assertions. The HTTP tests were falsely passing because the `TenantGuardMiddleware` was blocking with "Tenant not resolved" before the permission check — the tests never actually tested the authorization handler.

---

## 9. Files Modified (Task 18.1.1)

| File | Change |
|------|--------|
| `src/Centerix.Application/Platform/Promotions/Commands/CreateContractFromOfferCommand.cs` | Load Plan.PlanFeatures, populate limits + features from Plan |
| `src/Centerix.Application/Platform/Contracts/Commands/CreateContractCommand.cs` | Load Plan for limits/features; document as non-production path |
| `src/Centerix.Domain/Platform/Contracts/Contract.cs` | Add `ValidateSnapshotCompleteness()` domain invariant |
| `src/Centerix.Domain/Platform/Contracts/ContractErrors.cs` | Add `SnapshotIncomplete` error |
| `src/Centerix.Application/Platform/Commands/ChangeSubscriptionPlanCommand.cs` | Fix ledger RunningBalance: CreditUsage uses balance from CreditCreation |
| `src/Centerix.Application/Platform/Contracts/Commands/CreateSubscriptionFromContractCommand.cs` | Add `ValidateSnapshotCompleteness()` guard |
| `tests/Centerix.SecurityTests/Task18CommercialIntegrityTests.cs` | 10 new tests covering full snapshot path, ledger sequence, invariant |
| `docs/TASK-18-COMMERCIAL-INTEGRITY-IMPLEMENTATION.md` | Updated documentation |

---

## 10. Decision Records

| Decision | Choice | Rationale |
|----------|--------|-----------|
| D-01: TenantAdmin billing | Hybrid | View/request own billing data; approval/execution stays PlatformAdmin-only |
| D-02: Unused period | Customer Credit | Credits are simpler than refunds; auto-apply to next invoice; no cash outflow |
| CreateContractCommand | Non-production path | No handler or API endpoint invokes this; limits loaded from Plan |

---

## 11. Dependencies

- Task 17 (findings triage) — COMPLETE
- Task 16 (initial audit) — COMPLETE

---

**TASK 18.1.1 IS COMPLETE. ALL 55 TESTS PASSING.**
