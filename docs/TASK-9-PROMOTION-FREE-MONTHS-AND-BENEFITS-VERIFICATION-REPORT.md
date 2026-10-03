# TASK 9 CORRECTION — PROMOTION BENEFIT ELIGIBILITY RULE — READY FOR REVIEW

**TASK 9 CORRECTION — READY FOR REVIEW**

> **Correction notice (this revision).** The previous revision of this report described a
> `BuildEntitlementRule` helper inside `PromotionCalculationService` that generated a default
> eligibility rule from the calculated offer. That was wrong, and it has been removed.
>
> **The benefit eligibility rule is TRUSTED, CONFIGURED `Promotion` DATA.** It is supplied by
> platform configuration, persisted on the Promotion, snapshotted onto the Offer, and read at
> Contract creation. It is never derived from the Promotion type, the discount, the plan, or any
> runtime state, and there is no default anywhere in the codebase. A benefit-bearing promotion
> without a configured rule is a validation failure, not a silently generated `ContractActive`.

This document reports the implementation and verification of Task 9: a Promotion can grant
**free months** or an **additional benefit/gift** on top of the purchased subscription, governed by
an explicitly configured commercial eligibility rule.

---

## 1. What the previous revision got wrong

The previous implementation built the rule as:

```csharp
// REMOVED — generated, not configured
Benefit promotions  → AllOf(ContractActive, AmountPaidAtLeast(FinalAmount))
Free months         → ContractActive
```

Consequences, all now fixed:

- The Promotion never stored a rule, so a promotion's commercial gate was not reviewable, not
  auditable, and not changeable without a code change.
- A promotion grant could never demand anything stronger than "the contract is active" plus, for
  benefits, "the customer paid the final amount" — the amount requirement was derived from the
  discount, not configured.
- A `FreeMonthsBenefit` could be produced from a rule the platform never chose.

## 2. The corrected design

### 2.1 Configured, not derived

```csharp
// Promotion
public EligibilityRule? BenefitEligibilityRule { get; private set; }
```

| Promotion type | Rule |
|---|---|
| `FreeMonthsBonus` | **required** |
| `AdditionalBenefits` | **required** |
| `PayForXMonths` with `FreeMonthsCount` | **required** |
| `PayForXMonths` without `FreeMonthsCount` | not allowed |
| `PercentageDiscount` / `FixedAmountDiscount` / `PromotionalPrice` | **rejected** — a discount-only promotion grants nothing, so a rule would be dead configuration |

No code path generates a rule. `PromotionCalculationService` only *reads*
`promotion.BenefitEligibilityRule` and passes it through; if a benefit-bearing promotion somehow
reaches calculation without one (a pre-rule row), it fails with
`Promotion.BenefitEligibilityRule_Missing` rather than granting an ungated entitlement.

### 2.2 Validation error codes

| Code | Condition |
|---|---|
| `Promotion.BenefitEligibilityRule_Required` | benefit-bearing promotion with no rule (Create and Update) |
| `Promotion.BenefitEligibilityRule_NotSupportedForType` | a rule supplied for a discount-only type |
| `Promotion.BenefitEligibilityRule_Invalid` | payload is not a canonical rule |
| `Promotion.BenefitEligibilityRule_Missing` | benefit-bearing promotion reaching calculation with a null rule |

### 2.3 Canonical serialization

`Promotion.BenefitEligibilityRule` uses **the same canonical serializer and value converter** as
every other eligibility rule column in the database (`ContractBenefit.EligibilityRule`,
`FreeMonthsBenefit.EligibilityRule`, `OfferBenefit.EligibilityRule`, …):

```csharp
builder.Property(p => p.BenefitEligibilityRule)
    .HasConversion(
        v => v == null ? null : EligibilityRuleSerializer.Serialize(v),
        v => v == null ? null : EligibilityRuleSerializer.Deserialize(v)!)
    .HasColumnType("nvarchar(max)");
```

The application layer accepts the canonical JSON form and maps it into the domain algebra through
`BenefitEligibilityRuleParser`. It is **not** raw arbitrary JSON: parsing goes through the closed
domain algebra, which validates every leaf (AllOf arity, defined enums, non-negative amounts), and
a non-canonical payload is rejected with a validation error instead of being stored. The DTO
exposes the rule read-only.

### 2.4 Snapshot chain

```text
Promotion.BenefitEligibilityRule        (configured, persisted, audit-logged)
   ↓ CalculateAndPersistOfferHandler
OfferFreeMonthsBenefit.EligibilityRule  /  OfferBenefit.EligibilityRule
   ↓ AcceptOfferHandler / CreateContractFromOfferHandler
FreeMonthsBenefit.EligibilityRule       /  ContractBenefit.EligibilityRule
```

Each step copies the rule verbatim. No step consults the current Promotion, Plan, or environment.
A later Promotion edit affects only newly calculated Offers (T9-C06, SQL-T9-C08).

---

## 3. API surface

### 3.1 Promotion types

```csharp
PercentageDiscount  = 0,   // unchanged
FixedAmountDiscount = 1,   // unchanged
PayForXMonths       = 2,   // unchanged
PromotionalPrice    = 3,   // unchanged
FreeMonthsBonus     = 4,   // NEW
AdditionalBenefits  = 5    // NEW
```

### 3.2 Promotion benefit configuration (all optional, all nullable)

| Field | Type | Meaning |
|---|---|---|
| `FreeMonthsCount` | `int?` | Free months granted. Required for `FreeMonthsBonus`. |
| `BenefitName` | `string?` | Benefit name. Required for `AdditionalBenefits`. |
| `BenefitDescription` | `string?` | Optional benefit description. |
| `BenefitValue` | `decimal?` | Contractual value. Required, strictly positive. |
| `BenefitType` | `ContractBenefitType?` | Benefit category. Required. |
| `BenefitCurrencyCode` | `string?` | ISO-4217, 3 letters. Required. |
| `BenefitEligibilityRule` | `EligibilityRule?` | Canonical JSON. **Required for benefit-bearing types.** |

Wired through `CreatePromotionCommand`, `UpdatePromotionCommand`, `PromotionDto`,
`ListPromotionsQuery` / `GetPromotionByIdQuery` and the create/update audit payloads. The
controller binds the commands directly, so no controller change was required.

### 3.3 Other validation (unchanged from the first Task 9 revision)

| Code | Condition |
|---|---|
| `Promotion.FreeMonthsCount_Invalid` | `FreeMonthsCount` missing or `<= 0` for `FreeMonthsBonus` |
| `Promotion.BenefitName_Required` | `AdditionalBenefits` without a name |
| `Promotion.BenefitValue_Invalid` | value missing or `<= 0` |
| `Promotion.BenefitType_Invalid` | benefit type missing or undefined |
| `Promotion.BenefitCurrencyCode_Invalid` | currency missing or not 3 letters |
| `Promotion.BenefitConfig_Conflicting` | free months **and** a benefit configured together |
| `Promotion.BenefitConfig_NotSupportedForType` | benefit config on a discount-only type |
| `Promotion.BenefitValue_ExceedsMaximum` | benefit value > `3 ×` the offer's monthly list price |
| `Promotion.FreeMonths_ExceedDuration` | free months > the purchased term |

The gift cap uses the same monthly value that becomes `Offer.MonthlyListPrice` and then
`Contract.ContractualMonthlyValue`, so the offer can never be converted into a contract that
violates the existing `Contract` gift invariant. No silent substitute value is used.

### 3.4 PayForXMonths

`PayForXMonths` keeps its existing commercial behaviour by default. When `FreeMonthsCount` is set,
the granted entitlement is the **real difference** `DurationMonths − ChargedMonths` — never the
configured counter — so the entitlement cannot contradict the "pay X get Y" promise.

---

## 4. Data model

### 4.1 Migrations (both schema-only and nullable-additive)

```text
20261003145517_AddPromotionBenefitConfiguration
    FreeMonthsCount        int            NULL
    BenefitName            nvarchar(200)  NULL
    BenefitDescription     nvarchar(500)  NULL
    BenefitValue           decimal(18,2)  NULL
    BenefitType            tinyint        NULL
    BenefitCurrencyCode    nvarchar(3)    NULL

20261003160538_AddPromotionBenefitEligibilityRule
    BenefitEligibilityRule nvarchar(max)  NULL
```

No existing column was altered, renamed, retyped or dropped, and **no historical promotion row was
backfilled**. Existing promotions keep their exact previous behaviour. The rule column is nullable
specifically so pre-rule rows keep a null rule instead of an invented historical one.

### 4.2 No new entity types

No new aggregate, entity, DbSet or table. The grant reuses `OfferFreeMonthsBenefit` and
`OfferBenefit`, and on the contract side `FreeMonthsBenefit` and `ContractBenefit`.

---

## 5. Tests

### 5.1 Correction tests — domain (`Task9C_PromotionBenefitRuleDomainTests`, 8 tests)

| ID | Guarantees |
|---|---|
| T9-C01 | The configured rule is stored on the Promotion and used verbatim by both benefit types |
| T9-C02 | No default rule is ever generated; a discount offer never gains an entitlement or a rule |
| T9-C02b | An ungated `OfferFreeMonthsBenefit` cannot be constructed, and a ruleless promotion cannot grant one |
| T9-C03 | `Create` rejects a benefit-bearing promotion with no rule (all three benefit-bearing shapes) |
| T9-C04 | `Update` cannot remove the rule from a benefit-bearing promotion |
| T9-C05 | A rule on a discount-only promotion is rejected |
| T9-C06 | A new rule applies to the next calculation only; the earlier offer object is unchanged |
| T9-C07 | The configured rule reaches both Offer snapshot children unchanged, and the Offer accepts them |

### 5.2 Correction tests — application (`Task9C_PromotionBenefitRuleApplicationTests`, 10 tests)

| ID | Guarantees |
|---|---|
| T9-C-A01 | Create persists the rule; `GetPromotionById` and `ListPromotions` return it |
| T9-C-A02 | Create without a rule returns `..._Required` and persists nothing |
| T9-C-A03 | Create with a rule on a discount-only promotion is rejected |
| T9-C-A04 | 5 malformed / non-canonical payloads are all rejected and never stored |
| T9-C-A05 | Update changes the rule for future offers; the existing offer snapshot keeps its own rule |
| T9-C-A06 | The configured rule reaches the persisted `OfferBenefit` row unchanged |

### 5.3 Correction tests — SQL Server (`Task9C_PromotionBenefitRuleSqlServerTests`, 11 tests)

| ID | Guarantees |
|---|---|
| SQL-T9-C01 | The additive rule column exists, is nullable, and is `nvarchar(max)` |
| SQL-T9-C02 | A configured rule persists and reloads byte-identically through a new context |
| SQL-T9-C03 | 4 malformed rule payloads are rejected and nothing is persisted |
| SQL-T9-C04 | A benefit-bearing promotion without a rule is rejected and nothing is persisted |
| SQL-T9-C05 | A rule on a discount-only promotion is rejected and nothing is persisted |
| SQL-T9-C06 | The configured rule reaches the persisted `OfferFreeMonthsBenefit` row |
| SQL-T9-C07 | The configured rule reaches the persisted `OfferBenefit` row |
| SQL-T9-C08 | A later rule change applies to new offers only, never to existing snapshots |

The SQL create-paths construct the **real** `CreatePromotionHandler` against the **real** SQL
Server context, substituting only the platform authorization boundary and the audit sink — the
established pattern in this repository's SQL tests, so a failure cannot be attributed to the harness.

### 5.4 First-revision tests (retained)

`T9-D01..D17` (18), `T9-A01..A10` (10) and `SQL-T9-01..12` (12) all still pass. No assertion was
weakened: each benefit-bearing seed now supplies an explicit rule through its helper, and the
assertions are unchanged. One obsolete claim was corrected rather than deleted — the old
`T9_D14_EntitlementCarriesRule_AndRuleIsOfferDerivedNotPlanDerived` asserted that the rule was
*offer-derived*, which is exactly the defect; it is now
`T9_D14_EntitlementCarriesExactlyTheConfiguredPromotionRule` and asserts the opposite.

---

## 6. Verification run

### Build

```text
dotnet build Centerix.slnx --no-restore
→ 0 Errors
```

### Task 9 suites (all levels)

| Suite | Total | Passed | Failed | Skipped | Not Executed | Exit Code |
|---|---|---|---|---|---|---|
| `Task9_PromotionBenefitsDomainTests` | 18 | 18 | 0 | 0 | 0 | 0 |
| `Task9_PromotionBenefitsApplicationTests` | 10 | 10 | 0 | 0 | 0 | 0 |
| `Task9_PromotionBenefitsSqlServerTests` | 12 | 12 | 0 | 0 | 0 | 0 |
| `Task9C_PromotionBenefitRuleDomainTests` | 8 | 8 | 0 | 0 | 0 | 0 |
| `Task9C_PromotionBenefitRuleApplicationTests` | 10 | 10 | 0 | 0 | 0 | 0 |
| `Task9C_PromotionBenefitRuleSqlServerTests` | 11 | 11 | 0 | 0 | 0 | 0 |
| **Task 9 total** | **69** | **69** | **0** | **0** | **0** | **0** |

### Full regression

| Metric | Value |
|---|---|
| Total | 1991 |
| Passed | 1990 |
| Failed | 0 |
| Skipped | 1 |
| Not Executed | 0 |
| Exit Code | 0 |

The baseline before this correction was 1962 total / 1961 passed / 1 skipped; the 29 new
correction tests account for the difference. The single skip is pre-existing and unrelated
(`Task18_5CreditEconomicOriginSqlServerTests.Test15_Task1851_MixedLineageProportionalTransferredOrigin`).
No infrastructure failure was reported as a skip.

---

## 7. SQL Server

```text
Server:        .            (local SQL Server, explicitly pinned via
                            CENTERIX_SQLTEST_CONNECTION="Server=.;Trusted_Connection=True;
                            TrustServerCertificate=True;Encrypt=False")
Docker:                  NOT USED
Testcontainers:          NOT USED
In-memory substitution:  used only for the T9-A* / T9-C-A* application suites, which are
                         explicitly InMemory suites by design; every SQL guarantee runs
                         against real SQL Server
```

---

## 8. EF model status

```text
dotnet ef migrations has-pending-model-changes --context AppDbContext
→ No changes have been made to the model since the last migration.

dotnet ef migrations has-pending-model-changes --context TenantDbContext
→ No changes have been made to the model since the last migration.
```

`PendingModelChangesWarning` is not suppressed.

---

## 9. Source verification

| Check | Result |
|---|---|
| `BuildEntitlementRule` in live source | **absent** — the generated-rule helper is gone |
| `AmountPaidAtLeast` / `PaymentTermsEquals` / `ContractActive()` inside `Platform/Promotions` | **0 matches** — the promotion path generates no rule |
| `PromotionCalculationService` rule source | only `promotion.BenefitEligibilityRule` |
| `Promotion.BenefitEligibilityRule` persisted | nullable column, canonical serializer conversion |
| `PromotionCreate` / `PromotionUpdate` rule argument | present and domain-validated |
| Offer handler | copies the promotion rule verbatim into both snapshot children |

---

## 10. Regressions checked explicitly

| Guarantee | Result |
|---|---|
| The four original promotion types still work | T9-D03, T9-A09, SQL-T9-10, T9-C02, T9-C05 |
| `PromotionType` existing values unchanged | 0..3 untouched; 4 and 5 appended |
| No historical promotion row backfilled | both migrations nullable-additive only |
| EligibilityStatus reversal untouched | no eligibility code changed |
| Fulfillment system untouched | no fulfillment code changed |
| No entity, DbSet or table added | only columns on the existing `Promotions` table |
| `Plan.BonusMonths` behaviour unchanged | T9-D12, T9-A06, SQL-T9-07 |
| `PaymentTerms` still an explicit operator decision | not derived from any promotion field |
| Tenant isolation | offers/benefits stamped and queried per tenant; SQL tests use isolated tenants |
| Currency isolation | benefit carries explicit ISO-4217; Contract-side check unchanged |
| No no-op or duplicated test cases | 69 distinct tests, no duplicate method names |

---

## 11. Files changed in this correction

```text
src/Centerix.Domain/Platform/Promotions/Promotion.cs
src/Centerix.Domain/Platform/Promotions/PromotionErrors.cs
src/Centerix.Domain/Platform/Promotions/PromotionCalculationService.cs
src/Centerix.Application/Platform/Promotions/BenefitEligibilityRuleParser.cs          (new)
src/Centerix.Application/Platform/Promotions/Commands/CreatePromotionCommand.cs
src/Centerix.Application/Platform/Promotions/Commands/UpdatePromotionCommand.cs
src/Centerix.Application/Platform/Promotions/PromotionDto.cs
src/Centerix.Application/Platform/Promotions/Queries/PromotionQueries.cs
src/Centerix.Infrastructure/Data/Configurations/PromotionConfiguration.cs
src/Centerix.Infrastructure/Data/Migrations/20261003160538_AddPromotionBenefitEligibilityRule.cs
src/Centerix.Infrastructure/Data/Migrations/20261003160538_AddPromotionBenefitEligibilityRule.Designer.cs
src/Centerix.Infrastructure/Data/Migrations/AppDbContextModelSnapshot.cs
tests/Centerix.SecurityTests/Task9_PromotionBenefitsDomainTests.cs
tests/Centerix.SecurityTests/Task9_PromotionBenefitsApplicationTests.cs
tests/Centerix.SecurityTests/Task9_PromotionBenefitsSqlServerTests.cs
tests/Centerix.SecurityTests/Task9C_PromotionBenefitRuleDomainTests.cs              (new)
tests/Centerix.SecurityTests/Task9C_PromotionBenefitRuleApplicationTests.cs         (new)
tests/Centerix.SecurityTests/Task9C_PromotionBenefitRuleSqlServerTests.cs           (new)
docs/TASK-9-PROMOTION-FREE-MONTHS-AND-BENEFITS-VERIFICATION-REPORT.md
```

---

## 12. Commit

```text
Commit:  fix(commerce): make promotion benefit eligibility rule configured data
```

---

## 13. Canonical enforcement of `BenefitEligibilityRule` (final correction)

`EligibilityRuleSerializer.Deserialize` accepts any *semantically equivalent* payload and silently
drops properties it does not know. That means canonicality cannot be inferred from a successful
deserialization.

`BenefitEligibilityRuleParser` — the single boundary shared by `CreatePromotionHandler` and
`UpdatePromotionHandler` — now requires the input to be byte-identical to the canonical
serialization of the rule it deserializes to:

```csharp
var rule      = EligibilityRuleSerializer.Deserialize(json);
var canonical = EligibilityRuleSerializer.Serialize(rule);
if (!string.Equals(json, canonical, StringComparison.Ordinal))
    return Error.Validation("Promotion.BenefitEligibilityRule_Invalid", …);
```

| Input | Result |
|---|---|
| `{"type":"contract_active"}` | **accepted** (exactly canonical) |
| `{"type":"contract_active","extra":"ignored"}` | **rejected** — deserializes fine, canonical form differs |
| `{"type":"all_of","rules":[…]}` with any other spacing, member order, or nested extra property | **rejected** |
| `{"type":"unknown_rule"}` | rejected (unknown discriminator) |
| `$type` / `assembly` / `typeName` / `clrType` payloads | rejected — the closed algebra never resolves CLR types |

No semantic normalization is performed, so arbitrary rule JSON is never quietly rewritten into
canonical JSON. The single error code `Promotion.BenefitEligibilityRule_Invalid` covers malformed,
unknown-discriminator and non-canonical input for this boundary; the message names the specific
cause. `Promotion.BenefitEligibilityRule` still stores canonical JSON in the same `nvarchar` column
— no schema change, no new migration, and the database representation is unchanged.

Verification counts for this correction: domain 29/29, application 28/28, SQL Server 27/27,
Task 9 total 84/84, full regression 2006 total / 2005 passed / 0 failed / 1 skipped / exit code 0.

---

**TASK 9 CORRECTION — READY FOR REVIEW**
