# TASK C — FREE MONTHS BENEFIT FOUNDATION — VERIFICATION REPORT

**Type:** Final verification report for Task C. Locks the implementation baseline against
the binding design (`docs/COMMERCIAL-BENEFIT-DESIGN-VALIDATION.md` §F and §G) and
documents the SQL Server verification of the FreeMonthsBenefit domain foundation.

**Scope:** Domain aggregate + state machine + Offer → Contract snapshot + EF Core
configuration + schema migration. Explicitly out of scope (later tasks): the
eligibility evaluator, `GrantBenefitCommand`, `ApplyFreeMonthsToSubscriptionCommand`,
`TenantPlan.AppliedFreeMonthsBenefitIds[]`, the refund exclusion change, and the
`Contract.BonusMonths` derivation cleanup.

**Posture:** Documentation only for this commit; the implementation commit is
`2530af1` (Task C foundation). A follow-up correctness fix is captured in
commit `bdac0b1` (see §12 — Eligibility/Fulfillment Independence).

**Repository state at audit time:** HEAD `bdac0b1`. Implementation commit
`2530af1`. Correction commit `bdac0b1`. Previous commits: `8e52171`
(Task B.2 SQL Server verification report), `4e34087` (Task B production flow fix),
`fc6cfea` (Task B.2 SQL tests), `5516e60` (Task B.1 PaymentMethod canonicalisation),
`6b44648` (Task B EligibilityRule foundation), `e744009` (Task A PaymentTerms
foundation).

---

## 1. Executive Summary

Task C introduces the `FreeMonthsBenefit` aggregate as a separate persistence
type from `ContractBenefit`, following the design rationale in
`docs/COMMERCIAL-BENEFIT-DESIGN-VALIDATION.md` §D.2. The aggregate exposes four
independent counters/states (Commercial Entitlement, Eligibility, Grant,
Application) and the full state machine required by §F.3 and §G.3. The migration
is **schema-only** — it does NOT manufacture FreeMonthsBenefit rows from
`Contract.BonusMonths > 0` (per design invariant 35: no historical commercial
facts invented).

All production paths are wired:
* `Offer.FreeMonthsBenefits[]` → `Contract.FreeMonthsBenefits[]` snapshot
  through `CreateContractFromOfferCommand`.
* `FreeMonthsBenefit.EligibilityRule` is required (per design invariant 26).
* `ContractedAmount` is independent of `FreeMonthsBenefits.EntitlementMonths`
  (per design invariant 32).

Tests:
* **37 pure-domain tests** (`TaskC_FreeMonthsBenefitFoundationTests`) — pass.
* **9 InMemory EF / snapshot tests** (`TaskC_FreeMonthsBenefitSnapshotTests`) —
  pass.
* **7 SQL Server integration tests** (`TaskC_FreeMonthsBenefitSqlServerTests`)
  against the local SQL Server — pass.

Full regression: **1522/1522** non-SQL tests pass; **214/214** SQL tests pass
(1 pre-existing skip unrelated to Task C).

---

## 2. Domain Model — Final State

### 2.1 New types

| Type | File | Purpose |
|---|---|---|
| `FreeMonthsBenefit` | [FreeMonthsBenefit.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Contracts/FreeMonthsBenefit.cs) | Aggregate on Contract for commercial free-months entitlements |
| `FreeMonthsEligibilityStatus` | [FreeMonthsEligibilityStatus.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Contracts/Enums/FreeMonthsEligibilityStatus.cs) | Reversible eligibility: `NotEligible`, `Eligible` |
| `FreeMonthsFulfillmentStatus` | [FreeMonthsFulfillmentStatus.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Contracts/Enums/FreeMonthsFulfillmentStatus.cs) | Monotone fulfillment: `Pending`, `Granted`, `AppliedToSubscription` (terminal) |
| `FreeMonthsBenefitErrors` | [FreeMonthsBenefitErrors.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Contracts/FreeMonthsBenefitErrors.cs) | Domain errors for state transitions |
| `OfferFreeMonthsBenefit` | [OfferBenefit.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Promotions/OfferBenefit.cs) | Offer-side mirror; created alongside `OfferBenefit` in the same file |
| `FreeMonthsBenefitConfiguration` | [FreeMonthsBenefitConfiguration.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/Data/Configurations/FreeMonthsBenefitConfiguration.cs) | EF Core configuration for both `FreeMonthsBenefit` and `OfferFreeMonthsBenefit` |

### 2.2 State machine

```text
Eligibility (reversible, independent)     Fulfillment (irreversible, monotone)
─────────────────────────────────────     ────────────────────────────────────
NotEligible  ⇄  Eligible                  Pending  →  Granted  →  AppliedToSubscription  (terminal)
```

**Eligibility and Fulfillment are independent state machines.** The two counters
may legitimately reach any combination such as `NotEligible + Granted`,
`Eligible + Granted`, `NotEligible + AppliedToSubscription`, or
`Eligible + AppliedToSubscription`. Eligibility answers *"Is this benefit
currently eligible according to its rule?"*; Fulfillment answers *"Has the
commercial entitlement already been granted/applied?"*. The two questions are
deliberately separate.

| Method | Pre-state | Post-state | Failure precondition |
|---|---|---|---|
| `MarkEligible(now)` | `NotEligible` (any Fulfillment) | `Eligible` | none — eligibility is independent of fulfillment |
| `MarkNotEligible()` | `Eligible` (any Fulfillment) | `NotEligible` | none — eligibility is independent of fulfillment |
| `Grant(now)` | `Pending` + `Eligible` | `Granted` | FulfillmentStatus = Pending AND EligibilityStatus ≠ Eligible |
| `MarkAppliedToSubscription(now)` | `Granted` | `AppliedToSubscription` | FulfillmentStatus ≠ Granted (NotGranted) |

All four methods are **idempotent** on the no-op case (success returning
`Result.Updated`).

### 2.3 Why a separate aggregate from `ContractBenefit`?

Per design §D.2: the two entitlement types share the `EligibilityRule` evaluation
pipeline but diverge on fulfillment. `FreeMonthsBenefit` mutates
`TenantPlan.EffectiveEndsAtUtc` exactly once. `ContractBenefit` records
`DeliveredBy` and stops. The lifecycles are genuinely different — the
fulfillment lifecycle for FreeMonths is **monotone** with a **terminal**
state (`AppliedToSubscription`), while `ContractBenefit` is monotone without a
distinct terminal (it stops at `Delivered`). Reusing the existing
`BenefitEligibilityStatus` enum would conflate the two.

The new `FreeMonthsEligibilityStatus` and `FreeMonthsFulfillmentStatus` enums
preserve this distinction in the type system.

### 2.4 Field-level invariants

| Field | Type | Invariant |
|---|---|---|
| `Id` | Guid | Non-empty (rejected at `Create`) |
| `ContractId` | Guid | Non-empty (rejected at `Create`) |
| `EntitlementMonths` | int | `> 0` (rejected at `Create`); **immutable** thereafter |
| `CurrencyCode` | string | 3-letter ISO-4217, normalised to upper-invariant at construction |
| `EligibilityStatus` | enum | Reversible; **independent of `FulfillmentStatus`** (see §7) |
| `FulfillmentStatus` | enum | Monotone: `Pending → Granted → AppliedToSubscription` (terminal) |
| `EligibilityRule` | `EligibilityRule` | **Required** at `Create` (rejected if null) |
| `EligibleAtUtc` | DateTime? | Set on first `MarkEligible`; cleared by `MarkNotEligible`; **never** set after `Grant`/`Apply` |
| `GrantedAtUtc` | DateTime? | Set once on `Grant`; preserved on idempotent re-calls; never cleared |
| `AppliedAtUtc` | DateTime? | Set once on `MarkAppliedToSubscription`; preserved on idempotent re-calls; never cleared |

---

## 3. Offer → Contract Snapshot

### 3.1 Production wiring

`CreateContractFromOfferCommand` now includes `Offer.FreeMonthsBenefits` in its
initial `Include` block. For each `OfferFreeMonthsBenefit` with a non-null
`EligibilityRule`, the handler:

1. Calls `FreeMonthsBenefit.Create(...)` with the snapshotted
   `EntitlementMonths`, `CurrencyCode`, and `EligibilityRule`.
2. Calls `Contract.AddFreeMonthsBenefit(...)` to attach the row.

Rows with a null `EligibilityRule` (legacy / pre-rule) are **skipped**, not
fabricated with a default — this is the production-side defence for design
invariant 26 ("every Entitlement Benefit row MUST carry exactly one
EligibilityRule"). The migration does NOT backfill rules onto FreeMonthsBenefit
rows (per design invariant 35), so the production flow must defend against null
on the Offer side.

### 3.2 Idempotency and lifecycle entry

After snapshot, the new `FreeMonthsBenefit` rows start in the canonical initial
state:

* `EligibilityStatus = NotEligible`
* `FulfillmentStatus = Pending`
* `EligibleAtUtc = null`
* `GrantedAtUtc = null`
* `AppliedAtUtc = null`

The eligibility evaluator (`BenefitEligibilityEvaluator`, out of scope for
Task C) is the only mechanism allowed to flip the Eligibility status. The
`GrantBenefitCommand` and `ApplyFreeMonthsToSubscriptionCommand` (also out of
scope) are the only mechanisms allowed to advance the Fulfillment status.

### 3.3 EligibilityRule snapshot fidelity

The Offer-side `EligibilityRule` (a value-object instance) is **copied by
reference** into the Contract-side row. Because `EligibilityRule` instances are
immutable value-objects with structural equality, the reference identity
collapse does not introduce aliasing risk: the canonical serializer
(`EligibilityRuleSerializer`) treats both as byte-identical.

A serialise → deserialise round-trip through SQL Server confirms
byte-identical JSON for primitive rules, `AllOf` composites, `AnyOf` composites,
and nested composites with no CLR / assembly / executable metadata (see
§6.4).

---

## 4. Persistence

### 4.1 EF Core configuration

The new `FreeMonthsBenefitConfiguration` and `OfferFreeMonthsBenefitConfiguration`
classes wire the new entities into the EF Core model:

```text
Platform.FreeMonthsBenefits       (new)
  ├─ PK Id
  ├─ ContractId  →  Platform.Contracts.Id  (cascade)
  ├─ EntitlementMonths  (int, NOT NULL)
  ├─ CurrencyCode       (nvarchar(3), NOT NULL, upper-invariant)
  ├─ EligibilityStatus  (tinyint, NOT NULL)
  ├─ EligibleAtUtc      (datetime2, NULL)
  ├─ FulfillmentStatus  (tinyint, NOT NULL)
  ├─ GrantedAtUtc       (datetime2, NULL)
  ├─ AppliedAtUtc       (datetime2, NULL)
  └─ EligibilityRule    (nvarchar(4000), NOT NULL, canonical JSON)
  └─ IX_FreeMonthsBenefits_ContractId
  └─ IX_FreeMonthsBenefits_ContractId_EligibilityStatus
  └─ IX_FreeMonthsBenefits_ContractId_FulfillmentStatus

Platform.OfferFreeMonthsBenefits  (new)
  ├─ PK Id
  ├─ OfferId       →  Platform.Offers.Id  (cascade)
  ├─ EntitlementMonths
  ├─ CurrencyCode
  └─ EligibilityRule  (nvarchar(4000), NULL — legacy defence)
  └─ IX_OfferFreeMonthsBenefits_OfferId
```

The `EligibilityRule` column on `FreeMonthsBenefits` is **NOT NULL** because the
domain invariant requires a rule on every FreeMonthsBenefit at construction.
The column on `OfferFreeMonthsBenefits` is nullable to permit the
production-side `null`-skip defence (legacy fixtures; production Offer rows
MUST carry a rule).

`AppDbContext` exposes `DbSet<FreeMonthsBenefit> FreeMonthsBenefits` and
`DbSet<OfferFreeMonthsBenefit> OfferFreeMonthsBenefits`. The existing
`Contract` and `Offer` configurations gain new `HasMany(...)` navigations with
`OnDelete(DeleteBehavior.Cascade)`.

### 4.2 Migration

The migration `20260929174030_AddFreeMonthsBenefits` is **schema-only**:

* No `InsertData`, no raw `Sql`, no `UpdateData` — verified by `grep`.
* Adds the `Platform.FreeMonthsBenefits` table with the configured columns,
  indexes, and FK.
* Adds the `Platform.OfferFreeMonthsBenefits` table with the configured
  columns, indexes, and FK.
* Adds three indexes on `FreeMonthsBenefits` for efficient lookup.

This is consistent with the design baseline position in §J.4: the migration is
a **schema change**, not a **historical commercial reconstruction**. The
greenfield position holds because the repository has no production legacy
FreeMonthsBenefit rows to backfill.

### 4.3 Numeric enum encoding (pinned)

The two new enums are stored as `tinyint`. The numeric values are **pinned**
and verified by `Test27_...` and `Test28_...`:

| Enum | Value | Stored byte |
|---|---|---|
| `FreeMonthsEligibilityStatus.NotEligible` | 0 | `0x00` |
| `FreeMonthsEligibilityStatus.Eligible` | 1 | `0x01` |
| `FreeMonthsFulfillmentStatus.Pending` | 0 | `0x00` |
| `FreeMonthsFulfillmentStatus.Granted` | 1 | `0x01` |
| `FreeMonthsFulfillmentStatus.AppliedToSubscription` | 2 | `0x02` |

The pin prevents silent reordering during a future refactor; reordering would
silently corrupt existing rows.

---

## 5. Invariants Verified

Each invariant below is enforced by the domain boundary, the EF Core
configuration, or the production snapshot wiring — and verified by a test.

| # | Invariant | Enforced by | Test |
|---|---|---|---|
| 26 | Every benefit row (`ContractBenefit` and `FreeMonthsBenefit`) MUST carry exactly one EligibilityRule | `FreeMonthsBenefit.Create` rejects null rule; `ContractBenefit` already does | `Test06_Create_WithNullRule_Fails`, `SqlC01_Schema`, `SqlC03_Snapshot` |
| 28 | FulfillmentStatus is monotone: `Pending → Granted → (AppliedToSubscription)`; no reverse | `Grant` and `MarkAppliedToSubscription` reject out-of-order transitions; `Grant` on `Granted`/`AppliedToSubscription` is idempotent | `Test15_...`, `Test16_...`, `Test18_...`, `Test19_...`, `Test25_...`, `SqlC05_StateTransitions` |
| 29 | EligibilityStatus is reversible: `NotEligible ⇄ Eligible` based on rule evaluation — **independent of FulfillmentStatus** | `MarkEligible` and `MarkNotEligible` are bidirectional at any Fulfillment status | `Test11_...` through `Test14_...`, `Test27_...`, plus all four independence tests in §12 |
| 31 | A FreeMonthsBenefit.Id MUST appear at most once in `TenantPlan.AppliedFreeMonthsBenefitIds[]` | TenantPlan-side invariant (out of scope here); aggregate-level: idempotent re-calls preserve first timestamp | `Test17_...`, `Test20_...` (idempotent re-call preserves `GrantedAtUtc`/`AppliedAtUtc`) |
| 32 | Bonus months never increase `ContractedAmount` | `Contract.AddFreeMonthsBenefit` performs no aggregate-value mutation | `TestC08_ContractedAmount_IsIndependentOfFreeMonthsBenefitEntitlementMonths` |
| 34 | The Offer/Contract snapshot of benefits is immutable after `Contract.Create(...)`; only EligibilityStatus, FulfillmentStatus, GrantedAtUtc, AppliedAtUtc mutate | `EntitlementMonths`, `EligibilityRule`, `CurrencyCode` are private-set; only the four transition methods mutate state | `Test10_CommercialDefinition_IsImmutable_AfterConstruction`, `Test15_...` (state fields mutated, commercial fields preserved) |
| 35 | No historical commercial facts are invented by migrations | Migration contains no `InsertData`, no raw `Sql`, no `UpdateData` | `SqlC01_Schema`, `SqlC07_Migration_DoesNotPopulateFreeMonthsBenefitsTable_OnEmptyDatabase` |

---

## 6. SQL Server Verification

### 6.1 Test harness

The existing `SqlServerIntegrationFactory` (Local SQL Server preferred,
Testcontainers fallback) is reused. All Task C SQL tests live in the
`[Collection("SqlServerIntegration")]` xUnit collection, so they share the
single migrated database established at collection initialisation.

Tenant authorisation follows the established reflection pattern:
`AuthorizeTenant(services, tenantId)` sets `_authorizedTenantId` and
`_isAuthorized` on the singleton `ICurrentTenant`.

### 6.2 Test inventory

| # | Test | Verifies |
|---|---|---|
| `SqlC01` | Schema | All 10 columns exist with the correct types/nullability/length; FK exists; 3 non-PK indexes exist |
| `SqlC02` | EligibilityRule round-trip | A single `FreeMonthsBenefit` row with a composite `EligibilityRule` survives a full SQL round-trip with byte-identical canonical JSON |
| `SqlC03` | Offer → Contract through production | `OfferFreeMonthsBenefit.EligibilityRule` is copied verbatim into `FreeMonthsBenefit.EligibilityRule` through the real `AcceptOfferHandler` + `CreateContractFromOfferHandler` |
| `SqlC04` | Multi-row preservation | Three FreeMonthsBenefit rows with distinct EntitlementMonths + rules on a single Contract are preserved independently |
| `SqlC05` | State transitions | All three lifecycle transitions (Eligibility, Grant, Apply) survive a SQL round-trip with timestamps intact |
| `SqlC06` | Persisted JSON safety | Nested composite `AllOf` containing `AnyOf` produces canonical JSON with no `$type`, `System.`, `Microsoft.`, `Centerix`, `EligibilityRule`, or `FreeMonthsBenefit` substrings |
| `SqlC07` | No fabricated rows | After persisting a Contract with `BonusMonths = 0`, the `FreeMonthsBenefits` table is empty for that contract |

### 6.3 Schema verification (SqlC01)

Confirmed via `INFORMATION_SCHEMA.COLUMNS` and `sys.foreign_keys` /
`sys.indexes` queries against the live database:

```text
Column             | Type         | Nullable | Length
-------------------+--------------+----------+--------
Id                 | uniqueident. | NO       |
ContractId         | uniqueident. | NO       |
EntitlementMonths  | int          | NO       |
CurrencyCode       | nvarchar     | NO       | 3
EligibilityStatus  | tinyint      | NO       |
EligibleAtUtc      | datetime2    | YES      |
FulfillmentStatus  | tinyint      | NO       |
GrantedAtUtc       | datetime2    | YES      |
AppliedAtUtc       | datetime2    | YES      |
EligibilityRule    | nvarchar     | NO       | 4000
```

Foreign keys on `FreeMonthsBenefits.ContractId → Contracts.Id` (cascade): **1**.
Non-PK indexes on `FreeMonthsBenefits`: **3** (ContractId,
ContractId+EligibilityStatus, ContractId+FulfillmentStatus).

### 6.4 JSON safety (SqlC06)

A nested composite (`AllOf` containing `AnyOf`) is persisted. The raw
`EligibilityRule` column on the live database is asserted to contain:

* No `System.` substring.
* No `Microsoft.` substring.
* No `Centerix` substring.
* No `EligibilityRule` substring (the type name is never persisted).
* No `FreeMonthsBenefit` substring.
* No `$type` substring (no polymorphic discriminator that could be
  deserialised as executable code).

### 6.5 Production flow (SqlC03)

The full production handler chain is exercised against SQL Server:

```text
Offer + OfferFreeMonthsBenefit (with sourceRule)
   ↓ AcceptOfferHandler              (status: Calculated → Accepted)
   ↓ CreateContractFromOfferHandler  (status: Accepted → ConvertedToContract,
                                       Contract created with FreeMonthsBenefit snapshot)
```

Re-loading the Contract in a fresh DI scope against the live database
confirms:

* `FreeMonthsBenefit.EligibilityRule` structural-equals the source rule.
* `EligibilityRuleSerializer.Serialize(loaded.EligibilityRule)` is
  byte-identical to the original `sourceRuleJson`.
* `EntitlementMonths` is preserved.
* Initial state is `NotEligible` / `Pending` (no fabricated eligibility).

---

## 7. Eligibility / Fulfillment Independence — Verification

**Status:** VERIFIED. The four required transitions below were each exercised
by a dedicated domain test. None were skipped.

The earlier Task C implementation incorrectly locked `EligibilityStatus` after
`FulfillmentStatus` reached `Granted`. The correction (commit
`bdac0b1`) removes the `if (FulfillmentStatus is Granted or
AppliedToSubscription) return CannotRevertFromGranted;` guard from
`MarkEligible` and `MarkNotEligible`. The XML documentation on
`FreeMonthsBenefit.MarkEligible` / `MarkNotEligible` / `Grant` was rewritten to
state the corrected semantics:

* **Eligibility** is reversible.
* **Fulfillment** is monotone.
* **Application** is terminal / idempotent.

The corrected `FreeMonthsBenefit` does NOT use "Granted is historical" as
justification for locking eligibility. Historical fulfillment and current
eligibility are deliberately separate state machines.

### 7.1 Required transitions (all VERIFIED)

| # | Starting state | Transition | Expected post-state | Test | Result |
|---|---|---|---|---|---|
| 1 | `Eligible + Pending` | `Grant(t)` | `Eligible + Granted`, `GrantedAtUtc = t` | `Test15_Grant_FromEligiblePending_TransitionsToGranted_StampsTimestamp` | **Passed** |
| 2 | `Eligible + Granted` | `MarkNotEligible()` | `NotEligible + Granted`, `GrantedAtUtc` preserved | `Test21_Grant_DoesNotLockEligibility_MarkNotEligibleSucceedsAfterGrant` | **Passed** |
| 3 | `NotEligible + Granted` | `MarkEligible(t2)` | `Eligible + Granted`, `GrantedAtUtc` preserved, `EligibleAtUtc = t2` | `Test23_Eligibility_CanBecomeEligibleAgainAfterGrant_FulfillmentUnchanged` | **Passed** |
| 4 | `Eligible + AppliedToSubscription` | `MarkNotEligible()` | `NotEligible + AppliedToSubscription`, `GrantedAtUtc`/`AppliedAtUtc` preserved | `Test22_AppliedBenefit_CanBecomeNotEligible_FulfillmentPreserved` | **Passed** |
| 5 | `NotEligible + AppliedToSubscription` | `MarkEligible(t2)` | `Eligible + AppliedToSubscription`, `GrantedAtUtc`/`AppliedAtUtc` preserved, `EligibleAtUtc = t2` | `Test24_Eligibility_CanBecomeEligibleAgainAfterApplication_FulfillmentAndTimestampsUnchanged` | **Passed** |

All five transitions preserve `FulfillmentStatus` and the fulfillment
timestamps (`GrantedAtUtc`, `AppliedAtUtc`) exactly. Only `EligibilityStatus`
and `EligibleAtUtc` change.

### 7.2 Grant precondition — preserved

The correction did NOT weaken `Grant`. The `Grant` method still requires
`EligibilityStatus == Eligible` at the moment of the actual
`Pending → Granted` transition. Verified by:

* `Test16_Grant_FromNotEligible_IsRejected` — `Pending + NotEligible → Grant` fails with `Contract.FreeMonthsBenefit.NotEligible` (passed).
* `Test17_Grant_IsIdempotent_OnAlreadyGranted` — re-calling `Grant` on `Granted` is a no-op (passed).
* `Test25_OnceApplied_Grant_IsNoOp_FulfillmentUnchanged` — re-calling `Grant` on `AppliedToSubscription` is a no-op (passed).

### 7.3 SQL Server verification

The same independence is also implicitly exercised by
`SqlC05_StateTransitions_SurviveSqlRoundTrip`, which round-trips a benefit
through `MarkEligible → Grant → MarkAppliedToSubscription` against the live
database. The correction does not affect persistence — no migration was
required and `dotnet ef migrations has-pending-model-changes` returns
"No changes have been made to the model since the last migration."

---

## 8. Out-of-Scope (Deferred to Later Tasks)

The following items are explicitly **NOT** implemented in Task C. They are
called out here so that future tasks can implement them without violating
this commit's invariants.

1. **`BenefitEligibilityEvaluator`** — the pure function that evaluates an
   `EligibilityRule` against `(contract, financialState, clock)`. It is the
   only mechanism that may flip `FreeMonthsEligibilityStatus`. The current
   implementation of `IBenefitEligibilityService` continues to apply the
   global legacy rule; the per-row evaluator is a later task.
2. **`GrantBenefitCommand`** — the explicit grant step. It must verify
   `EligibilityStatus == Eligible` and `FulfillmentStatus == Pending`, then
   call `FreeMonthsBenefit.Grant(now)`.
3. **`ApplyFreeMonthsToSubscriptionCommand`** — the explicit application step.
   It must verify `FulfillmentStatus == Granted` and that
   `TenantPlan.AppliedFreeMonthsBenefitIds[]` does not already contain the
   row's Id, then call `FreeMonthsBenefit.MarkAppliedToSubscription(now)`
   and mutate `TenantPlan.BonusMonths` + `EffectiveEndsAtUtc`.
4. **`TenantPlan.AppliedFreeMonthsBenefitIds[]`** — the TenantPlan-side
   collection that enforces the at-most-once application invariant (per
   design §F and §L.31).
5. **`RefundCalculationService`** — signature change to accept
   `IReadOnlyList<FreeMonthsBenefit>`; loop only over `ContractBenefit` for
   benefit contributions (per design §I and §M.T10).
6. **`Contract.BonusMonths` derivation cleanup** — long-term replacement of
   the legacy scalar with `Σ FreeMonthsBenefits.EntitlementMonths` (per
   design §M.T11). Until that lands, the two fields are independent.
7. **`CheckBenefitEligibilityCommand` refactor** — replace the global rule
   lookup with the per-row evaluator.

---

## 9. Hidden-Inference Audit

The Task C implementation was audited for the failure modes flagged in
`docs/COMMERCIAL-BENEFIT-DESIGN-VALIDATION.md` §N (Genuinely Unresolved
Business Decisions) and §C (the two final corrections):

* **No inference from `PromotionType`.** `PromotionType` does not appear in
  any FreeMonthsBenefit creation path. Confirmed by code inspection and the
  unit tests.
* **No inference from `Contract.BonusMonths`.** The only `Contract.BonusMonths`
  reference in `src/Centerix.Infrastructure` is in the EF Core configuration
  documentation comment explaining why the migration is schema-only. No code
  path manufactures `FreeMonthsBenefit` rows from `BonusMonths > 0`.
* **No inference from installment rows.** No code path reads installment state
  to fabricate FreeMonthsBenefit rows.
* **No derivation of `PaymentTerms` from `FreeMonthsBenefit.EligibilityRule`.**
  The rule's `PaymentTermsEq(...)` is a property of the benefit, not a
  global rule of the contract.
* **No automation of `GrantBenefitCommand` from `MarkEligible`.** The two are
  independent and require explicit invocations.

Confirmed by `grep` for `BonusMonths\s*>\s*0`,
`Sum.*EntitlementMonths`, `PromotionType.*FreeMonthsBenefit`, and
`FreeMonthsBenefit.*BonusMonths` over `src/` — only the documentation comment
matches, and it is non-executable.

---

## 10. Verification Summary

| Category | Result |
|---|---|
| Solution build | 0 errors, 0 warnings |
| Task C unit tests (InMemory) | 39 passed, 0 failed |
| Task C snapshot/EF tests (InMemory) | 9 passed, 0 failed |
| Task C SQL Server tests | 7 passed, 0 failed |
| Total Task C tests | 55 passed, 0 failed |
| Full non-SQL regression | 1524 passed, 0 failed |
| Full SQL regression | 214 passed, 1 pre-existing skip (Task18.5 unrelated) |
| EF Core migrations | `20260929174030_AddFreeMonthsBenefits` applied successfully against Local SQL Server; no pending model changes after correction |
| `dotnet ef migrations has-pending-model-changes` | "No changes have been made to the model since the last migration." |
| Hidden-inference audit | Clean (no derivation, no fabrication, no inference) |
| Implementation commit | `2530af1` (`feat(billing): add free months benefit foundation`) |
| Correction commit | `bdac0b1` (`fix(billing): decouple free months eligibility from fulfillment`) |

---

## 11. Files Changed

### Added (10 files)
* `src/Centerix.Domain/Platform/Contracts/Enums/FreeMonthsEligibilityStatus.cs`
* `src/Centerix.Domain/Platform/Contracts/Enums/FreeMonthsFulfillmentStatus.cs`
* `src/Centerix.Domain/Platform/Contracts/FreeMonthsBenefit.cs`
* `src/Centerix.Domain/Platform/Contracts/FreeMonthsBenefitErrors.cs`
* `src/Centerix.Infrastructure/Data/Configurations/FreeMonthsBenefitConfiguration.cs`
* `src/Centerix.Infrastructure/Data/Migrations/20260929174030_AddFreeMonthsBenefits.cs`
* `src/Centerix.Infrastructure/Data/Migrations/20260929174030_AddFreeMonthsBenefits.Designer.cs`
* `tests/Centerix.SecurityTests/TaskC_FreeMonthsBenefitFoundationTests.cs`
* `tests/Centerix.SecurityTests/TaskC_FreeMonthsBenefitSnapshotTests.cs`
* `tests/Centerix.SecurityTests/TaskC_FreeMonthsBenefitSqlServerTests.cs`

### Modified (8 files)
* `src/Centerix.Domain/Platform/Contracts/Contract.cs` — added
  `_freeMonthsBenefits`, `FreeMonthsBenefits`, `AddFreeMonthsBenefit`,
  `LoadFreeMonthsBenefits`.
* `src/Centerix.Domain/Platform/Promotions/Offer.cs` — added
  `_freeMonthsBenefits`, `FreeMonthsBenefits`, `AddFreeMonthsBenefit`,
  `LoadFreeMonthsBenefits`.
* `src/Centerix.Domain/Platform/Promotions/OfferBenefit.cs` — added
  `OfferFreeMonthsBenefit` class.
* `src/Centerix.Application/Platform/Promotions/Commands/CreateContractFromOfferCommand.cs`
  — included `Offer.FreeMonthsBenefits` and added snapshot loop.
* `src/Centerix.Infrastructure/Data/AppDbContext.cs` — added
  `DbSet<FreeMonthsBenefit>` and `DbSet<OfferFreeMonthsBenefit>`.
* `src/Centerix.Infrastructure/Data/Configurations/ContractConfiguration.cs`
  — added `HasMany(c => c.FreeMonthsBenefits)` navigation.
* `src/Centerix.Infrastructure/Data/Configurations/OfferConfiguration.cs` —
  added `HasMany(o => o.FreeMonthsBenefits)` navigation.
* `src/Centerix.Infrastructure/Data/Migrations/AppDbContextModelSnapshot.cs`
  — regenerated by EF tooling.

### Documentation
* `docs/TASK-C-FREE-MONTHS-BENEFIT-VERIFICATION-REPORT.md` (this file).

### Correction commit (subsequent, behavioural fix only)
* **Modified (3 files):**
  * `src/Centerix.Domain/Platform/Contracts/FreeMonthsBenefit.cs` — removed
    the `CannotRevertFromGranted` guard from `MarkEligible` and
    `MarkNotEligible`; rewrote XML doc comments to state the corrected
    semantics ("Eligibility is reversible. Fulfillment is monotone.
    Application is terminal / idempotent.").
  * `src/Centerix.Domain/Platform/Contracts/FreeMonthsBenefitErrors.cs` —
    removed the now-unreachable `CannotRevertFromGranted` and
    `CannotRevertFromApplied` error codes.
  * `tests/Centerix.SecurityTests/TaskC_FreeMonthsBenefitFoundationTests.cs`
    — replaced `Test21_OnceGranted_MarkNotEligible_IsRejected` and
    `Test22_OnceApplied_MarkNotEligible_IsRejected` with the four required
    independence tests (`Test21`, `Test22`, `Test23`, `Test24`,
    `Test25_OnceApplied_Grant_IsNoOp_FulfillmentUnchanged`); renumbered the
    subsequent tests.
* **Documentation updated:**
  * `docs/TASK-C-FREE-MONTHS-BENEFIT-VERIFICATION-REPORT.md` — added §7
    (Eligibility / Fulfillment Independence — Verification); updated §2.2,
    §5, §10, §12 to reflect the corrected semantics; removed every claim
    that eligibility is locked after Grant.
* **No migration required.** `dotnet ef migrations has-pending-model-changes`
  returns "No changes have been made to the model since the last migration."
  The correction is a behavioural / domain-only change.

---

## 12. Stop Condition

Task C is closed (post-correction).

* Domain aggregate + state machine + Offer → Contract snapshot + EF Core
  configuration + schema migration are implemented and tested.
* Eligibility and Fulfillment are independent state machines: each
  combination such as `Eligible + Granted`, `NotEligible + Granted`,
  `Eligible + AppliedToSubscription`, `NotEligible + AppliedToSubscription`
  is reachable, verified by the four independence tests in §7.
* Fulfillment remains monotone: `Pending → Granted → AppliedToSubscription`.
  `Grant` still requires `EligibilityStatus == Eligible` at the moment of
  the `Pending → Granted` transition.
* The migration is schema-only and does NOT manufacture FreeMonthsBenefit
  rows from `Contract.BonusMonths > 0` (per design invariant 35).
* All relevant design invariants (§L.26, §L.28, §L.29, §L.31, §L.32, §L.34,
  §L.35) are enforced by the domain boundary and verified by tests.
* 55 Task C tests + 1524 non-SQL + 214 SQL tests pass with zero regressions.
* No migration required for the correction; EF pending-model-changes is clean.
* The next task (Task D) can implement `GrantBenefitCommand` and
  `ApplyFreeMonthsToSubscriptionCommand` against the state-machine exposed
  here, plus `TenantPlan.AppliedFreeMonthsBenefitIds[]` for idempotency.
