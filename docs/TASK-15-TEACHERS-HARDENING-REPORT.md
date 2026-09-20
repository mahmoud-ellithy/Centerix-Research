# Task 15 — Teachers Module Production Hardening Report

## Summary

| Item | Status |
|------|--------|
| H-01 Feature Gating | ALREADY FIXED — verified |
| H-02 SalaryPayment State Machine | ALREADY FIXED — verified |
| H-03 SalaryPayment Concurrency (RowVersion) | ALREADY FIXED — verified |
| H-04 Soft-Delete Filter | ALREADY FIXED — verified |
| F-05 Teacher UserId Membership Check | FIXED — handler + validator |
| F-10 EffectiveFrom Default Guard | FIXED — domain model |
| F-14 NetAmount > GrossAmount Guard | FIXED — domain + validator |
| Test Coverage | 23 new tests |
| Full Regression | **1279 passed, 0 failed** |

## Findings Detail

### H-01 — Feature Gating ✅ ALREADY FIXED

All four Teachers mutation endpoints (`POST`, `PUT`, `DELETE` for teachers and salary payments)
already carry `[RequireFeature(FeatureCodes.TeacherManagement)]`. Verified in:
- `TeachersController.cs` lines 43, 52, 61, 115, 131, 147

### H-02 — SalaryPayment State Machine ✅ ALREADY FIXED

- `Create()` no longer accepts a `Status` parameter — always creates `Pending`.
- `MarkPaid()` blocks `Paid→Paid` and `Cancelled→Paid`.
- `Cancel()` blocks `Paid→Cancel`.

Verified in `SalaryPayment.cs` lines 67–100.

### H-03 — SalaryPayment Concurrency (RowVersion) ✅ ALREADY FIXED

- `[Timestamp] public byte[] RowVersion { get; private set; }` on `SalaryPayment`.
- `IsRowVersion()` configured in `SalaryPaymentConfiguration.cs`.
- Migration `202603130000_AddSalaryPaymentRowVersion` exists.

### H-04 — Soft-Delete Filter ✅ ALREADY FIXED

`ApplySoftDeleteFilterFor<T>()` correctly composes:
```csharp
e.TenantId == _currentTenant.TenantId && e.DeletedAtUtc == null
```

### F-05 — Teacher UserId Membership Check ✅ FIXED

**Problem:** `CreateTeacherHandler` and `UpdateTeacherHandler` accepted any `UserId` without
verifying the user holds an active `TenantMembership` for the current tenant. A cross-tenant
user ID could be injected.

**Fix:**
- `CreateTeacherHandler.cs`: Added `TenantMemberships` lookup — verifies `m.UserId == request.UserId && m.TenantId == currentTenant.TenantId && m.Status == Active` before proceeding. On failure, releases the reserved limit slot and returns `TeacherErrors.UserNotInTenant`.
- `UpdateTeacherHandler.cs`: Same membership check added.
- `TeacherErrors.cs`: Added `UserNotInTenant` error constant.

### F-10 — EffectiveFrom Default Guard ✅ FIXED

**Problem:** `TeacherSalaryConfig.Create()` accepted `default(DateOnly)` (0001-01-01) as a
valid effective date, which would match any historical query.

**Fix:** Added guard at `TeacherSalaryConfig.cs` line 46:
```csharp
if (effectiveFrom == default) return TeacherSalaryConfigErrors.EffectiveFromRequired;
```

### F-14 — NetAmount > GrossAmount Guard ✅ FIXED

**Problem:** `SalaryPayment.Create()` allowed `netAmount > grossAmount`, which is financially
invalid.

**Fix:**
- `SalaryPayment.cs`: Added `if (netAmount > grossAmount) return SalaryPaymentErrors.NetExceedsGross;`
- `CreateSalaryPaymentValidator.cs`: Added `.LessThanOrEqualTo(x => x.GrossAmount)` rule on `NetAmount`.

## Test Results

### New Task15 Tests (23 total)

| Test | Finding | Status |
|------|---------|--------|
| `StateMachine_PendingToPaid_Succeeds` | H-02 | ✅ |
| `StateMachine_PaidToPaid_Fails` | H-02 | ✅ |
| `StateMachine_CancelledToPaid_Fails` | H-02 | ✅ |
| `StateMachine_CancelPendingPayment_Succeeds` | H-02 | ✅ |
| `StateMachine_AlreadyPaid_CancelFails` | H-02 | ✅ |
| `StateMachine_AlreadyCancelled_CancelFails` | H-02 | ✅ |
| `Create_PendingPayment_Succeeds` | H-02 | ✅ |
| `Create_EmptyTeacherId_Fails` | H-02 | ✅ |
| `TeacherSalaryConfig_Create_ValidEffectiveFrom_Succeeds` | F-10 | ✅ |
| `TeacherSalaryConfig_Create_DefaultEffectiveFrom_Fails` | F-10 | ✅ |
| `TeacherSalaryConfig_Create_PercentageOver100_Fails` | F-10 | ✅ |
| `TeacherSalaryConfig_Create_PercentageZeroOrNegative_Fails` | F-10 | ✅ |
| `CreateTeacherHandler_ForeignUser_ReturnsUserNotInTenant` | F-05 | ✅ |
| `UpdateTeacherHandler_ForeignUser_ReturnsUserNotInTenant` | F-05 | ✅ |
| `CreateTeacherHandler_ValidMember_Succeeds` | F-05 | ✅ |
| `SalaryPayment_Create_NetExceedsGross_Fails` | F-14 | ✅ |
| `SalaryPayment_Create_NetEqualsGross_Succeeds` | F-14 | ✅ |
| `SalaryPayment_Create_ValidSucceeds` | F-14 | ✅ |
| `SoftDeletedTeacher_IsNotVisibleInList` | H-04 | ✅ |
| `SoftDeletedTeacher_ExistenceCheck_ReturnsFalse` | H-04 | ✅ |
| `ActiveTeacher_IsVisibleInList` | H-04 | ✅ |
| `DeletedTeacher_IsExcludedFromFilter` | H-04 | ✅ |
| `CrossTenantTeacher_IsNotVisible` | H-04 | ✅ |

### Side-Effect Fix

`Phase5TeachersAuthorizationHttpTests.Teacher_Update_WithFeature_Succeeds` was failing because
F-05's new membership check rejected the random `userId` used in the update. Fixed by adding
`EnsureTenantMembershipForUserAsync()` helper and creating a membership before the update call.

## Files Modified

| File | Change |
|------|--------|
| `src/.../CreateTeacherCommand.cs` | Added TenantMemberships check in handler |
| `src/.../UpdateTeacherCommand.cs` | Added TenantMemberships check in handler |
| `src/.../TeacherErrors.cs` | Added `UserNotInTenant` error |
| `src/.../TeacherSalaryConfig.cs` | Added `EffectiveFrom == default` guard |
| `src/.../SalaryPayment.cs` | Added `NetAmount > GrossAmount` guard |
| `src/.../SalaryPaymentErrors.cs` | Added `NetExceedsGross` error |
| `src/.../SalaryPaymentCommands.cs` | Added FluentValidation `NetAmount ≤ GrossAmount` |
| `tests/.../Task15TeachersHardeningTests.cs` | New test file (23 tests) |
| `tests/.../Phase5TeachersAuthorizationHttpTests.cs` | Fixed side-effect from F-05 |

## Task 15.1 — Verification Gap Closure

### H-01 — HTTP Feature-Gating Matrix (32 tests, 32/32 pass)

Tests across 8 endpoints × 4 cases (FeaturePresent→Allowed, FeatureMissing→403, FeatureExpired→403, PermissionMissing→403):

| Endpoint | FeaturePresent | FeatureMissing | FeatureExpired | PermissionMissing |
|----------|---------------|---------------|---------------|-------------------|
| Teacher PUT | ✅ 204 | ✅ 403 | ✅ 403 | ✅ 403 |
| Teacher DELETE | ✅ 204 | ✅ 403 | ✅ 403 | ✅ 403 |
| Subject PUT | ✅ 204 | ✅ 403 | ✅ 403 | ✅ 403 |
| Subject DELETE | ✅ 204 | ✅ 403 | ✅ 403 | ✅ 403 |
| TeacherSalaryConfig PUT | ✅ 204 | ✅ 403 | ✅ 403 | ✅ 403 |
| TeacherSalaryConfig DELETE | ✅ 204 | ✅ 403 | ✅ 403 | ✅ 403 |
| SalaryPayment MarkPaid | ✅ 204 | ✅ 403 | ✅ 403 | ✅ 403 |
| SalaryPayment Cancel | ✅ 204 | ✅ 403 | ✅ 403 | ✅ 403 |

File: `tests/.../Task15_1FeatureGatingHttpTests.cs`

**Test fixes applied:**
- TeacherSalaryConfig URL: corrected `teacherssalaryconfigs` → `teachersalaryconfigs` (route mismatch)
- Subject AcademicStage seed: changed from hardcoded Id=1 (duplicate key across tests) to auto-generated Id per tenant

### H-04 — Shared Soft-Delete Regression (10 tests, 10/10 pass)

| Entity | NotVisibleInList | GetById_ReturnsNull | Any_ReturnsFalse |
|--------|-----------------|--------------------|-----------------|
| Teacher | ✅ | ✅ | ✅ |
| Student | ✅ | ✅ | ✅ |
| Branch | ✅ | ✅ | ✅ |
| Cross-Tenant Soft-Delete Combined | ✅ | | |

File: `tests/.../Task15_1SoftDeleteRegressionTests.cs`

### H-03 — SQL Server Concurrency (2 tests, Docker required — not run)

- `SalaryPayment_SequentialMarkPaidVsCancel_ExactlyOneWins`
- `SalaryPayment_ParallelCancelVsCancel_ExactlyOneWins`

Uses Testcontainers SQL Server via `SqlServerIntegrationFactory`. Requires Docker — could not execute in current environment.

File: `tests/.../Task15_1ConcurrencySqlServerTests.cs`

### EF Model Changes Check

```
No changes have been made to the model since the last migration.
```

## Ambiguous Business Rules (NOT implemented — documented as UNKNOWN)

1. **Effective-date overlap**: No duplicate-prevention logic exists for overlapping `EffectiveFrom` ranges on `TeacherSalaryConfig`. Business rule unclear.
2. **Rating duplicates**: No constraint prevents multiple ratings for the same teacher+period. Business rule unclear.
3. **GroupId FK**: `Teacher.GroupId` is nullable with no FK enforcement. Business rule unclear.
