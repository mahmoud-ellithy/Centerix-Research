# TASK B.2 — SQL SERVER VERIFICATION REPORT

## Commit

`4e34087466a77d1d4f71b04d61c4c5b4d9953ac2`

(`fix(billing): verify eligibility snapshot through production flow`)

## Build

- Command: `dotnet build Centerix.slnx --no-restore`
- Result: Succeeded
- Errors: 0
- Warnings: 0
- Exit Code: 0

## Task B Tests

- Command: `dotnet test tests/Centerix.SecurityTests/Centerix.SecurityTests.csproj --no-build --verbosity normal --filter "FullyQualifiedName~TaskB_EligibilityRuleFoundationTests"`
- Class: `Centerix.SecurityTests.TaskB_EligibilityRuleFoundationTests`
- Total: 55
- Passed: 55
- Failed: 0
- Skipped: 0
- Duration: 3.25 s

## SQL Server Tests

- Command: `dotnet test tests/Centerix.SecurityTests/Centerix.SecurityTests.csproj --no-build --logger "trx;LogFileName=$env:TEMP\testresults\b2_final2.trx" --verbosity normal --filter "FullyQualifiedName~TaskB_2_EligibilityRuleSqlServerTests"`
- Environment: Local SQL Server (fixture probed `Server=.`; no `CENTERIX_SQLTEST_CONNECTION` was supplied; Testcontainers was NOT started)
- Server: `Server=.;Trusted_Connection=True;TrustServerCertificate=True;Encrypt=False;Connect Timeout=5`
- Database: `CenterixSec_924795582e7f4b0ba819658f4332693f` (created and dropped per run by the fixture; 54 AppDbContext migrations applied)
- Total: 9
- Passed: 9
- Failed: 0
- Skipped: 0
- Duration: 20.47 s

Per-test execution evidence (TRX `b2_final2.trx`, `outcome="Passed"`):

| Test | Duration |
|------|----------|
| Sql01_PaymentMethod_Canonicalisation_SurvivesDatabaseRoundTrip | 151 ms |
| Sql02_AllOf_CompositeRule_RoundTripsExactly | 69 ms |
| Sql03_AnyOf_CompositeRule_RoundTripsExactly | 84 ms |
| Sql04_AllPrimitiveRules_RoundTripIndividually | 104 ms |
| Sql05_OfferToContract_PreservesEligibilityRuleAcrossSnapshot_ThroughProductionFlow | 993 ms |
| Sql06_NullEligibilityRule_PersistsAndReloads | 194 ms |
| Sql07_EligibilityRuleColumn_MatchesConfiguredSchema(OfferBenefits) | 1 s |
| Sql07_EligibilityRuleColumn_MatchesConfiguredSchema(ContractBenefits) | 201 ms |
| Sql08_PersistedJson_DoesNotContainClrOrExecutableMetadata | 63 ms |

Live fixture log captured during the run:

```text
[SqlServerFixture] Probing local SQL Server (Server=.)...
[SqlServerFixture] Local SQL Server reachable.
[SqlServerFixture] Using master connection:
  Server=.;Trusted_Connection=True;TrustServerCertificate=True;Encrypt=False;Connect Timeout=5
[SqlServerFixture] Created database CenterixSec_924795582e7f4b0ba819658f4332693f
[SqlServerFixture] Using database CenterixSec_924795582e7f4b0ba819658f4332693f
[SqlServerFixture] Test host built.
[SqlServerFixture] TenantDbContext migrated.
[SqlServerFixture] AppDbContext CanConnect=True
[SqlServerFixture] 54 pending AppDbContext migrations.
[SqlServerFixture] AppDbContext migrated.
```

## Critical Production Flow

```text
Offer
  ↓ Persist OfferBenefit with EligibilityRule through offer.AddBenefit(...)
  ↓ Real production AcceptOfferHandler       (Centerix.Application/Platform/Promotions/Commands/AcceptOfferCommand.cs)
  ↓ Real production CreateContractFromOfferHandler  (Centerix.Application/Platform/Promotions/Commands/CreateContractFromOfferCommand.cs)
  ↓ ContractBenefit (created by handler; EligibilityRule snapshotted from offerBenefit)
  ↓ SQL Server persistence (nvarchar(4000), nullable, no default)
  ↓ Fresh DI scope + fresh DbContext reload (IgnoreQueryFilters + AsNoTracking)
  ↓ Assertions:
     - source EligibilityRule == persisted ContractBenefit.EligibilityRule
     - Serialize(source) == Serialize(persisted)
     - Offer.Status == ConvertedToContract
     - Offer.ContractId == handlerResult.Value
```

Result: **PASS** — `Sql05_OfferToContract_PreservesEligibilityRuleAcrossSnapshot_ThroughProductionFlow` passed in 993 ms against Local SQL Server. The test does NOT manually construct `ContractBenefit`; it relies entirely on the production handler, then reloads through a fresh DbContext and asserts the equality and serialization invariants.

## EF Verification

- Command: `dotnet ef migrations has-pending-model-changes --project src/Centerix.Infrastructure --startup-project src/Centerix.API --context AppDbContext`
- Output: `No changes have been made to the model since the last migration.`
- Pending model changes: **NO**

Live SQL Server schema (verified via `Sql07_EligibilityRuleColumn_MatchesConfiguredSchema` against the live database):

```text
Platform.OfferBenefits.EligibilityRule   nvarchar(4000)  NULL  (0 default constraints)
Platform.ContractBenefits.EligibilityRule nvarchar(4000)  NULL  (0 default constraints)
```

## Full Regression

- Command: `dotnet test Centerix.slnx --no-build --logger "trx;LogFileName=$env:TEMP\testresults\full_final2.trx" --verbosity normal`
- Total: 1684
- Passed: 1683
- Failed: 0
- Skipped: 1
- Duration: 13.11 min (786.34 s)
- Exit Code: 0

## Git Hygiene

- `git status --short`: empty (no uncommitted changes)
- `git diff`: empty (no working-tree modifications)
- Clean: **YES**

The verification task itself introduced **zero** changes to the repository.

## Skips / Problems

```text
Pre-existing intentional skip (unrelated to Task B):
  Test:    Centerix.SecurityTests.Task18_5CreditEconomicOriginSqlServerTests
           .Test15_Task1851_MixedLineageProportionalTransferredOrigin
  Reason:  [Fact(Skip = "Complex overlapping subscription scenario - covered by Test16 and other tests")]
  Source:  tests/Centerix.SecurityTests/Task18_5CreditEconomicOriginSqlServerTests.cs:1172
  Impact:  None on Task B / EligibilityRule foundation.

Failures:        0
Defects found:   0
Workarounds:     0
```

## Final Status

**TASK B.2 — VERIFIED AND CLOSED**