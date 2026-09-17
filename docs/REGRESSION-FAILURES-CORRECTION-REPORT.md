# REGRESSION-FAILURES-CORRECTION-REPORT

## 1. Failures Investigated

| # | Test | File |
|---|------|------|
| 1 | `Phase3AuthorizationHttpTests.Students_CrossTenantBranch_IsInvisibleAndReturnsNotFound` | `tests/Centerix.SecurityTests/Phase3AuthorizationHttpTests.cs` |
| 2 | `Phase3AuthorizationHttpTests.Students_TenantAdmin_CanCreateReadUpdateSoftDelete` | `tests/Centerix.SecurityTests/Phase3AuthorizationHttpTests.cs` |
| 3 | `Phase9FinancialConcurrencySqlServerTests.Concurrent_PaymentAllocations_CannotExceedPaymentAmount` | `tests/Centerix.SecurityTests/Phase9FinancialConcurrencySqlServerTests.cs` |

---

## 2. Root Cause

### Test 1: `Students_CrossTenantBranch_IsInvisibleAndReturnsNotFound`

**Root Cause:** Shared InMemory database state contamination between test methods within the same class.

**Evidence:**

- **Class:** `Phase3AuthorizationHttpTests`
- **Fixture:** `IClassFixture<TestWebApplicationFactory>` — all test methods in the class share a single factory instance and its InMemory database.
- **Line 667:** `db.Branches.IgnoreQueryFilters().Single(b => b.Name == "A-Branch")`
- **Line 1206:** Same pattern in `Students_CrossTenantUpdateBranch_IsRejected`.

The query uses `IgnoreQueryFilters()` (bypassing Finbuckle's tenant global filter) but does NOT scope by `TenantId`. When prior test methods in the same class have already seeded branches into the shared InMemory database, `.Single()` finds multiple matching rows and throws `InvalidOperationException: Sequence contains more than one element`.

The test passes when run individually (fresh database) but fails when run as part of the full class.

### Test 2: `Students_TenantAdmin_CanCreateReadUpdateSoftDelete`

**Root Cause:** Same shared InMemory database contamination.

**Evidence:**

- **Line 628:** `db.Students.IgnoreQueryFilters().SingleAsync()` — expects exactly one student across ALL tenants.
- When prior test methods (e.g., `Students_FeatureMissing_PermissionPresent_IsDenied`, `Students_LimitExhausted_IsDeniedEvenWithFeatureAndPermission`, `Students_CrossTenantUpdateBranch_IsRejected`) create students, the `IgnoreQueryFilters().SingleAsync()` query finds multiple records.

Same pattern as Test 1: passes individually, fails when run with the full class.

### Test 3: `Concurrent_PaymentAllocations_CannotExceedPaymentAmount`

**Root Cause:** The handler's idempotency check (placed BEFORE capacity validation) causes both concurrent identical allocations to succeed, so the test never observes the expected 1-success-1-failure outcome.

**Evidence:**

- **Handler:** `src/Centerix.Application/Platform/Billing/Commands/AllocatePaymentCommand.cs:173-198`
- The idempotency check at line 173 matches on `(PaymentId, InvoiceId, InstallmentId, AllocatedAmount, Active, TenantId)`.
- When two concurrent requests allocate 7,000 each against a 10,000 payment:
  1. Transaction A succeeds, commits allocation of 7,000.
  2. Transaction B retries (after Serializable isolation conflict), sees Transaction A's allocation.
  3. The idempotency check matches (same PaymentId + InvoiceId + null InstallmentId + 7000 + Active + TenantId).
  4. Transaction B returns `Result.Updated` (success) without creating a duplicate.
  5. Both operations succeed. `successCount == 2`, `failCount == 0`.
- **Test line 464:** `if (successCount == 1 && failCount == 1)` — never true.
- **Test line 498:** `Assert.True(iterationsWithExpectedOutcome >= 1)` — fails because `iterationsWithExpectedOutcome == 0`.

The financial invariant IS preserved: total allocated = 7,000 ≤ 10,000. The handler's concurrency control (Serializable isolation, deadlock retry, ChangeTracker.Clear, idempotency) is correct. The test's assertion incorrectly assumes both concurrent identical requests cannot both succeed.

---

## 3. Correction

### Phase 3 — Scoped `.Single()` calls by `TenantId`

**File:** `tests/Centerix.SecurityTests/Phase3AuthorizationHttpTests.cs`

**Change 1 (line 628):**
```diff
- var soft = await db.Students.IgnoreQueryFilters().SingleAsync();
+ var soft = await db.Students.IgnoreQueryFilters().SingleAsync(x => x.TenantId == s.TenantId.ToString());
```

**Change 2 (line 667):**
```diff
- branchAId = db.Branches.IgnoreQueryFilters().Single(b => b.Name == "A-Branch").Id;
+ branchAId = db.Branches.IgnoreQueryFilters().Single(b => b.Name == "A-Branch" && b.TenantId == sA.TenantId.ToString()).Id;
```

**Change 3 (line 1206):** Same pattern as Change 2 in `Students_CrossTenantUpdateBranch_IsRejected`.

**Rationale:** When `IgnoreQueryFilters()` bypasses the tenant global query filter, the `.Single()` call must explicitly scope by `TenantId` to isolate to the test's seeded tenant. This is semantically correct — we are querying for records belonging to a specific tenant, and the `TenantId` filter preserves the uniqueness invariant.

### Phase 9 — Capacity test now uses distinct allocation identities

**File:** `tests/Centerix.SecurityTests/Phase9FinancialConcurrencySqlServerTests.cs`

**Previous (insufficient) correction:** Changed assertion to `successCount >= 1` to accept idempotent outcomes. This was insufficient because the test should prove the **capacity** invariant, not idempotency (which is already covered by `Concurrent_IdenticalRetry_DoesNotCreateDuplicateFinancialEffects`).

**Final correction:** Create two separate invoices (`invoiceA`, `invoiceB`) per iteration so each allocation targets a different `InvoiceId`. Since the handler's idempotency key is `(PaymentId, InvoiceId, InstallmentId, AllocatedAmount)`, different `InvoiceId` values make the allocations **distinct** — the second cannot be treated as an idempotent retry. The capacity guard is the only mechanism preventing over-allocation.

**Key change in test setup:**
```diff
- Guid invoiceId;
+ Guid invoiceAId;
+ Guid invoiceBId;
  ...
- var invoice = CreateInvoice(db, tenantId, 10000m, $"INV-{tenantId}-{i}");
- invoiceId = invoice.Id;
+ var invoiceA = CreateInvoice(db, tenantId, 10000m, $"INV-A-{tenantId}-{i}");
+ invoiceAId = invoiceA.Id;
+ var invoiceB = CreateInvoice(db, tenantId, 10000m, $"INV-B-{tenantId}-{i}");
+ invoiceBId = invoiceB.Id;
```

**Key change in concurrent operations:**
```diff
- new AllocatePaymentCommand(paymentId, invoiceId, 7000m)
+ new AllocatePaymentCommand(paymentId, invoiceAId, 7000m)
  ...
- new AllocatePaymentCommand(paymentId, invoiceId, 7000m)
+ new AllocatePaymentCommand(paymentId, invoiceBId, 7000m)
```

**Assertion restored to strict:**
```diff
- if (successCount >= 1)
+ if (successCount == 1 && failCount == 1)
```

**Rationale:** Two distinct allocations (different `InvoiceId`) against the same payment cannot be treated as idempotent. The handler's capacity check (`currentAllocated + request.AllocatedAmount > payment.Amount`) must prevent over-allocation. Under Serializable isolation, one transaction commits (7,000 allocated) and the other either gets a serialization conflict (retry → capacity check fails) or deadlock (retry → capacity check fails). The test now proves: **two legitimate concurrent allocations that together exceed capacity cannot both succeed**.

**No production logic was changed.** The handler (`AllocatePaymentHandler`) was not modified. The correction is in the test data setup (two invoices instead of one).

---

## 4. Tests

### Phase 3

| Test | Individual Result |
|------|-------------------|
| `Students_TenantAdmin_CanCreateReadUpdateSoftDelete` | **Passed** (5s) |
| `Students_CrossTenantBranch_IsInvisibleAndReturnsNotFound` | **Passed** (5s) |

| Suite | Result |
|-------|--------|
| `Phase3AuthorizationHttpTests` (18 tests) | **18/18 Passed** (10s) |

### Phase 9

| Test | Run 1 | Run 2 |
|------|-------|-------|
| `Concurrent_PaymentAllocations_CannotExceedPaymentAmount` (capacity) | **Passed** (16s) | **Passed** (12s) |
| `Concurrent_IdenticalRetry_DoesNotCreateDuplicateFinancialEffects` (idempotency) | **Passed** (3s) | — |

| Suite | Result |
|-------|--------|
| `Phase9FinancialConcurrencySqlServerTests` (16 tests) | **16/16 Passed** (24s) |

### Full Regression Suite

| Metric | Result |
|--------|--------|
| Passed | **1128** |
| Failed | **0** |
| Skipped | **0** |
| Total | **1128** |
| Duration | 3m 43s |

---

## 5. Regression

All of the following test categories remain green:

- Tenant isolation (`C1CrossTenantIsolationTests`)
- Cross-tenant authorization (`Phase2AuthorizationHttpTests`)
- Student CRUD (`Phase3AuthorizationHttpTests` — 18/18)
- Payment allocation (`Phase9FinancialConcurrencySqlServerTests`)
- Invoice balance (`Phase8_1_1FinancialIntegrityTests`)
- Installment settlement (`Phase8_1_3InstallmentRehydrationTests`)
- Refund (`Phase9FinancialLedgerHardeningTests`)
- Credit application (`Phase10_1CreditApplicationCorrectionTests`)
- Subscription lifecycle (`Phase9_3_3ContractSubscriptionAlignmentTests`)
- Renewal (`Phase9_3_1RenewalHardeningTests`)
- Cancellation (`Phase9_4_2CancellationConcurrencySqlServerTests`)
- Expiration (`Phase9_5_1NaturalExpirationConcurrencySqlServerTests`)

No regressions were introduced. The corrections are limited to:
1. Two scoped `.Single()` queries in test assertions (Phase 3)
2. Capacity test uses distinct InvoiceIds to bypass idempotency and prove the capacity invariant (Phase 9)
3. Idempotency test remains strict and separate

**Idempotency and capacity are separate concurrency concerns.** The idempotency test (`Concurrent_IdenticalRetry_DoesNotCreateDuplicateFinancialEffects`) proves that identical retries do not create duplicates. The capacity test (`Concurrent_PaymentAllocations_CannotExceedPaymentAmount`) proves that two distinct allocations that together exceed payment capacity cannot both succeed.

---

## 6. Commit SHA

```
f9c331c fix: restore strict capacity test with distinct allocation identities
```
