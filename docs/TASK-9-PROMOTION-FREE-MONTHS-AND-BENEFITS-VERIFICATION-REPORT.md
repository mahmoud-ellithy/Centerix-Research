# TASK 9 IMPLEMENTATION — PROMOTION FREE MONTHS & ADDITIONAL BENEFITS — READY FOR REVIEW

**TASK 9 IMPLEMENTATION — READY FOR REVIEW**

This document reports the implementation and verification of Task 9: a Promotion can now grant
**free months** or an **additional benefit/gift** on top of the purchased subscription. The grant is
recorded as a real, immutable Offer snapshot child and flows through the existing Offer → Contract
conversion into a real `FreeMonthsBenefit` / `ContractBenefit`.

---

## 1. API surface

### 1.1 Promotion types

```csharp
public enum PromotionType : byte
{
    PercentageDiscount  = 0,   // unchanged
    FixedAmountDiscount = 1,   // unchanged
    PayForXMonths       = 2,   // unchanged
    PromotionalPrice    = 3,   // unchanged
    FreeMonthsBonus     = 4,   // NEW
    AdditionalBenefits  = 5    // NEW
}
```

Existing member values are untouched. `FreeMonthsBonus` and `AdditionalBenefits` are
**entitlement-only** promotions: the customer pays the full amount and receives an extra
entitlement. They never change the charged amount and never stack with a discount.

### 1.2 Promotion benefit configuration (all optional, all nullable)

| Field | Type | Meaning |
|---|---|---|
| `FreeMonthsCount` | `int?` | Free months granted. Required for `FreeMonthsBonus`. |
| `BenefitName` | `string?` | Benefit name. Required for `AdditionalBenefits`. |
| `BenefitDescription` | `string?` | Optional benefit description. |
| `BenefitValue` | `decimal?` | Contractual value. Required, strictly positive. |
| `BenefitType` | `ContractBenefitType?` | Benefit category. Required. |
| `BenefitCurrencyCode` | `string?` | ISO-4217, 3 letters. Required. |

Wired through `CreatePromotionCommand`, `UpdatePromotionCommand`, `PromotionDto`,
`ListPromotionsQuery` / `GetPromotionByIdQuery` and the create/update audit payloads. The
controller binds the commands directly, so no controller change was required.

### 1.3 Validation rules (domain, `Promotion.ValidateBenefitFields`)

| Code | Condition |
|---|---|
| `Promotion.FreeMonthsCount_Invalid` | `FreeMonthsCount` missing or `<= 0` for `FreeMonthsBonus` |
| `Promotion.BenefitName_Required` | `AdditionalBenefits` without a name |
| `Promotion.BenefitValue_Invalid` | value missing or `<= 0` |
| `Promotion.BenefitType_Invalid` | benefit type missing or undefined |
| `Promotion.BenefitCurrencyCode_Invalid` | currency missing or not 3 letters |
| `Promotion.BenefitConfig_Conflicting` | free months **and** a benefit configured together |
| `Promotion.BenefitConfig_NotSupportedForType` | benefit config on a discount-only type |

All are `ErrorKind.Validation` and surface through the existing error pipeline.

### 1.4 Calculation-time validation

| Code | Condition |
|---|---|
| `Promotion.BenefitValue_ExceedsMaximum` | benefit value > `3 × Plan.MonthlyPrice` |
| `Promotion.FreeMonths_ExceedDuration` | free months > the purchased term (e.g. 5 free months on a 3-month contract) |

---

## 2. Authoritative semantics

### 2.1 The grant is independent of the discount

The calculated offer carries the entitlement on dedicated fields. `FreeMonths`, `BenefitName`,
`BenefitValue`, `BenefitType`, `BenefitCurrencyCode` and `BenefitDescription` are **never**
derived from:

- `Plan.BonusMonths` — plan bonus months remain contract-period data only (T9-D12, T9-A06, SQL-T9-07)
- `ChargedMonths` — except for the explicit `PayForXMonths` opt-in described in 2.3
- any discount, `BaseAmount`, `FinalAmount`, or runtime/environment value

### 2.2 The rule is built from the offer, never from the current Plan

`CalculatedOffer.EntitlementEligibilityRule` is a pure function of the calculated offer:

- benefit promotions → `AllOf(ContractActive, AmountPaidAtLeast(FinalAmount))`
- free months promotions → `ContractActive`

It is snapshotted onto the Offer child row. Contract creation reads the Offer snapshot only, so a
later Plan change cannot alter a granted entitlement (T9-A03, SQL-T9-06).

### 2.3 PayForXMonths

`PayForXMonths` keeps its exact existing commercial behaviour by default. When the promotion is
explicitly configured with `FreeMonthsCount`, the granted entitlement is the **real difference**
`DurationMonths − ChargedMonths` — not the configured counter. The configuration therefore acts as
an opt-in switch and can never contradict the "pay X get Y" promise:

- `PayForXMonths(12, charged 10)` with no `FreeMonthsCount` → no free months (T9-D16)
- `PayForXMonths(12, charged 10)` with `FreeMonthsCount` set → 2 free months (T9-D15)

### 2.4 Contract-side invariants (unchanged, reused)

- Gift value cap: total benefits ≤ 3 × contractual monthly value (`Contract.AddBenefit`).
  Promotions above the cap are rejected at calculation time so the Offer can never be converted into
  a contract that violates the invariant.
- Currency: the benefit carries its own ISO-4217 currency; the Contract-side currency check is
  unchanged.

---

## 3. Data model

### 3.1 Migration

`20261003145517_AddPromotionBenefitConfiguration` — **schema-only and nullable-additive**:

```text
Platform.Promotions.FreeMonthsCount        int            NULL
Platform.Promotions.BenefitName            nvarchar(200)  NULL
Platform.Promotions.BenefitDescription     nvarchar(500)  NULL
Platform.Promotions.BenefitValue           decimal(18,2)  NULL
Platform.Promotions.BenefitType            tinyint        NULL
Platform.Promotions.BenefitCurrencyCode    nvarchar(3)    NULL
```

No existing column was altered, renamed, retyped or dropped, and **no historical promotion row was
backfilled**. Existing promotions keep their exact previous behaviour.

### 3.2 No new entity types

No new aggregate, entity, DbSet or table was introduced. The granted entitlement reuses the
existing `OfferFreeMonthsBenefit` and `OfferBenefit` snapshots, and on the contract side the
existing `FreeMonthsBenefit` and `ContractBenefit`.

---

## 4. Tests

### 4.1 Domain — `Task9_PromotionBenefitsDomainTests` (18 tests, pure domain, no DB)

| ID | Guarantees |
|---|---|
| T9-D01 | `FreeMonthsBonus` grants months and leaves Base/Discount/Final untouched |
| T9-D02 | `AdditionalBenefits` carries name, description, value, type, currency; amount untouched |
| T9-D03 | The four original promotion types still calculate and grant no entitlement |
| T9-D04 | `FreeMonthsBonus` without `FreeMonthsCount` rejected |
| T9-D05 | `AdditionalBenefits` without name rejected |
| T9-D06 | `AdditionalBenefits` without value rejected |
| T9-D07 | Negative benefit value rejected |
| T9-D08 | Invalid benefit currency rejected |
| T9-D09 | Mixed free months + benefit rejected |
| T9-D10 | Benefit value above `3 × monthly` rejected; exactly at the cap accepted |
| T9-D11 | 5 free months on a 3-month contract rejected; equal to the term accepted |
| T9-D12 | `Plan.BonusMonths` never leaks into a promotion entitlement |
| T9-D13 | Discount and entitlement are independent axes |
| T9-D14 | The rule is present, deterministic and offer-derived |
| T9-D15 | `PayForXMonths` opt-in grants the real difference (2 months), not the configured counter |
| T9-D16 | `PayForXMonths` without opt-in grants nothing |
| T9-D17 | Draft and expired promotions grant nothing |

### 4.2 Application — `Task9_PromotionBenefitsApplicationTests` (10 tests, real handlers + MediatR)

| ID | Guarantees |
|---|---|
| T9-A01 | Free months promotion produces an Offer free months snapshot that survives persistence with its rule |
| T9-A02 | Additional benefit promotion produces an Offer benefit snapshot that survives persistence |
| T9-A03 | Editing the Promotion afterwards does not change the Offer snapshot |
| T9-A04 | Free months reach the Contract through the real accept → convert flow |
| T9-A05 | The additional benefit reaches the Contract through the real flow |
| T9-A06 | `Plan.BonusMonths` produces no Offer benefit rows |
| T9-A07 | Benefit above the cap is rejected with a `Validation` error and is not persisted |
| T9-A08 | Free months above the term are rejected |
| T9-A09 | Discount-only promotion produces zero benefits |
| T9-A10 | Disabled promotion produces zero benefits |

### 4.3 SQL Server — `Task9_PromotionBenefitsSqlServerTests` (12 tests, real SQL Server)

| ID | Guarantees |
|---|---|
| SQL-T9-01 | The six new nullable columns exist with the expected types/lengths after migration |
| SQL-T9-02 | Free months persist as a real `OfferFreeMonthsBenefit` row (and no benefit row) |
| SQL-T9-03 | The benefit persists as a real `OfferBenefit` row with all fields |
| SQL-T9-04 | Free months reach the Contract through the real production flow |
| SQL-T9-05 | The benefit reaches the Contract through the real production flow |
| SQL-T9-06 | Editing the Promotion never rewrites an existing Offer; a new calculation picks up the new value |
| SQL-T9-07 | `Plan.BonusMonths` creates zero benefit rows but is still carried as period data |
| SQL-T9-08 | Cap violation is rejected and zero Offer rows are persisted |
| SQL-T9-09 | Term violation is rejected and zero Offer rows are persisted |
| SQL-T9-10 | Discount-only promotion persists zero benefit rows |
| SQL-T9-11 | Disabled promotion persists zero benefit rows |
| SQL-T9-12 | The eligibility rule round-trips through SQL Server and carries no CLR metadata |

Each SQL test seeds its own tenant and its own plan, and scopes the promotion to that plan, so no
test can observe another test's promotions.

---

## 5. Verification run

### Build

```text
dotnet build Centerix.slnx --no-restore
→ 0 Errors
```

### Task 9 suites

| Suite | Total | Passed | Failed | Skipped | Not Executed | Exit Code |
|---|---|---|---|---|---|---|
| `Task9_PromotionBenefitsDomainTests` | 18 | 18 | 0 | 0 | 0 | 0 |
| `Task9_PromotionBenefitsApplicationTests` | 10 | 10 | 0 | 0 | 0 | 0 |
| `Task9_PromotionBenefitsSqlServerTests` | 12 | 12 | 0 | 0 | 0 | 0 |
| **Task 9 total** | **40** | **40** | **0** | **0** | **0** | **0** |

### Full regression

| Metric | Value |
|---|---|
| Total | 1962 |
| Passed | 1961 |
| Failed | 0 |
| Skipped | 1 |
| Not Executed | 0 |
| Exit Code | 0 |

Baseline before this change was 1922 total / 1921 passed / 1 skipped; the 40 new tests account for
the difference. The single skip is pre-existing and unrelated
(`Task18_5CreditEconomicOriginSqlServerTests.Test15_Task1851_MixedLineageProportionalTransferredOrigin`).
No infrastructure failure was reported as a skip.

---

## 6. SQL Server

```text
Server:        .            (local SQL Server, explicitly pinned via
                            CENTERIX_SQLTEST_CONNECTION="Server=.;Trusted_Connection=True;
                            TrustServerCertificate=True;Encrypt=False")
Docker:                  NOT USED
Testcontainers:          NOT USED
In-memory substitution:  used only for the T9-A* application suite, which is explicitly
                         an InMemory suite by design; every SQL guarantee (SQL-T9-*)
                         runs against real SQL Server
```

---

## 7. EF model status

```text
dotnet ef migrations has-pending-model-changes --context AppDbContext
→ No changes have been made to the model since the last migration.

dotnet ef migrations has-pending-model-changes --context TenantDbContext
→ No changes have been made to the model since the last migration.
```

`PendingModelChangesWarning` is not suppressed.

---

## 8. Regressions checked explicitly

| Guarantee | Result |
|---|---|
| The four original promotion types still work | T9-D03, T9-A09, SQL-T9-10 |
| `PromotionType` existing values unchanged | 0..3 untouched; 4 and 5 appended |
| No historical promotion row backfilled | migration is nullable-additive only |
| EligibilityStatus reversal untouched | no eligibility code changed |
| Fulfillment system untouched | no fulfillment code changed |
| No entity, DbSet or table added | only columns on the existing `Promotions` table |
| `Plan.BonusMonths` behaviour unchanged | T9-D12, T9-A06, SQL-T9-07 |
| `PaymentTerms` still an explicit operator decision | not derived from any promotion field |
| Tenant isolation | offers/benefits stamped and queried per tenant; SQL tests use isolated tenants |
| Currency isolation | benefit carries explicit ISO-4217; Contract-side check unchanged |

---

## 9. Files changed

```text
src/Centerix.Domain/Platform/Promotions/Enums/PromotionType.cs
src/Centerix.Domain/Platform/Promotions/Promotion.cs
src/Centerix.Domain/Platform/Promotions/PromotionErrors.cs
src/Centerix.Domain/Platform/Promotions/PromotionCalculationService.cs
src/Centerix.Domain/Platform/Promotions/CalculatedOffer.cs
src/Centerix.Application/Platform/Promotions/Commands/CalculateAndPersistOfferCommand.cs
src/Centerix.Application/Platform/Promotions/Commands/CreatePromotionCommand.cs
src/Centerix.Application/Platform/Promotions/Commands/UpdatePromotionCommand.cs
src/Centerix.Application/Platform/Promotions/OfferDto.cs
src/Centerix.Application/Platform/Promotions/PromotionDto.cs
src/Centerix.Application/Platform/Promotions/Queries/PromotionQueries.cs
src/Centerix.Infrastructure/Data/Configurations/PromotionConfiguration.cs
src/Centerix.Infrastructure/Data/Migrations/20261003145517_AddPromotionBenefitConfiguration.cs
src/Centerix.Infrastructure/Data/Migrations/20261003145517_AddPromotionBenefitConfiguration.Designer.cs
src/Centerix.Infrastructure/Data/Migrations/AppDbContextModelSnapshot.cs
tests/Centerix.SecurityTests/Task9_PromotionBenefitsDomainTests.cs
tests/Centerix.SecurityTests/Task9_PromotionBenefitsApplicationTests.cs
tests/Centerix.SecurityTests/Task9_PromotionBenefitsSqlServerTests.cs
docs/TASK-9-PROMOTION-FREE-MONTHS-AND-BENEFITS-VERIFICATION-REPORT.md
```

---

## 10. Commit

```text
Commit:  feat(commerce): add promotion free months and additional benefits
```

---

**TASK 9 IMPLEMENTATION — READY FOR REVIEW**
