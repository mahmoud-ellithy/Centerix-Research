# Task 9.5 / 9.5.1 — Subscription Natural Expiration & Lifecycle Completion

## Task 9.5 Status: COMPLETE
## Task 9.5.1 Status: COMPLETE

### Commit SHA:
```
efee5700fed5b848ac34b56918b2961b2b93bde6
```

### Build:
**PASS** — 0 errors, only pre-existing StyleCop warnings.

---

### Files Modified

| File | Change |
|------|--------|
| `src/Centerix.Domain/Platform/Subscriptions/TenantPlan.cs:281-303` | `MarkExpired` now allows `Active`, `PastDue`, and `Suspended` → `Expired`; emits `TenantPlanExpiredEvent` |
| `src/Centerix.Infrastructure/Platform/SubscriptionReconciliationService.cs:44-67` | Natural expiration now covers `Active`, `PastDue`, and `Suspended` states (not just `Active`) |
| `src/Centerix.Infrastructure/Platform/SubscriptionStateService.cs:17-47` | Injected `TimeProvider` (replaced `DateTime.UtcNow`); removed redundant inline MarkExpired call (reconciliation handles it) |

### Files Created

| File | Purpose |
|------|---------|
| `src/Centerix.Domain/Platform/Subscriptions/Events/TenantPlanExpiredEvent.cs` | Domain event emitted on natural expiration |
| `tests/Centerix.SecurityTests/Phase9_5SubscriptionNaturalExpirationTests.cs` | 40 InMemory tests covering all Task 9.5 requirements |
| `tests/Centerix.SecurityTests/Phase9_5_1NaturalExpirationConcurrencySqlServerTests.cs` | 11 SQL Server concurrency tests for Task 9.5.1 |

---

### Task 9.5 Tests (InMemory):
**40/40 PASS**

| # | Test | Result |
|---|------|--------|
| 1 | `Active_BeforeEnd_RemainsActive` | PASS |
| 2 | `Active_AtEnd_BecomesExpired` | PASS |
| 3 | `Active_AfterEnd_BecomesExpired` | PASS |
| 4 | `PastDue_AtEnd_BecomesExpired` | PASS |
| 5 | `PastDue_BeforeEnd_RemainsPastDue` | PASS |
| 6 | `Suspended_AtEnd_BecomesExpired` | PASS |
| 7 | `Suspended_BeforeEnd_RemainsSuspended` | PASS |
| 8 | `Cancelled_RemainsCancelled_RegardlessOfEnd` | PASS |
| 9 | `Expired_RemainsExpired_Idempotent` | PASS |
| 10 | `Pending_CannotBeExpired` | PASS |
| 11 | `Expiration_EmitsDomainEvent` | PASS |
| 12 | `Expiration_Idempotent_NoDuplicateEvents` | PASS |
| 13 | `Expiration_DoesNotCreateRefund` | PASS |
| 14 | `PastDue_Expiration_DoesNotCreateRefund` | PASS |
| 15 | `Expiration_PaidInstallments_RemainPaid` | PASS |
| 16 | `Expiration_UnpaidInstallments_RemainUnpaid` | PASS |
| 17 | `Expiration_DoesNotDeletePayments` | PASS |
| 18 | `Reconciliation_Active_EndReached_BecomesExpired` | PASS |
| 19 | `Reconciliation_PastDue_EndReached_BecomesExpired` | PASS |
| 20 | `Reconciliation_Suspended_EndReached_BecomesExpired` | PASS |
| 21 | `Reconciliation_Cancelled_EndReached_RemainsCancelled` | PASS |
| 22 | `Reconciliation_Expired_RemainsExpired` | PASS |
| 23 | `Reconciliation_PastDue_EndNotReached_RemainsPastDue` | PASS |
| 24 | `Reconciliation_Idempotent_Expired_BecomesExpiredOnce` | PASS |
| 25 | `Reconciliation_Idempotent_NoDuplicateSideEffects` | PASS |
| 26 | `TenantIsolation_ReconcileTenantA_DoesNotExpireTenantB` | PASS |
| 27 | `Renewal_OldSubscription_ExpiresIndependently` | PASS |
| 28 | `Renewal_NewSubscription_HasOwnCommercialTerms` | PASS |
| 29 | `Cancelled_Subscription_CannotBeExpired` | PASS |
| 30 | `SubscriptionStateService_UsesTimeProvider_NotDateTimeUtcNow` | PASS |
| 31 | `SubscriptionStateService_ActiveBeforeEnd_ReturnsActive` | PASS |
| 32 | `RepeatedReconciliation_ConvergesToExpired` | PASS |
| 33 | `RepeatedReconciliation_NoDuplicateRefunds` | PASS |
| 34 | `NoExpireSubscriptionCommand_Exists` | PASS |
| 35 | `MarkExpired_IsPublic_ButGuardedByTimeCheck` | PASS |
| 36 | `Expiration_DoesNotCancelInstallments` | PASS |
| 37 | `Expiration_HistoricalRecords_Unchanged` | PASS |
| 38 | `Expiration_PreservesCommercialSnapshot` | PASS |
| 39 | `TenantLifecycle_MultipleSubscriptions_OldExpires_NewRemains` | PASS |
| 40 | `Expiration_DoesNotModifyContractPricing` | PASS |

---

### Task 9.5.1 SQL Server Concurrency Tests:
**11/11 PASS** (REAL SQL Server via Testcontainers)

| # | Test | Result |
|---|------|--------|
| 1 | `ConcurrentExpiration_Active_EndReached_BecomesExpired` | PASS |
| 2 | `ConcurrentExpiration_PastDue_EndReached_BecomesExpired` | PASS |
| 3 | `ConcurrentExpiration_Suspended_EndReached_BecomesExpired` | PASS |
| 4 | `ConcurrentExpiration_FinancialIntegrity_NoSideEffects` | PASS |
| 5 | `ConcurrentExpiration_DomainEvent_ExactlyOneExpirationTransition` | PASS |
| 6 | `SequentialReconciliation_Idempotent_ActiveToExpired` | PASS |
| 7 | `ConcurrentExpiration_FinalStateMatchesSequential` | PASS |
| 8 | `ConcurrentExpiration_IndependentDbContexts_NoStaleTracker` | PASS |
| 9 | `ConcurrentExpiration_Cancelled_RemainsCancelled` | PASS |
| 10 | `ConcurrentExpiration_Active_NotYetEnded_RemainsActive` | PASS |
| 11 | `ConcurrentExpiration_TenantIsolation_ADoesNotAffectB` | PASS |

---

### Subscription Tests (Phase 9.1 / 9.1.1 / 9.1.2 / 9.2):
**All PASS** — No regressions in existing state machine, reconciliation, installment ownership, or finalization tests.

---

### Renewal Tests (Phase 9.3 / 9.3.1 / 9.3.3):
**All PASS** — Renewal creates new subscription; old subscription lifecycle unaffected by renewal.

---

### Cancellation Tests (Phase 9.4 / 9.4.2):
**All PASS** — Cancellation refund engine unaffected.

---

### Financial Tests:
**All PASS** — Expiration creates no Refund, no Invoice mutation, no Payment deletion, no PaymentAllocation mutation, no Installment cancellation. Paid installments remain Paid.

---

### SQL Server Concurrency Evidence:

```
SQL Server Natural Expiration Concurrency: PASS
Testcontainers: PASS (local SQL Server)
Concurrent Active: PASS
Concurrent PastDue: PASS
Concurrent Suspended: PASS
```

**Concurrency Strategy:**
- Both concurrent reconciliation operations create independent `AppDbContext` instances via separate DI scopes
- Both load the same `TenantPlan` row (with identical `RowVersion`) via `IgnoreQueryFilters()`
- Both call `MarkExpired(now)` → set `Status = Expired` and emit `TenantPlanExpiredEvent`
- `TenantPlan` has `RowVersion` (SQL Server `rowversion`) optimistic concurrency configured
- First `SaveChangesAsync` succeeds (1 row affected, RowVersion incremented)
- Second `SaveChangesAsync` encounters `DbUpdateConcurrencyException` (RowVersion mismatch)
- Second operation catches the exception gracefully — no retry loop is added
- Final state: `Expired` with no duplicate side effects
- This matches the established repository pattern (same as cancellation concurrency tests)

**ChangeTracker Safety:**
- Each concurrent operation creates its own `AppDbContext` via `_env.Factory.Services.CreateScope()`
- No shared `ChangeTracker` between operations
- No `ChangeTracker.Clear()` is needed because each scope creates a fresh context
- The `SubscriptionReconciliationService.DetachEntity()` helper detaches the entity after saving, preventing stale tracked state within a single scope

---

### Expiration Semantics:
```
EffectiveEndsAtUtc <= CurrentUtc
```
- `Active` + end reached → `Expired` (no refund)
- `PastDue` + end reached → `Expired` (no refund)
- `Suspended` + end reached → `Expired` (no refund)
- `Cancelled` + end reached → `Cancelled` (terminal, unchanged)
- `Expired` + any → `Expired` (idempotent)
- `Pending` + any → rejected (not yet activated)

---

### Refund Behavior:
**No refund is created by natural expiration.** The `MarkExpired` method does NOT invoke `IRefundCalculationService`, `RefundCalculationService`, `Refund.Create`, or any refund workflow. This is verified by:
- InMemory: `Expiration_DoesNotCreateRefund`, `PastDue_Expiration_DoesNotCreateRefund`, `Expiration_PaidInstallments_RemainPaid`
- SQL Server: `ConcurrentExpiration_FinancialIntegrity_NoSideEffects` — Refund count, Payment count, PaymentAllocation count, Installment count, CustomerLedgerEntry count all unchanged after concurrent expiration

---

### Installment Behavior:
- Paid installments remain `Paid` with unchanged `SettledAmount`
- Unpaid installments remain with unchanged `RemainingAmount`
- Installments are NOT cancelled by expiration
- Historical `PaymentAllocation` records are unchanged
- Expiration is a lifecycle transition, NOT a financial settlement

---

### Tenant Lifecycle:
- Tenant lifecycle is independent from subscription lifecycle
- `Tenant.ValidUpTo` is NOT modified by natural expiration (only cancellation sets it)
- Multiple subscriptions per tenant: old expires independently; new retains its own lifecycle
- Cross-tenant isolation verified: reconciling tenant A does not affect tenant B (InMemory + SQL Server)

---

### Migration:
**None required** — No schema changes required. The change extends an existing guard condition and adds a domain event; no new columns, tables, or indexes.

---

### Known Limitations:
1. **Domain event handler**: `TenantPlanExpiredEvent` is emitted but no handler is registered yet. This is consistent with existing pattern — `TenantPlanRenewedEvent` and `TenantPlanCancelledEvent` were added in earlier tasks without handlers.
2. **`SubscriptionStateService` simplified**: Removed the redundant inline `MarkExpired` call that existed before (the reconciliation service now handles all states). This is safe because `GetCurrentAsync` always triggers `ReconcileAsync` first, which handles expiration for Active/PastDue/Suspended.
