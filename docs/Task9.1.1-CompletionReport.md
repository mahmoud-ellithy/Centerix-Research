# Task 9.1.1 — Subscription State Machine Hardening: Completion Report

## Status: COMPLETE

## Commit SHA
`0bd116b` (commit includes: SubscriptionReconciliationService fix + 29 tests + completion report)

## P1 Findings Addressed

### P1-1: Hardcoded Grace Period Fallback Removed

**Before:** `GetGracePeriodDaysAsync()` silently fell back to `?? 7` when `SubscriptionPolicy` was missing, inventing a business value that could differ from the seeded platform policy.

**After:** Throws `InvalidOperationException` when `SubscriptionPolicy` is not configured. The central commercial policy must be seeded before subscription reconciliation can operate.

```csharp
// SubscriptionReconciliationService.cs — GetGracePeriodDaysAsync()
if (policy is null)
    throw new InvalidOperationException(
        "SubscriptionPolicy is not configured. The central commercial policy " +
        "must be seeded before subscription reconciliation can operate. " +
        "Grace period is a centrally controlled platform policy that cannot be " +
        "silently defaulted to an invented business value.");

return policy.GracePeriodDays;
```

**Rationale:** The grace period is a centrally controlled business value. Inventing one at runtime would silently apply an unintended policy, potentially causing subscriptions to transition to PastDue/Suspended too early or too late.

---

### P1-2: Installment Ownership Scoping

**Before:** Reconciliation loaded ALL installments for a contract without filtering by `TenantId` or `SubscriptionId`, violating tenant isolation and potentially including unrelated subscription installments.

**After:** Query scopes installments to the subscription's contract AND tenant, with null-backward-compat for unlinked installments:

```csharp
// SubscriptionReconciliationService.cs — ReconcileAsync()
var installments = await dbContext.Installments
    .Where(i => i.ContractId == subscription.ContractId.Value
        && i.TenantId == tenantId
        && (i.SubscriptionId == null || i.SubscriptionId == subscription.Id))
    .ToListAsync(cancellationToken);
```

**Three-layer scoping:**
1. `ContractId == subscription.ContractId.Value` — only installments belonging to this subscription's contract
2. `TenantId == tenantId` — enforces tenant isolation at query level
3. `SubscriptionId == null || SubscriptionId == subscription.Id` — backward-compat for unlinked installments + explicit subscription scoping

---

## Correctness Verifications (9 items)

| # | Claim | Status |
|---|-------|--------|
| 1 | Reconciliation derives state from Subscription + Installments + DueDate + GracePeriod + CurrentTime | Verified |
| 2 | `Installment.IsOverdue(DateTime utcNow)` uses `DueDateUtc` correctly | Verified |
| 3 | Multiple overdue installments: oldest drives decision (`.OrderBy(i => i.DueDateUtc).FirstOrDefault()`) | Verified |
| 4 | Payment recovery: `AllocatePaymentCommand` calls `ReconcileAsync` after allocation | Verified |
| 5 | Idempotency: terminal states return early; `MarkPastDue`/`SuspendFromObligation` check current status | Verified |
| 6 | Tenant isolation: subscription + installment queries filter by `tenantId` | Verified |
| 7 | Policy authorization: no controller/action allows tenant-level modification of `SubscriptionPolicy` | Verified |
| 8 | PastDue and Suspended are system-derived — no manual state injection commands exist | Verified |
| 9 | DueDate is authoritative from Installment domain entity | Verified |

---

## Test Results

### Task 9.1.1 Tests — 29/29 PASSED

| Category | Count | Status |
|----------|-------|--------|
| Grace period configuration | 5 | PASSED |
| Installment ownership / scoping | 4 | PASSED |
| State transitions | 7 | PASSED |
| Payment recovery | 3 | PASSED |
| Idempotency | 3 | PASSED |
| Terminal states | 2 | PASSED |
| Tenant isolation / authorization | 3 | PASSED |
| Edge cases | 2 | PASSED |
| **Total** | **29** | **PASSED** |

### Full Phase 9.1 Suite — 56/56 PASSED

| Test Class | Count | Status |
|------------|-------|--------|
| `Phase9_1SubscriptionStateMachineTests` | 27 | PASSED |
| `Phase9_1_1SubscriptionReconciliationHardeningTests` | 29 | PASSED |
| **Total** | **56** | **PASSED** |

### Full SecurityTests Project — Pre-existing failures only

All 5 failures are pre-existing infrastructure tests unrelated to this task:
- `Phase3AuthorizationHttpTests` (2) — HTTP authorization tests
- `Phase8_1_1ConcurrencySqlServerTests` (2) — SQL Server concurrency tests
- `Phase9FinancialConcurrencySqlServerTests` (1) — SQL Server concurrency test

---

## Files Changed

| File | Change | Lines |
|------|--------|-------|
| `src/Centerix.Infrastructure/Platform/SubscriptionReconciliationService.cs` | Modified — removed hardcoded grace period, added 3-layer installment scoping | +15 / -8 |
| `tests/Centerix.SecurityTests/Phase9_1_1SubscriptionReconciliationHardeningTests.cs` | Created — 29 regression tests | +580 |

---

## Migration Status

**No migration required.** Changes are query-only (no schema changes). The `SubscriptionId` column on `Installment` was already nullable and indexed from Phase 9.1.

---

## Remaining Items

- **P2: Installment ownership retrofit** — Phase 9.1 installment creation commands (`CreateInstallmentScheduleCommand`, `AddInstallmentCommand`) create installments WITHOUT `SubscriptionId`. A future task should set `SubscriptionId` on creation for full subscription-level scoping. The current null-backward-compat handles this gracefully.
- **P2: Integration test** — A Testcontainers-based integration test should verify the 3-layer scoping against real SQL Server query plan behavior.
