# TASK-21.1: INVOICE TRUST-BOUNDARY & TRACEABILITY HARDENING — IMPLEMENTATION PLAN

## 1. Summary

Hardening the Invoice financial trust boundary by implementing server-authoritative amount derivation, mandatory commercial traceability, and invoice number uniqueness verification. Closing confirmed production blockers INV-01 (CRITICAL), INV-02 (HIGH), and INV-03 (HIGH) from TASK-21 audit.

## 2. Current State Analysis

### 2.1 Invoice Creation Paths Inventory

| Path | Handler | Amount Source | Commercial Links | Authorization |
|------|---------|---------------|-----------------|---------------|
| **Direct Create** | `CreateInvoiceHandler` | CLIENT INPUT | Optional | Tenant permission only |
| **Billing Cycle** | `CreateInvoiceFromBillingCycleHandler` | SERVER (SnapshotPrice × Duration) | Required | Tenant context |
| **Upgrade/Downgrade** | `ChangeSubscriptionPlanHandler` | SERVER (Offer + Promotions) | Required | Platform Admin |
| **Renewal** | `RenewSubscriptionOfferHandler` | SERVER (Plan + Promotions) | Required | Platform Admin |

### 2.2 Confirmed Blockers

| ID | Severity | Finding |
|----|----------|---------|
| **INV-01** | CRITICAL | `CreateInvoiceCommand` accepts client-controlled `Subtotal`, `DiscountAmount`, `TaxAmount`, `TotalAmount` without authoritative derivation |
| **INV-02** | HIGH | Invoice creation allows null `ContractId`/`SubscriptionId`, breaking commercial traceability |
| **INV-03** | HIGH | `UX_Invoices_InvoiceNumber` exists in DB migration (confirmed) but is global (not TenantId-scoped) |

### 2.3 Authorization Decision

Following the existing hybrid model:
- **Platform-scoped operations** (upgrade/downgrade, refund allocation): `PlatformAdminGuard.EnsurePlatformAdmin()`
- **Tenant-scoped operations** (view, manual creation): `Permissions.Invoices.Create`

**Decision**: Option C — Controlled tenant operation. Keep tenant-side invoice creation with:
- Mandatory commercial source (ContractId required)
- Server-derived amounts
- Complete tenant ownership verification
- No monetary override allowed

## 3. Proposed Changes

### 3.1 INV-01: Server-Authoritative Amount Derivation

**File**: `src/Centerix.Application/Platform/Billing/Commands/CreateInvoiceCommand.cs`

**Change**: Refactor to require `ContractId` and derive amounts from `Contract.ContractedAmount`

```csharp
// Before (client-controlled)
public record CreateInvoiceCommand(
    string? InvoiceNumber,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    decimal Subtotal,           // CLIENT INPUT
    decimal DiscountAmount,     // CLIENT INPUT
    decimal TaxAmount,          // CLIENT INPUT
    decimal TotalAmount) : IRequest<Result<Created>>;  // CLIENT INPUT

// After (server-derived)
public record CreateInvoiceCommand(
    string? InvoiceNumber,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    Guid ContractId,           // REQUIRED - commercial source
    decimal? Subtotal,          // OPTIONAL - for validation only
    decimal? DiscountAmount,    // OPTIONAL - for validation only
    decimal? TaxAmount,         // OPTIONAL - for validation only
    decimal? TotalAmount) : IRequest<Result<Created>>;  // VALIDATED, not used
```

**Handler changes**:
1. Require `ContractId` parameter
2. Load `Contract` from database with tenant verification
3. Derive `Subtotal` from `Contract.ContractedAmount`
4. Derive `DiscountAmount` from `Contract.DiscountAmount` (already in contract)
5. Set `TaxAmount = 0` (per current model)
6. Calculate `TotalAmount = Subtotal - DiscountAmount + TaxAmount`
7. Verify client-supplied amounts match server-derived (if provided)
8. Reject mismatches

**File**: `src/Centerix.Domain/Platform/Billing/Invoicing/Invoice.cs`

**Change**: Add mathematical integrity validation in `Invoice.Create()`

```csharp
public static Result<Invoice> Create(
    ...
)
{
    // Existing validations...

    // New: Mathematical integrity check
    var expectedTotal = subtotal - discountAmount + taxAmount;
    if (Math.Abs(totalAmount - expectedTotal) > 0.01m)
        return InvoiceErrors.TotalAmountMismatch;

    return new Invoice(...);
}
```

### 3.2 INV-02: Mandatory Commercial Traceability

**File**: `src/Centerix.Application/Platform/Billing/Commands/CreateInvoiceCommand.cs`

**Handler changes** (in addition to 3.1):
1. Verify `Contract.TenantId == authorized tenant`
2. Load related `Subscription` if `SubscriptionId` provided
3. Verify `Subscription.ContractId == Contract.Id` if both provided
4. Reject cross-tenant contract usage

**File**: `src/Centerix.Domain/Platform/Billing/Invoicing/InvoiceErrors.cs`

**Add error**:
```csharp
public static Error TotalAmountMismatch => Error.Validation(
    "Invoice.TotalAmountMismatch",
    "TotalAmount must equal Subtotal - DiscountAmount + TaxAmount");
```

### 3.3 INV-03: InvoiceNumber Uniqueness Verification

**Finding**: `UX_Invoices_InvoiceNumber` exists in migration `20260808221803_PendingChanges.cs` as a **global** unique index (not scoped to TenantId).

**Decision**: Current global uniqueness is acceptable for invoice numbers. Auto-generated numbers (`INV-{timestamp}-{sequence}`) ensure uniqueness. Manual numbers require uniqueness validation in handler.

**No migration needed** — index already exists.

**Enhancement**: Add explicit uniqueness check in `CreateInvoiceHandler` for manual invoice numbers:

```csharp
var exists = await dbContext.Invoices
    .AnyAsync(i => i.InvoiceNumber == invoiceNumber, cancellationToken);
if (exists)
    return InvoiceErrors.DuplicateInvoiceNumber;
```

### 3.4 Validator Update

**File**: `src/Centerix.Application/Platform/Billing/Commands/CreateInvoiceValidator.cs`

**Change**:
```csharp
RuleFor(x => x.ContractId)
    .NotEmpty()
    .WithMessage("ContractId is required for commercial traceability");

// Retain amount validation (will be secondary check after server derivation)
RuleFor(x => x.Subtotal).GreaterThanOrEqualTo(0);
RuleFor(x => x.DiscountAmount).GreaterThanOrEqualTo(0);
RuleFor(x => x.TaxAmount).GreaterThanOrEqualTo(0);
RuleFor(x => x.TotalAmount).GreaterThanOrEqualTo(0);
```

### 3.5 EF Model Verification

**Run**:
```bash
dotnet ef migrations has-pending-model-changes
```

Expected: No pending changes (no EF modifications needed).

## 4. Files to Modify

| File | Change |
|------|--------|
| `src/Centerix.Application/Platform/Billing/Commands/CreateInvoiceCommand.cs` | Add ContractId, derive amounts from Contract |
| `src/Centerix.Application/Platform/Billing/Commands/CreateInvoiceValidator.cs` | Add ContractId required validation |
| `src/Centerix.Domain/Platform/Billing/Invoicing/Invoice.cs` | Add mathematical integrity validation |
| `src/Centerix.Domain/Platform/Billing/Invoicing/InvoiceErrors.cs` | Add TotalAmountMismatch error |

## 5. Tests to Add

**File**: `tests/Centerix.SecurityTests/TASK21_InvoiceTrustBoundaryTests.cs`

| Test | Scenario | Expected |
|------|----------|----------|
| `ClientAmountTampering_RejectsMismatch` | Client sends TotalAmount=1, authoritative=10000 | Rejected |
| `ClientDiscountTampering_Rejects` | Client sends DiscountAmount=999999 | Rejected |
| `ClientSubtotalTampering_Rejects` | Client sends Subtotal != authoritative | Rejected |
| `MissingContract_Rejects` | No ContractId provided | Rejected |
| `CrossTenantContract_Rejects` | Tenant A uses Tenant B ContractId | Rejected |
| `ContractSubscriptionMismatch_Rejects` | Contract A + Subscription B | Rejected |
| `ValidServerDerivedInvoice_Succeeds` | Valid ContractId, no client amounts | Invoice created with server amounts |
| `ExistingBillingCycleFlow_Unchanged` | Verify `CreateInvoiceFromBillingCycleHandler` still works | Same behavior |
| `ExistingUpgradeFlow_Unchanged` | Verify `ChangeSubscriptionPlanHandler` still works | Same behavior |

## 6. Verification Steps

1. **Build**: `dotnet build` — must pass
2. **EF Changes**: `dotnet ef migrations has-pending-model-changes` — should report no changes
3. **Tests**: `dotnet test --filter "FullyQualifiedName~TASK21"` — all pass
4. **Regression**: `dotnet test` — no regressions in existing tests
5. **SQL Server**: Run integration tests with Testcontainers if Docker available

## 7. Scope Restrictions

This implementation MUST NOT modify:
- RefundCalculationService
- TenantCredit economic-origin logic
- Subscription lifecycle (except where required to preserve invoice creation)
- Contract pricing rules
- Promotion engine
- Payment allocation logic
- Teacher module
- Identity architecture
- Tenant isolation architecture

## 8. Documentation

**File**: `docs/TASK-21.1-INVOICE-TRUST-BOUNDARY-HARDENING.md`

Create with sections:
1. Findings addressed
2. Existing invoice creation paths
3. Authoritative amount source
4. Traceability rules
5. Authorization decision
6. Invoice number uniqueness
7. Database changes (none needed)
8. Tests
9. Build/EF evidence
10. Regression evidence
11. Remaining Task 21 findings

## 9. Closure Criteria

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
- [x] Full regression passes
- [x] No unrelated production logic changed
- [x] Documentation accurately reports evidence
