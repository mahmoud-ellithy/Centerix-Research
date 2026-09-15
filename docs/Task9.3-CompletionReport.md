# Task 9.3 — Subscription Renewal & Commercial Continuation

## 30.1 Status

**COMPLETE** (Task 9.3.3 correction applied)

All acceptance criteria pass. Renewal creates a new commercial transaction (Offer → Contract → Subscription) using current commercial terms. Old subscriptions/contracts remain immutable. Old Active subscriptions are NEVER expired early by renewal. Contract and Subscription effective starts are temporally aligned.

## 30.2 Actual Commit SHA

```
3d133ef — Task 9.3.3: Fix Contract/Subscription temporal alignment in renewal handler
```

### Prior commits
```
9f43e07 — Task 9.3.2: fix(renewal): Renewal Lifecycle Correction
d0ca39b — Task 9.3: Subscription Renewal as New Commercial Transaction
5f67c68 — Task 9.3.1: Renewal Commercial Snapshot & Financial Chain Hardening
```

## 30.3 Files Changed

| File | Change |
|---|---|
| `src/Centerix.Domain/Platform/Contracts/Contract.cs` | Added `PreviousSubscriptionId` nullable field + `LinkToPreviousSubscription()` method |
| `src/Centerix.Domain/Platform/Subscriptions/TenantPlan.cs` | Added `AddCalendarMonths` static helper (removed `ExpireEarlyForRenewal` in 9.3.2) |
| `src/Centerix.Domain/Platform/Subscriptions/TenantPlanErrors.cs` | Added renewal-specific error codes |
| `src/Centerix.Application/Platform/Commands/RenewSubscriptionOfferCommand.cs` | Core renewal handler — **9.3.3: Fixed Contract effective start to align with Subscription start** |
| `src/Centerix.Application/Platform/Subscriptions/SubscriptionFactory.cs` | Added `activate` parameter to `CreateFromSnapshotAsync` |
| `src/Centerix.Infrastructure/Data/Configurations/ContractConfiguration.cs` | Added EF configuration for `PreviousSubscriptionId` |
| `src/Centerix.Infrastructure/Data/Migrations/20260915193613_AddContractPreviousSubscriptionId.cs` | Migration for PreviousSubscriptionId column |
| `src/Centerix.API/Controllers/TenantPlansController.cs` | Added `POST /api/tenantplans/{id}/renew-commercial` endpoint + request DTO |
| `tests/Centerix.SecurityTests/Phase9_3SubscriptionRenewalTests.cs` | 46 domain-level tests |
| `tests/Centerix.SecurityTests/Phase9_3_1RenewalHardeningTests.cs` | 40 domain tests + 4 SQL Server tests |
| `tests/Centerix.SecurityTests/Phase9_3_3ContractSubscriptionAlignmentTests.cs` | **NEW: 12 domain tests + 6 SQL Server tests for temporal alignment** |

## 30.4 Business Behavior (Task 9.3.3 Corrected)

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
New Contract starts at
New Subscription StartsAtUtc (IDENTICAL)
        │
        ▼
New Subscription Status:
  - Pending (if startsAt > now) — coexists with old Active via unique index
  - Active (if startsAt <= now) — old already expired
```

### Critical Business Rules (Task 9.3.3 Invariant)

1. **Contract/Subscription temporal alignment**: `NewContract.EffectiveAtUtc == NewSubscription.StartsAtUtc == startsAt`
2. **Old subscription is NEVER modified by renewal** — no early expiration, no cancellation
3. **Old subscription remains Active** until its natural `EffectiveEndsAtUtc`
4. **New subscription starts exactly at old's `EffectiveEndsAtUtc`** when old is still Active
5. **Renewal ≠ Cancellation** — renewal does not invoke refund or cancellation lifecycle
6. **Sequential entitlement** — old Active + new Pending = valid sequential arrangement
7. **Contract does not start during old service period**: `NewContract.EffectiveAtUtc >= OldSubscription.EffectiveEndsAtUtc`

## 30.5 Task 9.3.3 Defect Corrected

### Defect
In `RenewSubscriptionOfferHandler`, Step 9 created the new Contract using `effectiveAt = now` instead of `effectiveAt = startsAt`. This caused the Contract to start at request time while the Subscription correctly started at `oldSubscription.EffectiveEndsAtUtc` for scheduled renewals.

### Old Behavior (Defect)
```csharp
// Step 9 (BEFORE fix):
var effectiveAt = now;                    // BUG: uses current time
var endsAt = effectiveAt.AddMonths(durationMonths);
```

Result for scheduled renewal (old ends 2026-12-31, requested 2026-06-01):
```
Contract:   2026-06-01 → 2027-06-01  (WRONG: starts during old service)
Subscription: 2026-12-31 → 2027-12-31  (CORRECT)
```

### New Behavior (Corrected)
```csharp
// Step 9 (AFTER fix):
var effectiveAt = startsAt;              // CORRECT: aligned with subscription
var endsAt = startsAt.AddMonths(durationMonths);
```

Result for scheduled renewal:
```
Contract:     2026-12-31 → 2027-12-31  (ALIGNED)
Subscription: 2026-12-31 → 2027-12-31  (ALIGNED)
```

### Contract End Date Semantics (Preserved)
- `Contract.EndsAtUtc = startsAt + DurationMonths` (no bonus months)
- `Subscription.EffectiveEndsAtUtc = startsAt + DurationMonths + BonusMonths` (bonus included)
- This matches the existing `CreateContractFromOfferHandler` behavior where `endsAt = effectiveAt.AddMonths(offer.DurationMonths)`

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

### Task 9.3 Domain Tests (InMemory)
```
Total tests: 86
     Passed: 86
```

### Task 9.3.3 Domain Tests (Contract/Subscription Alignment)
```
Total tests: 12
     Passed: 12
```

Tests:
- Test01: Scheduled Renewal — Contract/Subscription Start Alignment
- Test02: Contract Does Not Start During Old Service Period
- Test03: Immediate Renewal — Contract/Subscription Start Alignment
- Test04: Old Subscription Remains Active After Scheduled Renewal
- Test05: Historical Contract Unchanged After Renewal
- Test06: Billing Cycle Alignment With Subscription
- Test07: Contract End Date Matches Contract Duration (No Bonus)
- Test08: Scheduled Renewal With Bonus Months — Contract vs Subscription End
- Test09: Contract Does Not Overlap Previous Contract
- Test10: Immediate Renewal — Expired Sub Aligns Contract and Sub
- Test11: Scheduled Renewal — Start Date Computation Preserves Invariant
- Test12: Contract EffectiveAt Never Before Old Subscription Ends

### SQL Server Integration Tests (Existing)
```
Total tests: 4
     Passed: 4
```

- `Renewal_HandlerCreates_BillingCycleAndInvoice` — PASS
- `Renewal_OldSubscription_RemainsActive_AfterRenewal` — PASS
- `SequentialDuplicateRenewal_Rejected` — PASS
- `Renewal_ConcurrentRequests_CannotBothSucceed` — PASS

### SQL Server Integration Tests (Task 9.3.3 Alignment)
```
Total tests: 6
     Passed: 6
```

- `Sql01_ScheduledRenewal_ContractEffectiveAt_EqualsSubscriptionStartsAt` — PASS
- `Sql02_ContractEffectiveAt_NotBeforeOldSubscriptionEnds` — PASS
- `Sql03_ImmediateRenewal_ContractAndSubscriptionAligned` — PASS
- `Sql04_OldSubscription_RemainsActive_AfterScheduledRenewal` — PASS
- `Sql05_BillingCycle_Invoice_AlignedWithSubscription` — PASS
- `Sql06_HistoricalContractUnchanged_AfterRenewal` — PASS

### Migration Tests
```
Total tests: 2
     Passed: 2
```

- `Migration_PreviousSubscriptionId_ColumnExists` — PASS
- `Migrations_NoPendingMigrations` — PASS

### Total Phase 9.3 Tests Executed
```
Total tests: 110
     Passed: 110
     Failed: 0
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
| Contract/Subscription alignment | **NEW 9.3.3**: Effective starts identical, no overlap | PASS |
| Billing chain alignment | BillingCycle/Invoice aligned with subscription period | PASS |
| SQL Server verification | Migration, billing chain, old sub preserved, concurrency, alignment | PASS |

## 30.8 Migration

The `PreviousSubscriptionId` is a nullable `Guid?` field on `Contract`.
Migration `20260915193613_AddContractPreviousSubscriptionId` adds the column.

## 30.9 Implementation Summary

### Architecture Decision
Renewal reuses the existing Offer → Contract → Subscription architecture. No parallel renewal system.

### Key Design Choices (Task 9.3.3)
1. **Contract/Subscription temporal alignment** — `NewContract.EffectiveAtUtc == NewSubscription.StartsAtUtc` for all renewal cases
2. **Contract end date = DurationMonths only** — bonus months are a Subscription concept; Contract uses `startsAt.AddMonths(durationMonths)`
3. **No early expiration** — old subscription lifecycle is never interrupted by renewal
4. **Temporal overlap guard** — based on service periods, not status flags
5. **Pending status for future starts** — new subscription is Pending when starting in the future, coexisting with old Active via filtered unique index
6. **Conditional activation** — `SubscriptionFactory.CreateFromSnapshotAsync(activate: bool)` controls whether new subscription is immediately Active
7. **SERIALIZABLE serialization** — concurrent renewals serialized at database level

### What Was NOT Changed
- Existing `RenewSubscriptionCommand` (legacy month-appending) preserved
- No new billing/invoice/payment logic
- No referral credit system
- No automatic payment collection
- No generic idempotency framework
- No cancellation/refund invoked by renewal
- No new domain entities introduced
- No Task 9.4 work started
