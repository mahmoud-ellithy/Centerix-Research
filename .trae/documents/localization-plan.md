# Localization Plan — Result/Error Messages & Enum Labels

## Summary

Translate all domain error messages and enum labels to the current user's language (detected from `Accept-Language` HTTP header), using per-language JSON files as the translation store (ABP-style). Error descriptions are localized at the single choke point `ApiController.Problem()`, and enum values get new localized label fields in DTOs alongside their existing raw values.

## Current State

- **Error messages** are hardcoded English strings in domain error catalogs (`FeatureErrors.cs`, `PlanErrors.cs`, etc.) and in `Infrastructure/Platform/PlatformService.cs` (Notfound errors). They flow through `Result<T>` → `ApiController.Problem()` where `error.Description` is written to ProblemDetails `title` and `detail`.
- **Enums** (`BillingStatus`, `SubscriptionStatus`, `LeadStage`) are exposed as raw `byte` or `string` in DTOs. No display-name/label exists.
- **Culture resolution**: `AddLocalization()` is called in `Program.cs` but `UseRequestLocalization()` is never called. No `.resx` files exist. No culture is set per request.
- **No JSON localization files** exist yet.

## Decisions (confirmed with user)

| Decision | Choice |
|----------|--------|
| Languages | Arabic + English, detected from `Accept-Language` header |
| Translation storage | JSON dictionary files (ABP-style) |
| Enum output | Add localized label field alongside raw value (non-breaking) |
| Scope | All Result error messages + enum values in DTOs |

## Proposed Changes

### Step 1 — Create JSON localization files

**New files:**
- `src/Centerix.API/Localization/en.json` — English translations (same as current English, kept as base)
- `src/Centerix.API/Localization/ar.json` — Arabic translations

**Structure:**
```json
{
  "Feature.Code_Required": "Feature code is required",
  "Feature.Description_Required": "Feature description is required",
  "Feature.Module_Required": "Module is required",
  "Plan.Code_Required": "Plan code is required",
  "Plan.DisplayName_Required": "Display name is required",
  "Plan.InvalidPrice": "Monthly price must be greater than or equal to zero",
  "Plan.InvalidLimits": "Limits must be greater than or equal to zero",
  "Plan.AlreadyDeactivated": "Plan is already deactivated",
  "Plan.AlreadyActive": "Plan is already active",
  "Lead.CenterName_Required": "Center name is required",
  "Lead.ContactName_Required": "Contact name is required",
  "Lead.Phone_Required": "Phone number is required",
  "Lead.Source_Required": "Source is required",
  "Lead.Stage_Required": "Stage is required",
  "Lead.InvalidStageTransition": "The requested stage transition is not allowed",
  "Lead.InvalidPhoneNumber": "Phone number must be 7-15 digits and may start with '+'",
  "Billing.PlanId_Required": "Plan ID is required",
  "Billing.Amount_Required": "Amount is required",
  "Billing.InvalidAmount": "Amount must be greater than zero",
  "Billing.Method_Required": "Payment method is required",
  "Billing.AlreadyPaid": "This invoice has already been paid",
  "Billing.InvoiceLocked": "Cannot modify a paid invoice",
  "TenantPlan.PlanId_Required": "Plan ID is required",
  "TenantPlan.StartsAt_Required": "Start date is required",
  "TenantPlan.EndDate_Before_Start": "End date must be after start date",
  "TenantPlan.AlreadyActive": "This plan subscription is already active",
  "TenantPlan.NotActive": "This plan subscription is not active",
  "TenantPlan.CannotCancelExpired": "Cannot cancel an expired subscription",
  "Plan.NotFound": "Plan with id '{id}' was not found.",
  "Feature.NotFound": "Feature with id '{id}' was not found.",
  "TenantPlan.NotFound": "TenantPlan with id '{id}' was not found.",
  "TenantCRMLead.NotFound": "TenantCRMLead with id '{id}' was not found.",
  "PlanFeature.PlanId_Invalid": "Plan ID must be greater than zero",
  "PlanFeature.FeatureId_Invalid": "Feature ID must be greater than zero",
  "TenantPlan.Status_Invalid": "Invalid subscription status",
  "Billing.Status_Invalid": "Invalid billing status",
  "Billing.PlanId_Required": "Plan ID is required",

  "Enum:SubscriptionStatus.Active": "Active",
  "Enum:SubscriptionStatus.Expired": "Expired",
  "Enum:SubscriptionStatus.Cancelled": "Cancelled",
  "Enum:SubscriptionStatus.Suspended": "Suspended",
  "Enum:BillingStatus.Unpaid": "Unpaid",
  "Enum:BillingStatus.Paid": "Paid",
  "Enum:BillingStatus.Refunded": "Refunded",
  "Enum:BillingStatus.Failed": "Failed",
  "Enum:LeadStage.New": "New",
  "Enum:LeadStage.Contacted": "Contacted",
  "Enum:LeadStage.Qualified": "Qualified",
  "Enum:LeadStage.Converted": "Converted",
  "Enum:LeadStage.Lost": "Lost",
  "Enum:ErrorKind.Failure": "Failure",
  "Enum:ErrorKind.Unexpected": "Unexpected",
  "Enum:ErrorKind.Validation": "Validation",
  "Enum:ErrorKind.Conflict": "Conflict",
  "Enum:ErrorKind.NotFound": "Not Found",
  "Enum:ErrorKind.Unauthorized": "Unauthorized",
  "Enum:ErrorKind.Forbidden": "Forbidden",
  "Error:Application": "Application error",
  "Error:TenantDeactivated": "Tenant deactivated",
  "Error:TenantDeactivatedDetail": "Your tenant account has been deactivated.",
  "Error:TenantExpired": "Tenant expired",
  "Error:TenantExpiredDetail": "Your tenant subscription has expired."
}
```

**What:** One JSON file per supported language with keys matching `error.Code` prefixed with `Enum:` for enum labels.

**Why:** JSON dictionary files are editable without recompilation, easy to version control, and match the ABP pattern the user prefers.

### Step 2 — Create `JsonLocalizationService` (custom localizer)

**New file:** `src/Centerix.API/Localization/JsonLocalizer.cs`

**What:**
- A singleton service that loads and caches JSON translation dictionaries at startup.
- Method `string Translate(string key, string cultureName)` — looks up key in the matching JSON file, falls back to English (the base file), then to the key itself.
- Method `string TranslateFormat(string key, string cultureName, params object[] args)` — same but calls `string.Format()` on the result.
- Supports `{0}`, `{1}` placeholders (e.g., `"Plan with id '{0}' was not found."`).

**Why:** Encapsulates JSON file loading, caching, and lookup logic cleanly. No external dependency. The English JSON is the invariant fallback.

### Step 3 — Configure request localization middleware

**Modified file:** `src/Centerix.API/DependencyInjection.cs`

**What:**
- Add `AddLocalization()` configuration with `ResourcesPath = "Localization"` (already registered in Program.cs, but ensure options are set).
- Register `JsonLocalizer` as singleton in DI.
- In `UseCoreMiddlewares()`, add `app.UseRequestLocalization()` with:
  - Supported cultures: `["ar", "en"]`
  - Default culture: `"en"`
  - Providers: `AcceptLanguageHeaderRequestCultureProvider`, `QueryStringRequestCultureProvider`, `CookieRequestCultureProvider`

**Why:** ASP.NET Core middleware that sets `CultureInfo.CurrentUICulture` per request based on the `Accept-Language` header, which our localizer reads.

### Step 4 — Localize error descriptions in `ApiController.Problem()`

**Modified file:** `src/Centerix.API/Controllers/ApiController.cs`

**What:**
- Inject `JsonLocalizer` into the base controller.
- Before writing `error.Description` to ProblemDetails, resolve it through the localizer: `_localizer.Translate(error.Code)`.
- For validation errors: same pattern, use translated description in `modelStateDictionary.AddModelError()`.

**Why:** This is the single choke point — every error message from any controller flows through here. No need to modify individual controllers or the domain.

### Step 5 — Localize `GlobalExceptionHandler`

**Modified file:** `src/Centerix.API/Infrastructure/GlobalExceptionHandler.cs`

**What:**
- Inject `JsonLocalizer`.
- Replace hardcoded `"Application error"` with `_localizer.Translate("Error:Application")`.
- Keep the exception message as `Detail` (technical detail not localized), or optionally localize too.

**Why:** Unhandled exceptions return a fixed English title. This localizes it.

### Step 6 — Localize `TenantGuardMiddleware`

**Modified file:** `src/Centerix.API/Infrastructure/TenantGuardMiddleware.cs`

**What:**
- Inject `JsonLocalizer` into the constructor.
- Replace hardcoded strings `"Tenant deactivated"`, `"Your tenant account has been deactivated."`, `"Tenant expired"`, `"Your tenant subscription has expired."` with localized versions using `_localizer.Translate(...)`.
- Detect culture from `Accept-Language` header manually since middleware runs before `UseRequestLocalization` is effective (or ensure middleware ordering is correct).

**Why:** Tenant guard middleware writes its own error JSON responses with hardcoded English.

### Step 7 — Add localized label fields to DTOs

**Modified files:**
- `src/Centerix.Application/Platform/TenantBillingDto.cs`
- `src/Centerix.Application/Platform/TenantPlanDto.cs`
- `src/Centerix.Application/Platform/TenantCRMLeadDto.cs`
- `src/Centerix.Application/Tenants/TenantDto.cs` (if its `Status` byte represents a domain enum)

**What:**
Add a computed, not-mapped string property like:

```csharp
public string StatusLabel => Status switch
{
    0 => "Unpaid",
    1 => "Paid",
    ...
};
```

The label values should come from the `JsonLocalizer` — but since DTOs are in the Application layer and don't reference the API layer, the label will be populated at the **mapping level** (PlatformService / query handler) after localization is available, not in the domain.

**Better approach (recommended):** Add `StatusLabel` and `StageLabel` as simple string properties, and populate them in the service/handler layer where the localizer is injectable.

**Modified file:** `src/Centerix.Infrastructure/Platform/PlatformService.cs`

**What:**
- Inject `JsonLocalizer`.
- In `GetTenantBillingsAsync`, `GetTenantCRMLeadsAsync`, `GetTenantPlansAsync`, after fetching the DTO lists, loop through and set the label fields by resolving enum translations. For example:
  - Map `BillingStatus.Paid` → key `"Enum:BillingStatus.Paid"` → translated string → set `billing.StatusLabel`.
- For `LeadStage` strings, parse the string → get the enum name → same lookup.

**Why:** Keeps the DTO pure (no dependencies). The service layer handles cross-cutting concerns like localization.

### Step 8 — Debug and verify localization of all error source points

**Verify these files have no remaining hardcoded English user-facing strings:**
- `src/Centerix.Domain/Platform/**/FeatureErrors.cs` — all `Error.Validation()` descriptions are codes → localized via Step 4.
- `src/Centerix.Domain/Platform/**/PlanErrors.cs` — same.
- `src/Centerix.Domain/Platform/**/TenantCRMLeadErrors.cs` — same.
- `src/Centerix.Domain/Platform/**/TenantBillingErrors.cs` — same.
- `src/Centerix.Domain/Platform/**/TenantPlanErrors.cs` — same.
- `src/Centerix.Infrastructure/Platform/PlatformService.cs` — all `Error.NotFound()` descriptions are localizable via Step 4 (their codes are in the JSON files).

No changes needed to the domain error catalogs — the localizer key is `error.Code`, so the descriptions act as fallback.

### Step 9 — Build & verify

```bash
dotnet build src/Centerix.API/Centerix.API.csproj --nologo
```

Fix any compilation errors:
- `JsonLocalizer` constructor params.
- DI registration for `JsonLocalizer` in `DependencyInjection.cs`.
- Controller base class injection for localizer.
- DTO new properties getting populated correctly.

## Files Changed/Added (summary)

| Action | File |
|--------|------|
| **NEW** | `src/Centerix.API/Localization/en.json` |
| **NEW** | `src/Centerix.API/Localization/ar.json` |
| **NEW** | `src/Centerix.API/Localization/JsonLocalizer.cs` |
| **EDIT** | `src/Centerix.API/Program.cs` (configure `AddLocalization` options) |
| **EDIT** | `src/Centerix.API/DependencyInjection.cs` (register localizer, add `UseRequestLocalization`) |
| **EDIT** | `src/Centerix.API/Controllers/ApiController.cs` (inject localizer, translate errors) |
| **EDIT** | `src/Centerix.API/Infrastructure/GlobalExceptionHandler.cs` (localize title) |
| **EDIT** | `src/Centerix.API/Infrastructure/TenantGuardMiddleware.cs` (localize tenant deactivated/expired messages) |
| **EDIT** | `src/Centerix.Application/Platform/TenantBillingDto.cs` (add `StatusLabel`) |
| **EDIT** | `src/Centerix.Application/Platform/TenantPlanDto.cs` (add `StatusLabel`) |
| **EDIT** | `src/Centerix.Application/Platform/TenantCRMLeadDto.cs` (add `StageLabel`) |
| **EDIT** | `src/Centerix.Infrastructure/Platform/PlatformService.cs` (inject localizer, populate label fields) |

## Verification

1. `dotnet build` passes with 0 errors
2. Request with `Accept-Language: ar` → error messages returned in Arabic
3. Request with `Accept-Language: en` or no header → error messages in English
4. `GET /api/tenantBillings` returns `StatusLabel` field with translated status name
5. `GET /api/tenantCRMLeads` returns `StageLabel` field with translated stage name
6. `GET /api/tenantPlans` returns `StatusLabel` field with translated subscription status
7. Tenant guard middleware returns translated deactivation/expiry messages
8. Unhandled exception handler returns translated `"Application error"` title
