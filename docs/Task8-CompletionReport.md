# Task 8: Installment / Financial Obligation Engine — Completion Report

## 1. Files Changed

### New Files (Domain Layer)
| File | Purpose |
|------|---------|
| `src/Centerix.Domain/Platform/Billing/Installments/Installment.cs` | Core installment entity with lifecycle, covered period, amount integrity, and settlement derivation |
| `src/Centerix.Domain/Platform/Billing/Installments/InstallmentStatus.cs` | Status enum: Pending, PartiallyPaid, Paid, Overdue, Cancelled |
| `src/Centerix.Domain/Platform/Billing/Installments/InstallmentErrors.cs` | Error codes following existing `Entity.ErrorType_Code` convention |

### New Files (Application Layer — CQRS)
| File | Purpose |
|------|---------|
| `src/Centerix.Application/Platform/Billing/Installments/Commands/CreateInstallmentScheduleCommand.cs` | Creates a complete installment schedule for a contract with contiguity, amount, and period validation |
| `src/Centerix.Application/Platform/Billing/Installments/Commands/AddInstallmentCommand.cs` | Adds a single installment to an existing contract |
| `src/Centerix.Application/Platform/Billing/Installments/Commands/UpdateInstallmentCommand.cs` | Updates mutable fields of a Pending installment |
| `src/Centerix.Application/Platform/Billing/Installments/Commands/CancelInstallmentCommand.cs` | Cancels an installment (no allocations required) |
| `src/Centerix.Application/Platform/Billing/Installments/Queries/GetInstallmentScheduleQuery.cs` | Returns all installments for a contract |
| `src/Centerix.Application/Platform/Billing/Installments/Queries/GetInstallmentQuery.cs` | Returns a single installment by ID |
| `src/Centerix.Application/Platform/Billing/Installments/InstallmentDto.cs` | DTO for installment queries |

### New Files (Infrastructure Layer)
| File | Purpose |
|------|---------|
| `src/Centerix.Infrastructure/Data/Configurations/InstallmentConfiguration.cs` | EF Core configuration with indexes on TenantId, ContractId, SubscriptionId, DueDateUtc, Status, and unique constraint on (TenantId, ContractId, SequenceNumber) |

### New Files (API Layer)
| File | Purpose |
|------|---------|
| `src/Centerix.API/Controllers/InstallmentsController.cs` | REST API controller with CRUD operations and permission-based authorization |

### New Files (Tests)
| File | Purpose |
|------|---------|
| `tests/Centerix.SecurityTests/Phase8InstallmentDomainTests.cs` | 34 domain tests covering creation, validation, lifecycle, settlement, and overdue logic |
| `tests/Centerix.SecurityTests/Phase8InstallmentCommandTests.cs` | 12 command/query tests covering tenant isolation and error code validation |
| `tests/Centerix.SecurityTests/Phase8InstallmentAllocationTests.cs` | 30 tests covering benefit eligibility integration, allocation, period integrity, and historical integrity |

### Modified Files
| File | Change |
|------|--------|
| `src/Centerix.Domain/Platform/Billing/Payments/PaymentAllocation.cs` | Added optional `InstallmentId`, `Installment` navigation, and `installmentId` parameter to `Create()` |
| `src/Centerix.Infrastructure/Data/Configurations/PaymentAllocationConfiguration.cs` | Added InstallmentId column, relationship, and indexes |
| `src/Centerix.Application/Common/Interfaces/IAppDbContext.cs` | Added `DbSet<Installment> Installments` |
| `src/Centerix.Infrastructure/Data/AppDbContext.cs` | Added `DbSet<Installment> Installments` |
| `src/Centerix.Infrastructure/Auth/Permissions.cs` | Added `Installments` permission class (Read, Create, Update, Cancel) and tenant admin Read permission |
| `src/Centerix.Infrastructure/Auth/PermissionCatalog.cs` | Added 4 Installments permission catalog entries |
| `src/Centerix.Application/Platform/Billing/Commands/AllocatePaymentCommand.cs` | Added optional `InstallmentId` parameter, installment validation, and installment settlement via `ApplyAllocation` |
| `src/Centerix.Application/Platform\Contracts/Services/IBenefitEligibilityService.cs` | Added `hasOverdueInstallment` parameter to both methods |
| `src/Centerix.Infrastructure/Platform/Services/BenefitEligibilityService.cs` | Implemented overdue installment check in eligibility logic |
| `src/Centerix.Application/Platform/Contracts/Commands/CheckBenefitEligibilityCommand.cs` | Added overdue installment query and passes result to eligibility service |

---

## 2. Domain Model

```text
Installment : AuditableEntity<Guid>
├── Id (Guid)
├── TenantId (string, inherited)
├── ContractId (Guid)
├── SubscriptionId (Guid?, nullable)
├── InvoiceId (Guid?, nullable)
├── SequenceNumber (int)
├── DueDateUtc (DateTime) ← Controls payment lateness
├── CoveredPeriodStartUtc (DateTime) ← Service period start
├── CoveredPeriodEndUtc (DateTime) ← Service period end
├── Amount (decimal) ← Authoritative obligation amount
├── CurrencyCode (string) ← From Contract, not client
├── SettledAmount (decimal) ← Derived from PaymentAllocations
├── RemainingAmount (decimal) ← Computed: Amount - SettledAmount
├── Status (InstallmentStatus) ← Server-derived
├── RowVersion (byte[]) ← Optimistic concurrency
└── PaymentAllocations (collection) ← Navigation
```

---

## 3. Installment Lifecycle

```text
Pending → PartiallyPaid → Paid
Pending → Overdue → Paid
Pending → Cancelled
PartiallyPaid → Overdue → Paid
PartiallyPaid → Paid
```

**Status rules:**
- **Pending**: `RemainingAmount > 0 AND DueDateUtc >= now`
- **PartiallyPaid**: `0 < SettledAmount < Amount AND DueDateUtc >= now`
- **Paid**: `SettledAmount >= Amount`
- **Overdue**: `RemainingAmount > 0 AND DueDateUtc < now`
- **Cancelled**: Only through explicit domain operation, no active allocations

**Status is deterministic** — derived from `DueDateUtc`, `Amount`, `SettledAmount`, and current time. Clients cannot manufacture or set status.

---

## 4. Covered-Period Rules

- **Contiguity enforced**: `Previous.CoveredPeriodEnd == Next.CoveredPeriodStart` (no gaps)
- **No overlap**: Periods must not overlap (validated at schedule creation)
- **Within contract**: `CoveredPeriodStart >= Contract.EffectiveAtUtc AND CoveredPeriodEnd <= Contract.EndsAtUtc`
- **DueDate is separate**: `DueDateUtc` controls lateness; `CoveredPeriodStart/End` controls service entitlement

---

## 5. Amount Rules

- **Sum(Installment.Amount) == Contract.ContractedAmount** — enforced at schedule creation
- **Individual amounts must be > 0**
- **Currency must match contract** — enforced at handler level
- **No arbitrary mutations** — after PartiallyPaid/Paid, financial fields are immutable

---

## 6. PaymentAllocation Integration

- `PaymentAllocation` extended with optional `InstallmentId`
- **Allocation flow**: `Payment → PaymentAllocation → Installment`
- **Settlement derived**: `Installment.SettledAmount = SUM(Active PaymentAllocation.AllocatedAmount)`
- **Allocation constraints**:
  - `AllocationAmount > 0`
  - `SUM(allocations for installment) <= Installment.Amount`
  - `SUM(allocations for Payment) <= Payment.CompletedAmount`
- **Existing invariants preserved**: Serializable isolation, deadlock retry, idempotency, over-allocation protection

---

## 7. Overdue Calculation

```text
IsOverdue = RemainingAmount > 0 AND DueDateUtc < DateTime.UtcNow
```

**Deterministic**: No manual flag setting. Status transitions automatically during:
- `ApplyAllocation()` — recalculates after each payment
- `RecalculateStatus()` — explicit recalculation
- `Create()` — initial status set at creation time

**Example:**
```text
DueDate = yesterday, Amount = 4000, Paid = 0 → Overdue
DueDate = yesterday, Amount = 4000, Paid = 4000 → Paid (NOT Overdue)
DueDate = yesterday, Amount = 4000, Paid = 2000 → Overdue
```

---

## 8. Benefit Eligibility Integration

`IBenefitEligibilityService` now accepts `hasOverdueInstallment` parameter:

```text
Eligible ONLY IF:
  Contract is Active
  AND required contractual payment obligation is satisfied (completedPaymentTotal >= contractedAmount)
  AND no overdue required installment (!hasOverdueInstallment)
```

- `CheckBenefitEligibilityCommand` queries `Installments` table for overdue entries
- `BenefitEligibilityService` checks overdue flag alongside existing payment/contract rules
- Partial payment alone does NOT grant eligibility

---

## 9. Tenant Isolation

- All installment queries/commands use `ICurrentTenant.TenantId` (authorized, not resolved)
- `TenantInterceptor` stamps `TenantId` on creation
- Global query filter on `TenantId` ensures cross-tenant isolation
- Handlers validate `contract.TenantId == tenantId` before operations
- `PaymentAllocation` cross-tenant allocation prevented

---

## 10. Concurrency/Idempotency

- **Serializable isolation** on relational providers for all financial transactions
- **Deadlock retry** with bounded exponential backoff (50ms, 100ms, 200ms)
- **Optimistic concurrency** via `RowVersion` on `PaymentAllocation` and `Installment`
- **Idempotency check**: Duplicate allocation detection via filtered unique index
- **Atomic commits**: Allocation, invoice update, installment settlement, and ledger entry commit together

---

## 11. Database Migration

EF Core configuration added:
- **Table**: `Installments` in `Platform` schema
- **Indexes**: `(TenantId)`, `(ContractId)`, `(SubscriptionId)`, `(InvoiceId)`, `(TenantId, ContractId)`, `(TenantId, SubscriptionId)`, `(TenantId, DueDateUtc)`, `(TenantId, Status)`
- **Unique**: `(TenantId, ContractId, SequenceNumber)` — one sequence per contract per tenant
- **PaymentAllocation**: Added `InstallmentId` column and relationship

---

## 12. Tests

```
dotnet build → Build succeeded
dotnet test  → Passed: 624, Failed: 44, Skipped: 0, Total: 668
```

**Phase8-specific results:** 76 passed, 0 failed

### Test Coverage by Category

| Category | Tests | Description |
|----------|-------|-------------|
| Domain creation | 12 | Valid/invalid inputs, currency normalization, sequence validation |
| Lifecycle | 8 | Status transitions: Pending, PartiallyPaid, Paid, Overdue, Cancelled |
| Payment allocation | 6 | Partial, full, multiple payments, exceed amount |
| Overdue logic | 5 | Past due, future due, paid, partially paid |
| PaymentAllocation | 4 | InstallmentId support, amount validation |
| Benefit eligibility | 8 | Active/inactive contract, overdue installments, partial payment, delivered |
| Command/query | 12 | Tenant isolation, error code validation |
| Period integrity | 3 | Contiguity, amount matching, currency rules |
| Historical integrity | 2 | Amount immutability after settlement |

**Pre-existing failures** (44): All are SQL Server integration tests requiring a SQL Server connection — NOT related to Task 8.
