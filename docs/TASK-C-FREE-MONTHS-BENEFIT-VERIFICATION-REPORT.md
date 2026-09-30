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
`2530af1` (Task C foundation). Two follow-up corrections follow it:
`bdac0b1` (§7 — Eligibility/Fulfillment Independence) and
`fix(billing): enforce free months eligibility rule snapshot`
(§13 — EligibilityRule Completeness).

**Repository state at audit time:** implementation commit `2530af1`,
Correction 1 commit `bdac0b1`, Correction 2 commit
`c0055e1` (`fix(billing): enforce free months eligibility rule snapshot`).
HEAD: `c0055e1`.
Previous commits: `f8c7ae1` (record Correction 1 SHA), `8e52171`
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
* `EligibilityRule` is required on **both** `FreeMonthsBenefit` and
  `OfferFreeMonthsBenefit` (per design invariant 26) — enforced in the domain
  factory, in the EF model, and in the database schema.
* A missing rule in the Offer snapshot fails the conversion explicitly; it is
  never silently dropped.
* `ContractedAmount` is independent of `FreeMonthsBenefits.EntitlementMonths`
  (per design invariant 32).

Tests:
* **41 pure-domain tests** (`TaskC_FreeMonthsBenefitFoundationTests`) — pass.
* **10 InMemory EF / snapshot tests** (`TaskC_FreeMonthsBenefitSnapshotTests`) —
  pass.
* **9 SQL Server integration tests** (`TaskC_FreeMonthsBenefitSqlServerTests`)
  against the local SQL Server — pass.

**60 Task C tests**, all passing. Full regression: **1744 total, 1743 passed,
0 failed, 1 skipped** (1 pre-existing skip unrelated to Task C — see §10).

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

`CreateContractFromOfferCommand` includes `Offer.FreeMonthsBenefits` in its
initial `Include` block. For each `OfferFreeMonthsBenefit`, the handler:

1. Validates that the row carries an `EligibilityRule`. A null rule returns
   `Error.Validation("Offer.IncompleteFreeMonthsBenefit", ...)` and aborts the
   whole conversion. The row is **never skipped** — see §13.
2. Calls `FreeMonthsBenefit.Create(...)` with the snapshotted
   `EntitlementMonths`, `CurrencyCode`, and `EligibilityRule`.
3. Calls `Contract.AddFreeMonthsBenefit(...)` to attach the row.

The `OfferFreeMonthsBenefit.EligibilityRule` is **required** at every layer
(domain factory, EF model, and database schema), so a commercial entitlement
can no longer exist without a rule, and a corrupt row can no longer be silently
discarded. The migration does NOT backfill rules onto existing rows (per design
invariant 35); the tightening migration
`20260929193144_RequireEligibilityRuleOnOfferFreeMonths` alters the column to
`NOT NULL` with **no fabricated default**, so a pre-existing null row would fail
loudly at conversion time rather than disappear.

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
  └─ EligibilityRule  (nvarchar(4000), NOT NULL, canonical JSON)
  └─ IX_OfferFreeMonthsBenefits_OfferId
```

The `EligibilityRule` column is **NOT NULL on both tables**. The domain
invariant requires a rule on every FreeMonthsBenefit *and* on every
OfferFreeMonthsBenefit at construction, so the schema mirrors the domain
exactly. Making the Offer-side column nullable would have allowed a commercial
entitlement to exist without a rule, which the production conversion path would
then have had to decide what to do with (§13).

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

A second, follow-up migration
`20260929193144_RequireEligibilityRuleOnOfferFreeMonths` (see §13) alters
`Platform.OfferFreeMonthsBenefits.EligibilityRule` from nullable to
`nvarchar(4000) NOT NULL`. It is also **schema-only**: a single
`AlterColumn` with `nullable: false` and **no** `defaultValue`, so no
historical commercial fact is invented (§L.35).

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
| 26 | Every benefit row (`ContractBenefit`, `FreeMonthsBenefit`, and `OfferFreeMonthsBenefit`) MUST carry exactly one EligibilityRule | `FreeMonthsBenefit.Create` and `OfferFreeMonthsBenefit.Create` reject a null rule; `ContractBenefit` already did; both EF columns are `NOT NULL`; the Offer → Contract handler fails explicitly on a null rule | `Test06_Create_WithNullRule_Fails`, `Test35_OfferFreeMonthsBenefit_Create_WithNullRule_Fails`, `Test36_..._WithValidRule_Succeeds`, `TestC07_...`, `TestC10_OfferToContract_WithNullRuleRow_FailsExplicitly_AndCreatesNoContract`, `SqlC01_Schema`, `SqlC03_Snapshot`, `SqlC08_..._IsNotNull_InSchema` |
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
| `SqlC08` | Schema requiredness | `INFORMATION_SCHEMA.COLUMNS` confirms `EligibilityRule` is `nvarchar(4000) NOT NULL` with **no** `COLUMN_DEFAULT` on **both** `Platform.FreeMonthsBenefits` and `Platform.OfferFreeMonthsBenefits` |

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

### 6.3.1 Schema verification (SqlC08)

`Platform.OfferFreeMonthsBenefits.EligibilityRule` is `nvarchar(4000) NOT NULL`
with **no** default, matching `Platform.FreeMonthsBenefits.EligibilityRule`
exactly. The absence of a `COLUMN_DEFAULT` proves the tightening migration
introduced no fabricated rule value.

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
database. Correction 1 did not affect persistence — no migration was required
for it. (Correction 2 in §13 *did* add a migration, but for the Offer-side
`EligibilityRule` column, unrelated to the state machine.)

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
| Solution build | 0 errors |
| Task C unit tests (pure domain) | 41 passed, 0 failed |
| Task C snapshot/EF tests (InMemory) | 10 passed, 0 failed |
| Task C SQL Server tests | 9 passed, 0 failed (Correction 3 added SqlC09) |
| Total Task C tests | 60 passed, 0 failed |
| Full regression | 1744 total, 1743 passed, 0 failed, 1 skipped (pre-existing; exact test named in §10.1) |
| EF Core migrations | `20260929174030_AddFreeMonthsBenefits` and `20260929193144_RequireEligibilityRuleOnOfferFreeMonths` both applied successfully against Local SQL Server |
| `dotnet ef migrations has-pending-model-changes` | "No changes have been made to the model since the last migration." |
| Hidden-inference audit | Clean (no derivation, no fabrication, no inference) |
| Implementation commit | `2530af1` (`feat(billing): add free months benefit foundation`) |
| Correction 1 commit | `bdac0b1` (`fix(billing): decouple free months eligibility from fulfillment`) |
| Correction 2 commit | `c0055e1` (`fix(billing): enforce free months eligibility rule snapshot`) |

### 10.1 Exact full-suite skip

Exactly one test in the full suite is skipped. It is pre-existing and
unrelated to Task C:

```text
Centerix.SecurityTests.Task18_5CreditEconomicOriginSqlServerTests
    .Test15_Task1851_MixedLineageProportionalTransferredOrigin

Skip reason (verbatim from its [Fact] attribute):
  "Complex overlapping subscription scenario - covered by Test16 and other tests"
```

No Task C test is skipped.

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
* **No migration required for Correction 1.** `dotnet ef migrations
  has-pending-model-changes` returned "No changes have been made to the model
  since the last migration." at that point. The correction was a behavioural /
  domain-only change.

### Correction 2 (subsequent — EligibilityRule completeness)

* **Modified (5 files):**
  * `src/Centerix.Domain/Platform/Promotions/OfferBenefit.cs` —
    `OfferFreeMonthsBenefit.EligibilityRule` made non-nullable and required in
    `Create` (null rejected with
    `OfferFreeMonthsBenefit.EligibilityRule_Required`). `OfferBenefit` (the
    non-FreeMonths class) is unchanged and keeps its nullable rule.
  * `src/Centerix.Application/Platform/Promotions/Commands/CreateContractFromOfferCommand.cs`
    — removed the silent `continue`; a null rule now returns
    `Error.Validation("Offer.IncompleteFreeMonthsBenefit", ...)`.
  * `src/Centerix.Infrastructure/Data/Configurations/FreeMonthsBenefitConfiguration.cs`
    — `OfferFreeMonthsBenefitConfiguration.EligibilityRule` marked
    `.IsRequired()`.
  * `src/Centerix.Infrastructure/Data/Migrations/AppDbContextModelSnapshot.cs`
    — regenerated by EF tooling.
  * The three `TaskC_FreeMonthsBenefit*Tests.cs` files.
* **Added (2 files):**
  * `src/Centerix.Infrastructure/Data/Migrations/20260929193144_RequireEligibilityRuleOnOfferFreeMonths.cs`
  * `...RequireEligibilityRuleOnOfferFreeMonths.Designer.cs`
* **Documentation updated:** this report (§3.1, §4.1, §4.2, §5, §6.2, §6.3.1,
  §7.3, §10, §12, §13).
* **Migration required** — the existing schema was **not** already correct:
  `20260929174030_AddFreeMonthsBenefits` had created
  `Platform.OfferFreeMonthsBenefits.EligibilityRule` as `nullable: true`. The
  new migration alters it to `nvarchar(4000) NOT NULL` with **no**
  `defaultValue` (EF's generated `defaultValue: ""` was removed to honour
  design invariant 35).

### Correction 3 (subsequent — SQL Server malformed-row verification)

* **Modified (1 file):**
  * `tests/Centerix.SecurityTests/TaskC_FreeMonthsBenefitSqlServerTests.cs`
    — added `SqlC09_OfferToContract_WithMalformedFreeMonthsBenefit_FailsExplicitly_AndCreatesNoContract`.
    No production code, no configuration, no migration, no model snapshot
    changes — purely a test addition that closes the acceptance gap from
    §6 / §8 of the previous correction's brief.
* **Documentation updated:** this report (§6.2, §6.3.1, §10, §13.3, §13.6).
* **No migration required.** `dotnet ef migrations has-pending-model-changes`
  returned "No changes have been made to the model since the last migration."
  on both `AppDbContext` and `TenantDbContext`. `SqlC09` is a pure
  behavioural test against the existing schema; it does not touch any model
  type, configuration, or migration.
* **Local SQL Server used.** Docker / Testcontainers were NOT used; the
  fixture's probe confirmed local SQL Server reachability and the test ran
  against that instance.

---

## 12. Stop Condition

Task C is closed (post-Correction 1 and Correction 2).

* Domain aggregate + state machine + Offer → Contract snapshot + EF Core
  configuration + schema migration are implemented and tested.
* Eligibility and Fulfillment are independent state machines: each
  combination such as `Eligible + Granted`, `NotEligible + Granted`,
  `Eligible + AppliedToSubscription`, `NotEligible + AppliedToSubscription`
  is reachable, verified by the four independence tests in §7.
* Fulfillment remains monotone: `Pending → Granted → AppliedToSubscription`.
  `Grant` still requires `EligibilityStatus == Eligible` at the moment of
  the `Pending → Granted` transition.
* Both migrations are schema-only and do NOT manufacture FreeMonthsBenefit
  rows from `Contract.BonusMonths > 0`, and the tightening migration adds no
  fabricated default (per design invariant 35).
* `EligibilityRule` is required on both the Contract-side and Offer-side
  snapshots in the domain, in the EF model, and in the database schema; a
  missing rule in the Offer snapshot aborts the conversion with an explicit
  validation error instead of silently dropping the commercial entitlement.
* All relevant design invariants (§L.26, §L.28, §L.29, §L.31, §L.32, §L.34,
  §L.35) are enforced by the domain boundary and verified by tests.
* 59 Task C tests pass, and the full suite passes with zero regressions
  (1 pre-existing skip, named in §10.1).
* EF pending-model-changes is clean.
* The next task (Task D) can implement `GrantBenefitCommand` and
  `ApplyFreeMonthsToSubscriptionCommand` against the state-machine exposed
  here, plus `TenantPlan.AppliedFreeMonthsBenefitIds[]` for idempotency.

---

## 13. EligibilityRule Completeness / No Silent Commercial Entitlement Loss (Correction 2)

**Status:** VERIFIED.

The initial Task C implementation allowed OfferFreeMonthsBenefit.Create() to
accept a null EligibilityRule (via an optional parameter defaulting to null).
The production CreateContractFromOfferHandler silently skipped any
OfferFreeMonthsBenefit with a null rule during Offer → Contract conversion.
This contradicted design invariant 26 ("every Entitlement Benefit row MUST carry
exactly one EligibilityRule") and risked silent loss of commercial entitlements.

### 13.1 Changes applied

| File | Change |
|---|---|
| [OfferBenefit.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Promotions/OfferBenefit.cs) | `OfferFreeMonthsBenefit.EligibilityRule` changed from `EligibilityRule?` to `EligibilityRule` (non-nullable). `Create()` changed from `EligibilityRule? eligibilityRule = null` to `EligibilityRule eligibilityRule` (required, no default, no optional creation mode retained). Null guard added: `if (eligibilityRule is null) return Error.Validation("OfferFreeMonthsBenefit.EligibilityRule_Required", ...)`. `OfferBenefit` (the non-FreeMonths class) is deliberately **unchanged** and keeps its nullable, default-null rule — only `OfferFreeMonthsBenefit` is corrected. |
| [CreateContractFromOfferCommand.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Promotions/Commands/CreateContractFromOfferCommand.cs) | Removed the silent `if (offerFreeMonths.EligibilityRule is null) continue;` skip. Replaced with explicit failure: `return Error.Validation("Offer.IncompleteFreeMonthsBenefit", ...)`. A null rule now aborts the whole Contract creation rather than silently dropping the benefit. |
| [FreeMonthsBenefitConfiguration.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/Data/Configurations/FreeMonthsBenefitConfiguration.cs) | `OfferFreeMonthsBenefitConfiguration`: `EligibilityRule` marked `.IsRequired()`. The schema now enforces `NOT NULL` at the database level. |
| [20260929193144_RequireEligibilityRuleOnOfferFreeMonths.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/Data/Migrations/20260929193144_RequireEligibilityRuleOnOfferFreeMonths.cs) | New migration altering `Platform.OfferFreeMonthsBenefits.EligibilityRule` from `nullable: true` to `nvarchar(4000) NOT NULL`. **No fabricated default** — EF's generated `defaultValue: ""` was removed per design invariant 35. |

### 13.2 Invariant summary

```text
OfferFreeMonthsBenefit.EligibilityRule  != null   (domain + EF model + schema)
FreeMonthsBenefit.EligibilityRule       != null   (domain + EF model + schema)
CreateContractFromOfferHandler: NULL RULE -> EXPLICIT FAILURE (never a silent skip)
Offer -> Contract copies EligibilityRule exactly
```

### 13.3 Tests added / updated

| Test | File | Verifies |
|---|---|---|
| `Test35_OfferFreeMonthsBenefit_Create_WithNullRule_Fails` | [TaskC_FreeMonthsBenefitFoundationTests.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/tests/Centerix.SecurityTests/TaskC_FreeMonthsBenefitFoundationTests.cs) | `Create(..., null!)` fails with `OfferFreeMonthsBenefit.EligibilityRule_Required` **and no object is created** (`result.Value` throws). |
| `Test36_OfferFreeMonthsBenefit_Create_WithValidRule_Succeeds` | Same | `Create(..., validRule)` succeeds and preserves `EntitlementMonths` and the rule. |
| `TestC07_OfferFreeMonthsBenefit_Create_RejectsNullRule_CannotSilentlyLoseEntitlement` | [TaskC_FreeMonthsBenefitSnapshotTests.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/tests/Centerix.SecurityTests/TaskC_FreeMonthsBenefitSnapshotTests.cs) | Replaces the old test that built a null-rule benefit and asserted the silent skip. |
| `TestC06_OfferToContract_SnapshotsFreeMonthsBenefitThroughProductionFlow` | Same | **Existing production-snapshot fidelity**: the real `AcceptOfferHandler` + `CreateContractFromOfferHandler` copy the rule verbatim (structural equality and byte-identical canonical JSON) with `EntitlementMonths` preserved. |
| `TestC10_OfferToContract_WithNullRuleRow_FailsExplicitly_AndCreatesNoContract` | Same | **Defensive production conversion**: after persisting a valid Offer, the tracked row's rule is nulled out (the only way to reach the branch — EF rejects a null rule at write time on both InMemory and SQL Server). The real handler then fails with `Offer.IncompleteFreeMonthsBenefit` and **no Contract row is persisted**. |
| `SqlC08_OfferFreeMonthsBenefits_EligibilityRule_IsNotNull_InSchema` | [TaskC_FreeMonthsBenefitSqlServerTests.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/tests/Centerix.SecurityTests/TaskC_FreeMonthsBenefitSqlServerTests.cs) | `INFORMATION_SCHEMA.COLUMNS` confirms `EligibilityRule` is `nvarchar(4000) NOT NULL` **and has no `COLUMN_DEFAULT`** on both `Platform.OfferFreeMonthsBenefits` and `Platform.FreeMonthsBenefits`. |
| `SqlC09_OfferToContract_WithMalformedFreeMonthsBenefit_FailsExplicitly_AndCreatesNoContract` | Same | **Production-handler defense on Local SQL Server** (Correction 3): a valid Offer + valid `OfferFreeMonthsBenefit` are written to SQL Server and accepted via the real `AcceptOfferHandler`. A fresh `DbContext` then materializes the Offer from SQL Server, the rule is nulled on the materialized entity via the same controlled test-only reflection technique used by `TestC10`, and the **real** `CreateContractFromOfferHandler` is invoked against that SQL-backed DbContext. Asserts: handler returns `Offer.IncompleteFreeMonthsBenefit`; no Contract row exists in SQL Server (fresh `DbContext`); Offer `Status != ConvertedToContract`, `ContractId == null`, `ConvertedAtUtc == null`; the FreeMonthsBenefit is still present on the Offer in SQL Server with a non-null rule. |

### 13.4 Verification evidence (historical — Correction 2 baseline)

* **Build:** `dotnet build Centerix.slnx --no-restore` — 0 errors.
* **Task C tests:** 59 passed, 0 failed (41 pure-domain + 10 InMemory snapshot + 8 Local SQL Server) (historical — pre-Correction-3; current total is **60**, **9 SQL Server**). [Superseded by §13.6.]
* **Full regression:** see §10.1 for the exact totals and the exact skipped test.
* **EF Core migrations:** `dotnet ef migrations has-pending-model-changes` returns "No changes have been made to the model since the last migration."
* **No unrelated diff:** Only `OfferBenefit.cs`, `CreateContractFromOfferCommand.cs`, `FreeMonthsBenefitConfiguration.cs`, the three Task C test files, this report, and the new migration (plus its designer and the regenerated model snapshot) were modified.

### 13.5 Re-verification evidence (historical — post-correction-2 SHA backfill, pre-Correction-3)

**[Superseded by §13.6]** — This section records the verification run at HEAD
`c0055e1` (Correction 2). Current state is 60 Task C tests, 9 SQL Server tests
(§13.6).

| Command | Result |
|---|---|
| `dotnet build Centerix.slnx --no-restore` | 0 errors |
| `dotnet test … --filter "FullyQualifiedName~TaskC_FreeMonthsBenefitFoundationTests"` | 41 passed, 0 failed, 0 skipped |
| `dotnet test … --filter "FullyQualifiedName~TaskC_FreeMonthsBenefitSnapshotTests"` | 10 passed, 0 failed, 0 skipped |
| `dotnet test … --filter "FullyQualifiedName~TaskC_FreeMonthsBenefitSqlServerTests"` (Local SQL Server: `Server=.`) | 8 passed, 0 failed, 0 skipped (historical — pre-Correction-3; current is 9) |
| `dotnet test … --filter "FullyQualifiedName~TaskC"` (combined) | **59 passed, 0 failed, 0 skipped** (historical — pre-Correction-3; current is 60) (Duration 14 s) |
| `dotnet ef migrations has-pending-model-changes --context AppDbContext` | "No changes have been made to the model since the last migration." |
| `dotnet ef migrations has-pending-model-changes --context TenantDbContext` | "No changes have been made to the model since the last migration." |
| Local SQL Server probe (`sqlcmd -S . -Q "SELECT @@VERSION"`) | Microsoft SQL Server 2022 RTM (16.0.1000.6) Developer Edition, reachable |
| Docker daemon probe (`docker ps`) | Unavailable — confirms the fixture cannot silently fall back to Testcontainers; Local SQL Server is the only available target and was used |

**No skipped tests.** No Task C test was filtered out. The single pre-existing
full-suite skip named in §10.1 remains the only skipped test in the suite and
is unrelated to Task C.

### 13.6 Correction 3 — SQL Server malformed-row production-flow verification

Correction 2 added `TestC10_OfferToContract_WithNullRuleRow_FailsExplicitly_AndCreatesNoContract`
on the InMemory provider and `SqlC08` (schema `NOT NULL`) on Local SQL Server.
The remaining acceptance gap was that the **same production-handler defense
scenario was not executed against real SQL Server** — only the schema
constraint was verified there. Correction 3 closes that gap.

#### Why a separate test rather than re-running `TestC10` against SQL Server

`TestC10` runs against the InMemory provider, where the EF change tracker is
the source of truth. On real SQL Server, the source of truth is the database;
the reflection trick (nulling the rule after persistence) must be applied to
the entity **materialized from SQL Server** to prove the production handler
remains defensive against malformed state that could (theoretically) reach it
after database round-trip.

#### Database invariant vs. application defensive invariant

The two defenses are distinct and both must be verified:

```text
Database invariant:
  Platform.OfferFreeMonthsBenefits.EligibilityRule  NOT NULL
  Platform.FreeMonthsBenefits.EligibilityRule       NOT NULL
  → confirmed by SqlC08 (INFORMATION_SCHEMA.COLUMNS)

Application defensive invariant:
  If a null rule reaches the production conversion handler
  (via an already-materialized entity, a test-only corruption,
  or a future pre-invariant legacy database row),
  the handler MUST return Offer.IncompleteFreeMonthsBenefit
  and MUST NOT create the Contract / mark the Offer converted.
  → confirmed by SqlC09 against Local SQL Server
```

The schema constraint prevents a `NULL` from ever being persisted in the first
place, so `SqlC09` cannot and does NOT attempt to insert a `NULL` rule
directly. The corruption is simulated **after SQL Server materialization** on
the materialized/tracked entity, using the same controlled test-only
technique already proven in `TestC10`. `SaveChangesAsync` is never called on
the corrupted entity, so the in-memory corruption does NOT propagate to SQL
Server; the persisted FreeMonthsBenefit row is verified to retain its
non-null rule on reload.

#### SqlC09 verification evidence (Local SQL Server)

* **Connection:** `Server=.;Trusted_Connection=True;TrustServerCertificate=True;Encrypt=False;Connect Timeout=5`
* **Database:** a per-run isolated `CenterixSec_*` database created by the fixture and dropped on disposal
* **Test:** `SqlC09_OfferToContract_WithMalformedFreeMonthsBenefit_FailsExplicitly_AndCreatesNoContract` — passed in 720 ms
* **Assertions proven against SQL Server:**
  * A. Handler returned `IsSuccess == false`
  * B. `contractResult.Errors!.First().Code == "Offer.IncompleteFreeMonthsBenefit"`
  * C. No `Contracts` row with the attempted `ContractNumber` exists in SQL Server (verified via fresh `DbContext`)
  * D. Reloaded Offer `Status != ConvertedToContract`, `ContractId == null`, `ConvertedAtUtc == null`
  * E. Reloaded Offer still contains the `OfferFreeMonthsBenefit` row with `EligibilityRule != null`, `EntitlementMonths == 1`, `CurrencyCode == "EGP"`
* **Docker / Testcontainers:** NOT used. The fixture's probe confirmed local SQL Server is reachable and the run used the local instance. Docker daemon is unavailable on this machine (`docker ps` fails) so a silent fallback to Testcontainers is impossible.

#### §13.5 update — full Task C test totals after Correction 3

After Correction 3 the Task C test surface is:

| Suite | Total | Passed | Failed | Skipped |
|---|---|---|---|---|
| TaskC_FreeMonthsBenefitFoundationTests (pure domain) | 41 | 41 | 0 | 0 |
| TaskC_FreeMonthsBenefitSnapshotTests (InMemory) | 10 | 10 | 0 | 0 |
| TaskC_FreeMonthsBenefitSqlServerTests (Local SQL Server) | **9** (was 8) | **9** | 0 | 0 |
| **Task C combined** | **60** | **60** | 0 | 0 |

---

## 14. EligibilityRule Mandatory Invariant

This is the canonical statement of the invariant Task C enforces. Any future
change that violates any clause is a regression and must be rejected.

### 14.1 Invariant clauses

```text
OfferFreeMonthsBenefit.Create
    requires EligibilityRule                              (non-nullable parameter)
    → rejects null with Error.Validation(
          "OfferFreeMonthsBenefit.EligibilityRule_Required")
    → no entity is materialized from a null rule           (Test35)

CreateContractFromOfferHandler
    never silently skips malformed FreeMonthsBenefits     (no `continue` on null rule)
    → returns Error.Validation("Offer.IncompleteFreeMonthsBenefit", ...)
      on the first OfferFreeMonthsBenefit whose EligibilityRule is null
    → the whole Contract creation is aborted              (TestC10)
    → the Offer is NOT marked ConvertedToContract
    → no partial Contract row is persisted

Malformed persisted OfferFreeMonthsBenefit row
    causes explicit conversion failure                    (TestC10 + SqlC08 + SqlC09)
    → schema is NOT NULL on both OfferFreeMonthsBenefits and FreeMonthsBenefits
    → EF rejects a null rule at write time on both InMemory and SQL Server
    → the production handler additionally fails explicitly if a row somehow
      reaches it with a null rule (defense in depth; TestC10 on InMemory,
      SqlC09 on real SQL Server after materialization)

No partial Contract is persisted                         (TestC10)
    → SaveChangesAsync only runs after the entire snapshot copy succeeds
    → on failure, no Contract / FreeMonthsBenefits rows are written

Offer → Contract copies EligibilityRule exactly           (TestC06 + SqlC03)
    → structural equality on the EligibilityRule value object
    → byte-identical canonical JSON before and after the SQL round-trip
```

### 14.2 Why four verification points across three layers of defense

| Layer | Defense | Tests |
|---|---|---|
| Domain factory | `OfferFreeMonthsBenefit.Create` rejects null at construction | `Test35` |
| EF Core | `IsRequired()` on `EligibilityRule` prevents null persistence at the schema level | `SqlC08` |
| Production handler (InMemory) | Explicit failure on the materialized entity with a null rule | `TestC10` |
| Production handler (SQL Server) | Same defense confirmed against a real SQL Server DbContext | `SqlC09` |

Each layer blocks a different escape route. A regression in any single layer is
caught by a test on a different layer.

### 14.3 Search audit — no silent drops remain

The full production source under `src/` was searched for the patterns
`EligibilityRule is null`, `EligibilityRule? == null`, and any
`continue` statement inside loops over `FreeMonthsBenefits`,
`OfferFreeMonthsBenefits`, or `ContractFreeMonthsBenefits`. The only
remaining hit is the explicit failure branch in
`CreateContractFromOfferCommand.cs:218`, which is not a silent drop but
the defensive explicit failure documented in §13.4 and verified by `TestC10`.
