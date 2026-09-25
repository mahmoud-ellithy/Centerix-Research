# TASK-21.1: INVOICE TRUST-BOUNDARY & TRACEABILITY HARDENING

**Status**: CLOSED
**Date**: 2026-09-25
**Auditor**: Senior ASP.NET Core Backend Architect + Financial Systems Engineer

---

## 1. Findings Addressed

### INV-01 — CRITICAL (CLOSED)

**Finding**: `CreateInvoiceCommand` accepted client-controlled `Subtotal`, `DiscountAmount`, `TaxAmount`, `TotalAmount` without authoritative derivation.

**Fix Applied**:
- Added `ContractId` as a required parameter
- Server derives all amounts from `Contract.ContractedAmount` and `Contract.DiscountAmount`
- Client-supplied amounts are validated against server-derived values
- Any mismatch is rejected with `Invoice.ClientAmountMismatch` error

**Files Modified**:
- `src/Centerix.Application/Platform/Billing/Commands/CreateInvoiceCommand.cs`
- `src/Centerix.Domain/Platform/Billing/Invoicing/Invoice.cs`
- `src/Centerix.Domain/Platform/Billing/Invoicing/InvoiceErrors.cs`

### INV-02 — HIGH (CLOSED)

**Finding**: Invoice creation allowed null `ContractId`/`SubscriptionId`, breaking commercial traceability.

**Fix Applied**:
- `ContractId` is now required on `CreateInvoiceCommand`
- Handler verifies `Contract.TenantId == authorized tenant`
- If `SubscriptionId` provided, verifies `Subscription.ContractId == Contract.Id`
- Cross-tenant contract usage is rejected

**Files Modified**:
- `src/Centerix.Application/Platform/Billing/Commands/CreateInvoiceCommand.cs`
- `src/Centerix.Application/Platform/Billing/Commands/CreateInvoiceValidator.cs`

### INV-03 — HIGH (VERIFIED)

**Finding**: Missing unique index on `Invoice.InvoiceNumber` at DB level.

**Verification**: The unique index `UX_Invoices_InvoiceNumber` exists in migration `20260808221803_PendingChanges.cs` (line 631-635). The index is global (not tenant-scoped), which is acceptable for invoice numbers.

**Enhancement Added**:
- Handler checks for duplicate invoice numbers within tenant scope before creation
- Rejects with `Invoice.DuplicateInvoiceNumber` error

**No Migration Needed**: Index already exists in database schema.

---

## 2. Existing Invoice Creation Paths

| Path | Handler | Amount Source | Commercial Links | Authorization | Status |
|------|---------|-------------|----------------|--------------|--------|
| Direct Create | `CreateInvoiceHandler` | SERVER (Contract) | Required | Tenant permission | HARDENED |
| Billing Cycle | `CreateInvoiceFromBillingCycleHandler` | SERVER (SnapshotPrice × Duration) | Required | Tenant context | UNCHANGED |
| Upgrade/Downgrade | `ChangeSubscriptionPlanHandler` | SERVER (Offer + Promotions) | Required | Platform Admin | UNCHANGED |
| Renewal | `RenewSubscriptionOfferHandler` | SERVER (Plan + Promotions) | Required | Platform Admin | UNCHANGED |

---

## 3. Authoritative Amount Source

### Direct Invoice Creation

```
Client Request
    ↓
ContractId (required)
    ↓
Load Contract from DB
    ↓
Subtotal = Contract.ContractedAmount
DiscountAmount = Contract.DiscountAmount
TaxAmount = 0m (per current model)
TotalAmount = Subtotal - DiscountAmount + TaxAmount
    ↓
Invoice.Create() with server-authoritative amounts
```

### Other Paths (Unchanged)

| Path | Source |
|------|--------|
| Billing Cycle | `Subscription.SnapshotPrice × DurationMonths` |
| Upgrade/Downgrade | `PromotionCalculationService.Calculate()` → `Offer.FinalAmount` |
| Renewal | `PromotionCalculationService.Calculate()` → `Offer.FinalAmount` |

---

## 4. Traceability Rules

### Commercial Chain Enforcement

```
Invoice.ContractId → Contract.TenantId == Authorized Tenant
    ↓
Invoice.SubscriptionId → Subscription.ContractId == Contract.Id (if provided)
    ↓
Invoice.SubscriptionId → Subscription.TenantId == Authorized Tenant (if provided)
```

### Error Codes

| Scenario | Error Code |
|----------|-----------|
| Contract not found | `Invoice.ContractNotFound` |
| Contract not owned by tenant | `Invoice.ContractNotOwnedByTenant` |
| Subscription not found | `TenantPlan.PlanNotFound` |
| Subscription/Contract mismatch | `Invoice.SubscriptionContractMismatch` |
| Subscription not owned by tenant | `Invoice.ContractNotOwnedByTenant` |
| Client amount mismatch | `Invoice.ClientAmountMismatch` |

---

## 5. Authorization Decision

**Chosen Option**: C — Controlled tenant operation

Invoice creation remains tenant-scoped with the following controls:
- Mandatory `ContractId` linked to the tenant
- Server-authoritative amount derivation
- No client monetary override allowed
- Complete tenant ownership verification

**Rationale**: Platform admin operations (upgrade/downgrade, renewal) already require `PlatformAdminGuard`. Tenant-side invoice creation is appropriate for manual invoice generation within the tenant's commercial context, provided commercial traceability is enforced.

---

## 6. Invoice Number Uniqueness

### Database Level

- **Index**: `UX_Invoices_InvoiceNumber` (global uniqueness)
- **Migration**: `20260808221803_PendingChanges.cs` (line 631-635)
- **Scope**: Global (not tenant-scoped)
- **Status**: Verified to exist

### Application Level

- Handler checks for duplicate invoice numbers within tenant scope
- Returns `Invoice.DuplicateInvoiceNumber` error if duplicate detected

---

## 7. Database Changes

**No new migrations required.**

The `UX_Invoices_InvoiceNumber` unique index already exists in migration `20260808221803_PendingChanges.cs`.

---

## 8. Tests

### TASK21_InvoiceTrustBoundaryTests.cs

| Test | Scenario | Expected Result |
|------|---------|----------------|
| `ClientAmountTampering_TotalAmountMismatch_Rejects` | Client sends TotalAmount=1, authoritative=9500 | Rejected |
| `ClientDiscountTampering_DiscountAmountMismatch_Rejects` | Client sends DiscountAmount=999999 | Rejected |
| `ClientSubtotalTampering_SubtotalMismatch_Rejects` | Client sends Subtotal != authoritative | Rejected |
| `MissingContract_ContractNotFound_Rejects` | No ContractId provided | Rejected |
| `CrossTenantContract_TenantBContract_Rejects` | Tenant A uses Tenant B ContractId | Rejected |
| `ContractSubscriptionMismatch_ContractASubscriptionB_Rejects` | Contract A + Subscription B | Rejected |
| `SubscriptionCrossTenantMismatch_Rejects` | Cross-tenant subscription usage | Rejected |
| `InvoiceNumberUniqueness_SameTenant_SameNumber_RejectsDuplicate` | Duplicate invoice number | Rejected |
| `ValidServerDerivedInvoice_NoClientAmounts_Succeeds` | Valid ContractId, no client amounts | Invoice created |
| `InvoiceMathematicalIntegrity_ValidAmounts_CreatesInvoice` | Amounts match formula | Invoice created |
| `ValidInvoice_WithSubscription_LinksCorrectly` | Valid Contract + Subscription | Invoice linked |

**Test Results**: 11/11 PASSED

---

## 9. Build / EF Evidence

```
dotnet build
Build succeeded.
    0 Error(s)
```

```
dotnet ef migrations has-pending-model-changes
No pending model changes detected.
```

---

## 10. Regression Evidence

### Existing Invoice Flows Unchanged

| Flow | Verification |
|------|--------------|
| Billing Cycle Invoice | `CreateInvoiceFromBillingCycleHandler` unchanged - derives from `Subscription.SnapshotPrice` |
| Upgrade/Downgrade | `ChangeSubscriptionPlanHandler` unchanged - derives from `PromotionCalculationService` |
| Renewal | `RenewSubscriptionOfferHandler` unchanged - derives from `PromotionCalculationService` |

### Domain Integrity

- `Invoice.Create()` now validates `TotalAmount == Subtotal - DiscountAmount + TaxAmount` (within 0.01m tolerance)
- All existing tests in `Phase10InvoiceFinancialIntegrityTests.cs` continue to pass

---

## 11. Remaining Task 21 Findings

### Not Addressed in This Task

| Finding | Severity | Reason |
|---------|----------|--------|
| TenantCredits.Create lacks PlatformAdminGuard | MEDIUM | Business decision - tenant-scoped adjustment |
| ApplyCreditToInvoiceHandler lacks PlatformAdminGuard | MEDIUM | Business decision - tenant internal operation |
| CreditApplication precision (10,2 vs 18,2) | MEDIUM | Technical debt - precision standardization |
| SubscriptionPolicy seed | MEDIUM | Deployment concern - must be seeded |
| Ledger duplicate credit creation | MEDIUM | Low probability edge case |

These findings are outside the scope of invoice trust-boundary hardening.

---

## 12. Closure Criteria Verification

- [x] INV-01 closed — Server-authoritative amount derivation implemented
- [x] INV-02 closed — ContractId required, traceability verified
- [x] INV-03 verified — Unique index exists (global scope, acceptable)
- [x] Client cannot manipulate authoritative invoice amount
- [x] Invoice has complete commercial traceability
- [x] Tenant isolation verified (cross-tenant rejected)
- [x] Existing internal invoice flows still work
- [x] SQL Server uniqueness verified (index exists in migration)
- [x] EF model has no pending changes
- [x] Build passes
- [x] Full regression passes (11/11 TASK21 tests + existing tests)
- [x] No unrelated production logic changed
- [x] Documentation accurately reports evidence

---

*Implementation completed: 2026-09-25*
*All tests pass, build succeeds, no regressions detected.*
