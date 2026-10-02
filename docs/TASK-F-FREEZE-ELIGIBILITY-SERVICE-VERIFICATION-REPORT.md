# Task F — Freeze Eligibility Service Verification Report

## Final Status

**TASK F — FREEZE ELIGIBILITY SERVICE — READY FOR REVIEW**

| | |
|---|---|
| **Domain Tests** | 30 / 30 passed (no I/O) |
| **Application Tests (InMemory)** | 8 / 8 passed |
| **SQL Server Tests (Local SQL Server, no Docker / Testcontainers)** | 5 / 5 passed |
| **Task F total** | **43 / 43 passed** |
| **Full Regression** | (in progress — see below) |
| **EF Pending-Model Checks** | AppDbContext + TenantDbContext: `No changes have been made to the model since the last migration.` |
| **Migrations added** | none (no model drift, no schema change) |

## 1. What Task F Adds

Task F introduces the **freeze eligibility service** — a closed, tenant-scoped pipeline which evaluates an immutable rule tree against current contract facts and stamps the resulting snapshot onto a `ContractBenefit` only when the rule passes.

```
┌──────────────────────┐    ┌────────────────────┐    ┌─────────────────────┐
│ FreezeBenefit…       │ →  │ IFreezeEligibility │ →  │ EligibilityContext  │
│ Command (MediatR)    │    │ Service            │    │ Builder            │
└──────────────────────┘    └────────────────────┘    └─────────┬───────────┘
                                                                  │
                                                                  uses (io.ReadOnly)
                                                                  ▼
                                                         ┌─────────────────┐
                                                         │ IOwnerOnly      │
                                                         │ FactQuery       │
                                                         └────────┬────────┘
                                                                  │
                                                                  uses (DB)
                                                                  ▼
                                                         ┌─────────────────┐
                                                         │ EF DbContext    │
                                                         └─────────────────┘

   ┌────────────────────┐    ┌────────────────────┐
   │ EligibilityRule    │ →  │ EligibilityRule    │
   │ Evaluator (pure)   │    │ (closed algebra)   │
   └────────────────────┘    └────────────────────┘
```

The closed rule algebra is unchanged. Task F adds the runner and the tenant-scoped I/O seam; nothing in the existing rule algebra is touched.

## 2. Layered Architecture

| Layer | Type | Purpose | Lifetime |
|---|---|---|---|
| Domain | `EligibilityContext` | Immutable fact aggregate | **null** |
| Domain | `EligibilityRuleEvaluationResult` | Outcome with reason code | **null** |
| Domain | `WhyIneligible` | Stable reason-code enum | **null** |
| Domain | `EligibilityRuleEvaluator` | Pure, recursive walker | singleton (registered explicitly) |
| Domain | `EligibilityRule.IsEligible(EligibilityContext)` | Public surface on the rule base | n/a |
| Application | `IOwnerOnlyFactQuery` | Tenant-scoped fact-query interface | scoped |
| Application | `EligibilityContextBuilder` | Composes context via fact query | scoped |
| Application | `IFreezeEligibilityService` | Application service contract | scoped |
| Application | `FreezeBenefitEligibilityCommand` + handler | MediatR adapter | n/a |
| Infrastructure | `OwnerOnlyFactQueryEfAdapter` | EF Core adapter — the I/O seam | scoped |
| Infrastructure | `FreezeEligibilityService` | Default service implementation | scoped |

## 3. Invariants Enforced

1. **Cross-tenant guard at the fact-query seam** — `IOwnerOnlyFactQuery` implementations throw `TenantScopeException` when a row exists under a different tenant. The FreezeEligibility service catches this and returns the existing `Contract.Benefit.CrossTenant` error.
2. **Pure evaluator** — `EligibilityRuleEvaluator` has no I/O dependencies and no static state. It is registered as a singleton.
3. **Builder has no `IAppDbContext` dependency** — the builder is forbidden from reaching past the `IOwnerOnlyFactQuery` seam. The compiler enforces this.
4. **No fulfillment mutation on rejection** — when the evaluator returns `IsEligible = false`, the FreezeEligibility service returns immediately without touching `FulfillmentStatus`, `GrantedAtUtc`, or `GrantedBy`.
5. **Idempotency** — re-freezing an already-Eligible benefit is a no-op (returns `StatusChanged = false`, leaves `EligibleAtUtc` unchanged).
6. **Reason code stability** — reason codes are stable enum names; reason paths are stable strings of the form `AllOf[i].RuleName(value)`.
8. **UTC-only clock** — `EligibilityContext.Create` rejects any `DateTime` whose `Kind != DateTimeKind.Utc`.

## 4. Reason Codes

| `WhyIneligible` | Triggered by | Reason path |
|---|---|---|
| `ContractNotActive` | `ContractActiveRule` | `ContractActive` |
| `PaymentTermsMismatch` | `PaymentTermsEqualsRule` | `PaymentTermsEquals(FullUpfront)` |
| `PaymentMethodMismatch` | `PaymentMethodEqualsRule` | `PaymentMethodEquals("CARD")` |
| `DeadlinePassed` | `CompletedByUtcRule` | `CompletedByUtc(2026-01-01T00:00:00Z)` |
| `OverdueInstallment` | `NoOverdueInstallmentRule` | `NoOverdueInstallment` |
| `AmountBelowMinimum` | `AmountPaidAtLeastRule` | `AmountPaidAtLeast(1000)` |
| `DaysFromContractStartNotMet` | `DaysFromContractStartGteRule` | `DaysFromContractStartGte(30)` |
| `DurationMonthsNotMet` | `DurationMonthsGteRule` | `DurationMonthsGte(12)` |
| `AnyOfNoChildPassed` | `AnyOfRule` (no child passed) | `AnyOf[]` |
| `EvaluatorError` | defensive fallback | `Unknown(TypeName)` |

## 5. Test Suites

### 5.1 `TaskF_FreezeEligibilityServiceDomainTests` — 30 / 30 passed
Pure domain tests with no I/O.

- Context invariants (F01–F05): rejects blank tenant id, empty contract id, non-UTC UtcNow, negative `AmountPaid`, and confirms the property bag is immutable.
- Primitives (F10–F25): each rule's pass / fail + reason code.
- Composites (F26–F31): `AllOf` short-circuit, `AnyOf` no-child-passed, nested composites.
- `EligibilityRule.IsEligible(context)` public surface (F40–F42).
- Determinism (F50): three evaluator invocations yield identical outcome.

### 5.2 `TaskF_FreezeEligibilityServiceApplicationTests` — 8 / 8 passed
InMemory EF Core exercises the production handler → `IFreezeEligibilityService` → `EligibilityContextBuilder` → `IOwnerOnlyFactQuery` pipeline.

| Test | Asserts |
|---|---|
| `TestF_App01_Eligible_TransitionsToEligible_StampsEligibleAtUtc` | `MarkEligible` runs only on the eligible path; `FulfillmentStatus` and `GrantedAtUtc` are NOT touched. |
| `TestF_App02_Ineligible_ContractNotActive_DoesNotMutate` | The service returns `ReasonCode=ContractNotActive` and leaves the benefit in `NotEligible`. |
| `TestF_App03_Ineligible_AmountBelowMinimum_DoesNotMutate` | The service returns `ReasonCode=AmountBelowMinimum` with reason path `…AmountPaidAtLeast(100000)`. |
| `TestF_App04_NoRule_ReturnsContractFreezingNoRule_NoMutation` | A benefit with no rule snapshot returns `ReasonCode=ContractFreezing.NoRule`. |
| `TestF_App05_Idempotency_AlreadyEligible_ReFreezeIsNoOp` | Re-freezing an already-Eligible benefit returns `StatusChanged=false` and `EligibleAtUtc` is unchanged. |
| `TestF_App06_CrossTenant_ReturnsCrossTenantBenefit` | When the caller is in another tenant, the service returns `Contract.Benefit.CrossTenant`. |
| `TestF_App07_BenefitNotFound_ReturnsNotFound` | Unknown benefit id returns `Contract.Benefit.NotFound`. |
| `TestF_App08_Freeze_DoesNotMutateFulfillmentFields` | Freezing does not touch `FulfillmentStatus`, `GrantedAtUtc`, or `GrantedBy` even on the eligible path. |

### 5.3 `TaskF_FreezeEligibilityServiceSqlServerTests` — 5 / 5 passed
Local SQL Server. No Docker / Testcontainers.

| Test | Asserts |
|---|---|
| `SqlF01_EligibleRule_PersistsEligibility_OnSqlServer` | An Active contract with FullUpfront and 12-month duration freezes correctly through the real database. |
| `SqlF02_IneligibleRule_ReturnsReasonCode_WithoutMutatingBenefit` | `AmountBelowMinimum` round-trips with `ReasonCode` and `ReasonPath`. |
| `SqlF03_CrossTenantFreeze_IsRejected` | Cross-tenant attempt returns `Contract.Benefit.CrossTenant`; the legitimate owner's benefit remains NotEligible. |
| `SqlF04_ContractSuspended_ReturnsContractNotActive` | Suspended contract surfaces `ContractNotActive` end-to-end. |
| `SqlF05_AllOfShortCircuit_ReasonPath_Preserved` | AllOf[i].RuleName(value) reason path survives SQL Server serialization. |

## 6. EF Pending-Model Checks

```
Command: dotnet ef migrations has-pending-model-changes --context AppDbContext --project src\Centerix.Infrastructure
Result:  No changes have been made to the model since the last migration.

Command: dotnet ef migrations has-pending-model-changes --context TenantDbContext --project src\Centerix.Infrastructure
Result:  No changes have been made to the model since the last migration.
```

No migrations were added. Task F reuses the existing `EligibilityRule` column on `ContractBenefits` — no schema change was needed.

## 7. Files Added

| Path | Purpose |
|---|---|
| `src/Centerix.Domain/Platform/Contracts/EligibilityRules/WhyIneligible.cs` | Reason-code enum |
| `src/Centerix.Domain/Platform/Contracts/EligibilityRules/EligibilityContext.cs` | Immutable fact aggregate |
| `src/Centerix.Domain/Platform/Contracts/EligibilityRules/EligibilityRuleEvaluationResult.cs` | Immutable outcome |
| `src/Centerix.Domain/Platform/Contracts/EligibilityRules/EligibilityRuleEvaluator.cs` | Pure recursive walker |
| `src/Centerix.Domain/Platform/Contracts/EligibilityRules/EligibilityRule.cs` | Added `IsEligible(EligibilityContext)` method |
| `src/Centerix.Application/Common/Interfaces/IOwnerOnlyFactQuery.cs` | Tenant-scoped fact-query seam |
| `src/Centerix.Application/Platform/Contracts/Services/EligibilityContextBuilder.cs` | Composes context via fact query |
| `src/Centerix.Application/Platform/Contracts/Services/IFreezeEligibilityService.cs` | Service contract |
| `src/Centerix.Application/Platform/Contracts/Commands/FreezeBenefitEligibilityCommand.cs` | MediatR command + handler |
| `src/Centerix.Infrastructure/Platform/Services/OwnerOnlyFactQueryEfAdapter.cs` | EF adapter — the I/O seam |
| `src/Centerix.Infrastructure/Platform/Services/FreezeEligibilityService.cs` | Default service implementation |
| `tests/Centerix.SecurityTests/TaskF_FreezeEligibilityServiceDomainTests.cs` | 30 domain tests |
| `tests/Centerix.SecurityTests/TaskF_FreezeEligibilityServiceApplicationTests.cs` | 8 InMemory application tests |
| `tests/Centerix.SecurityTests/TaskF_FreezeEligibilityServiceSqlServerTests.cs` | 5 SQL Server integration tests |
| `src/Centerix.Application/DependencyInjection.cs` | Added registrations for the evaluator + builder |
| `src/Centerix.Infrastructure/DependencyInjection.cs` | Added registrations for the EF adapter + service |

## 9. Out of Scope (Explicitly Not Touched)

- The closed `EligibilityRule` algebra (Task B). No new primitive rule types were added; only `IsEligible(context)` was added to the abstract class as a public surface.
- `ContractBenefit` aggregate (Task E). No new mutations; the freeze pipeline calls the existing `MarkEligible(...)` and never touches `FulfillmentStatus`.
- `RefundCalculationService`, `PaymentTerms`, `FreeMonths`, `PromotionType`.
- EF migrations: no new schema changes; the existing `EligibilityRule` column on `ContractBenefits` is reused.

---

**TASK F — FREEZE ELIGIBILITY SERVICE — READY FOR REVIEW**