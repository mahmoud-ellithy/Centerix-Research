# Task 9.3 — Subscription Renewal & Commercial Continuation

## 30.1 Status

**COMPLETE** (Task 9.3.2 correction applied)

All acceptance criteria pass. Renewal creates a new commercial transaction (Offer → Contract → Subscription) using current commercial terms. Old subscriptions/contracts remain immutable. Old Active subscriptions are NEVER expired early by renewal.

## 30.2 Actual Commit SHA

```
TBD (Task 9.3.2 commit)
```

### Prior commits
```
d0ca39b — Task 9.3: Subscription Renewal as New Commercial Transaction
5f67c68 — Task 9.3.1: Renewal Commercial Snapshot & Financial Chain Hardening
```

## 30.3 Files Changed

| File | Change |
|---|---|
| `src/Centerix.Domain/Platform/Contracts/Contract.cs` | Added `PreviousSubscriptionId` nullable field + `LinkToPreviousSubscription()` method |
| `src/Centerix.Domain/Platform/Subscriptions/TenantPlan.cs` | Added `AddCalendarMonths` static helper (removed `ExpireEarlyForRenewal` in 9.3.2) |
| `src/Centerix.Domain/Platform/Subscriptions/TenantPlanErrors.cs` | Added renewal-specific error codes |
| `src/Centerix.Application/Platform/Commands/RenewSubscriptionOfferCommand.cs` | Core renewal handler — temporal overlap guard, no early expiration |
| `src/Centerix.Application/Platform/Subscriptions/SubscriptionFactory.cs` | Added `activate` parameter to `CreateFromSnapshotAsync` |
| `src/Centerix.Infrastructure/Data/Configurations/ContractConfiguration.cs` | Added EF configuration for `PreviousSubscriptionId` |
| `src/Centerix.Infrastructure/Data/Migrations/20260915193613_AddContractPreviousSubscriptionId.cs` | Migration for PreviousSubscriptionId column |
| `src/Centerix.API/Controllers/TenantPlansController.cs` | Added `POST /api/tenantplans/{id}/renew-commercial` endpoint + request DTO |
| `tests/Centerix.SecurityTests/Phase9_3SubscriptionRenewalTests.cs` | 46 domain-level tests |
| `tests/Centerix.SecurityTests/Phase9_3_1RenewalHardeningTests.cs` | 41 domain tests + 6 SQL Server tests |

## 30.4 Business Behavior (Task 9.3.2 Corrected)

```
Old Subscription (Active, EffectiveEndsAtUtc = future)
        │
        │ Renewal requested
        ▼
Old Subscription remains Active (UNCHANGED)
until its natural EffectiveEndsAtUtc
        │
        ▼
New Subscription starts at
Old EffectiveEndsAtUtc
        │
        ▼
New Subscription Status:
  - Pending (if startsAt > now) — coexists with old Active via unique index
  - Active (if startsAt <= now) — old already expired
```

### Critical Business Rules (Task 9.3.2)

1. **Old subscription is NEVER modified by renewal** — no early expiration, no cancellation
2. **Old subscription remains Active** until its natural `EffectiveEndsAtUtc`
3. **New subscription starts exactly at old's `EffectiveEndsAtUtc`** when old is still Active
4. **Renewal ≠ Cancellation** — renewal does not invoke refund or cancellation lifecycle
5. **Sequential entitlement** — old Active + new Pending = valid sequential arrangement

## 30.5 Renewal Rules

### Pricing
- New Offer uses **current Plan pricing** (MonthlyPrice + PricingTier)
- Old subscription's SnapshotPrice is never read for new terms

### Start Date
- Old Active with future end → `new.StartsAtUtc = old.EffectiveEndsAtUtc`
- Old Expired → `new.StartsAtUtc = now`

### Overlap Prevention (Temporal)
The overlap guard uses **temporal service period overlap**, not status-based blocking:
```
reject if: existing.StartsAtUtc < new.EffectiveEndsAtUtc
       AND existing.EffectiveEndsAtUtc > new.StartsAtUtc
       AND existing is not terminal (Expired/Cancelled)
       AND existing is not the old subscription being renewed
```
This correctly allows sequential subscriptions where old ends exactly when new begins.

### Unique Index Compatibility
- Filtered index `UX_TenantPlans_TenantId_NonTerminalStatus` filters on `Status IN (1, 4, 5)` (Active, Suspended, PastDue)
- **Pending (0) is NOT in the filter** — allows Old Active + New Pending coexistence
- When old naturally expires (Status → Expired), new can be activated (Status → Active)

### Concurrency
- SERIALIZABLE transaction serializes concurrent renewal requests
- Temporal overlap guard catches the second request (finds the first's new Pending subscription)
- Deadlock retry with exponential backoff (3 attempts)

## 30.6 Historical Integrity

The following records are NEVER mutated by renewal:

- **Old Subscription**: Status remains Active, EffectiveEndsAtUtc unchanged, all snapshot fields unchanged
- **Old Contract**: All commercial terms unchanged
- **Old Offer**: All commercial terms unchanged
- **Old Invoice/Payment/Ledger**: Unchanged

## 30.7 Test Evidence

### Build
```
Build succeeded.
  0 Warning(s)
  0 Error(s)
```

### Task 9.3 Specific Tests (InMemory)
```
Total tests: 86
     Passed: 86
```

### SQL Server Integration Tests
```
Total tests: 6
     Passed: 6
```

- `Migration_PreviousSubscriptionId_ColumnExists` — PASS
- `Migrations_NoPendingMigrations` — PASS
- `Renewal_HandlerCreates_BillingCycleAndInvoice` — PASS
- `Renewal_OldSubscription_RemainsActive_AfterRenewal` — PASS (NEW in 9.3.2)
- `SequentialDuplicateRenewal_Rejected` — PASS
- `Renewal_ConcurrentRequests_CannotBothSucceed` — PASS

### Full Test Suite (non-SQL)
```
Total tests: 886
     Passed: 884
     Failed: 2 (pre-existing: Phase3AuthorizationHttpTests.Students_*)
```

### Test Categories Covered

| Category | Tests | Status |
|---|---|---|
| Renewal eligibility | Active, Expired, Cancelled, Pending, Suspended, PastDue states | PASS |
| Commercial snapshot independence | Current pricing, new contract values, plan change | PASS |
| Historical immutability | Old contract, subscription, offer, tiers, benefits unchanged | PASS |
| Gifts/Benefits on renewal | Old gift not copied, new from offer only | PASS |
| Discounts/Promotions | Old not inherited, current through offer engine | PASS |
| Temporal overlap prevention | Sequential allowed, actual overlap blocked | PASS |
| Start date calculation | Before/after/expiry anchoring | PASS |
| Old subscription lifecycle | Remains Active, EffectiveEndsAtUtc unchanged | PASS |
| Cancellation distinction | Renewal ≠ cancellation, no refund invoked | PASS |
| Concurrency | SERIALIZABLE + temporal guard, exactly one succeeds | PASS |
| SQL Server verification | Migration, billing chain, old sub preserved, concurrency | PASS |

## 30.8 Migration

The `PreviousSubscriptionId` is a nullable `Guid?` field on `Contract`.
Migration `20260915193613_AddContractPreviousSubscriptionId` adds the column.

## 30.9 Implementation Summary

### Architecture Decision
Renewal reuses the existing Offer → Contract → Subscription architecture. No parallel renewal system.

### Key Design Choices (Task 9.3.2)
1. **No early expiration** — old subscription lifecycle is never interrupted by renewal
2. **Temporal overlap guard** — based on service periods, not status flags
3. **Pending status for future starts** — new subscription is Pending when starting in the future, coexisting with old Active via filtered unique index
4. **Conditional activation** — `SubscriptionFactory.CreateFromSnapshotAsync(activate: bool)` controls whether new subscription is immediately Active
5. **SERIALIZABLE serialization** — concurrent renewals serialized at database level

### What Was NOT Changed
- Existing `RenewSubscriptionCommand` (legacy month-appending) preserved
- No new billing/invoice/payment logic
- No referral credit system
- No automatic payment collection
- No generic idempotency framework
- No cancellation/refund invoked by renewal
