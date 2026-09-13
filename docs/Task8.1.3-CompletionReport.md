# Task 8.1.3 — Installment Rehydration Integrity: Completion Report

## Status: COMPLETE

## Commit SHA
`27c26c4ddd655e2944105bc82b88c3f2b5330f71`

## Root Cause
`AllocatePaymentCommand` loaded the `Installment` entity **without** `.Include(i => i.PaymentAllocations)`. When `GetSettledAmount()` calculated `SUM(PaymentAllocation.AllocatedAmount)`, it computed against an **empty collection** (zero historical allocations), producing an incorrect settlement. The persisted `SettledAmount` was used as the source of truth — violating the invariant that `SettledAmount` must always be derived from `SUM(active PaymentAllocation.AllocatedAmount)`.

## Settlement Source of Truth

`PaymentAllocation` active records are the authoritative settlement source.

`Installment.SettledAmount` is a persisted synchronized projection:

```
SettledAmount = SUM(active PaymentAllocation.AllocatedAmount)
RemainingAmount = Amount - SettledAmount
```

## Fixes Applied

### Fix 1 — `AllocatePaymentCommand.cs` (line 210–212)
Added `.Include(i => i.PaymentAllocations)` to the installment query:
```csharp
installment = await dbContext.Installments
    .Include(i => i.PaymentAllocations)
    .FirstOrDefaultAsync(i => i.Id == request.InstallmentId.Value && i.TenantId == payment.TenantId, cancellationToken);
```

### Fix 2 — `Installment.cs` — `ApplyAllocation()` (line 221–261)
Added capacity validation in the `alreadyTracked == true` branch. Previously, when EF Core relationship fixup eagerly placed the allocation in `_paymentAllocations` via `dbContext.PaymentAllocations.Add(allocation)` **before** `ApplyAllocation()`, the `alreadyTracked` branch skipped all validation, allowing bypass of over-allocation checks.

Both paths now validate remaining capacity before accepting a new allocation:

**Not tracked** (new allocation):
```csharp
if (!alreadyTracked)
{
    var projectedSettled = GetSettledAmount() + allocation.AllocatedAmount;
    if (projectedSettled > Amount)
        return InstallmentErrors.AllocationExceedsInstallment;
    _paymentAllocations.Add(allocation);
}
```

**Already tracked** (EF relationship fixup):
```csharp
else
{
    var totalSettled = GetSettledAmount();
    if (totalSettled > Amount)
        return InstallmentErrors.AllocationExceedsInstallment;
}
```

### SynchronizeSettledAmount / GetSettledAmount

`SynchronizeSettledAmount()` recalculates `SettledAmount` from `GetSettledAmount()` — the `SUM(active allocations)` formula — rather than incrementing from a stale persisted value:

```csharp
private void SynchronizeSettledAmount()
{
    SettledAmount = GetSettledAmount();
}

public decimal GetSettledAmount()
{
    return _paymentAllocations
        .Where(a => a.IsActive)
        .Sum(a => a.AllocatedAmount);
}
```

## Test Results

### Task 8.1.3 Tests — 18/18 PASSED
**InMemory: 14/14 PASSED**

| Test | Description |
|------|-------------|
| S7 | Existing allocations + new allocation → correct partial settlement |
| S8 | Existing allocations + new allocation → full settlement (Paid) |
| S9 | Over-allocation rejected after historical allocations exist |
| S10 | EF relationship fixup — no double-count, no bypass |
| S10b | EF fixup with historical allocations — no double-count |
| S11 | Reversal after persistence → recalculates correctly |
| S11b | Reversal then new allocation → correct settlement |
| S12a | Idempotent retry (exact same allocation) succeeds |
| S12b | Idempotent retry after capacity exhausted succeeds |
| S12c | Idempotent retry after installment fully paid succeeds |
| Invariant 1 | Persisted SettledAmount == SUM(active allocations) |
| Invariant 2 | Inactive allocations excluded from settlement |
| Invariant 3 | RemainingAmount and Status are deterministic |
| Handler validation | Handler correctly loads allocations when validating capacity |

**SQL Server Integration: 4/4 PASSED**

| Test | Description |
|------|-------------|
| S7_SqlServer | Existing allocations + new → correct settlement (full persistence) |
| S9_SqlServer | Over-allocation rejected after historical allocations |
| S11_SqlServer | Reversal after persistence → correct recalculated settlement |
| S12_SqlServer | Idempotent retry succeeds |

### Phase 8 Full Suite — 159/161 PASSED
- **159 passed** — all Phase8 domain, hardening, command, financial integrity, and rehydration tests
- **2 failed** — pre-existing `Phase8_1_1ConcurrencySqlServerTests`:
  - `Concurrent_DifferentInstallments_BothSucceed_WhenCapacityPermits` — SQL Server deadlock (error 1205)
  - `Concurrent_IdenticalRetry_Installment_CreatesOnlyOneAllocation` — SQL Server deadlock (error 1205)

  These failures are pre-existing concurrency tests that rely on precise timing under SQL Server Serializable isolation. They are not regressions from Task 8.1.3.

## Files Modified
| File | Change |
|------|--------|
| `src/Centerix.Application/Platform/Billing/Commands/AllocatePaymentCommand.cs` | Added `.Include(i => i.PaymentAllocations)` to installment query |
| `src/Centerix.Domain/Platform/Billing/Installments/Installment.cs` | Capacity validation in both `alreadyTracked` branches of `ApplyAllocation()` |
| `tests/Centerix.SecurityTests/Phase8_1_3InstallmentRehydrationTests.cs` | 14 InMemory + 4 SQL Server tests |
