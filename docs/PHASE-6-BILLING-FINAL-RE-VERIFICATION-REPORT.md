# PHASE 6 — BILLING & INVOICING
# PRODUCTION FINANCIAL CORRECTNESS & SECURITY AUDIT

## 1. Executive Summary

**Verdict: NOT APPROVED**

The implemented Billing module is a **partial invoice skeleton**: tenant-isolated CRUD for invoices and lines plus a Draft→Issued→Paid / Cancel state machine. However, it has material financial-correctness defects that are provable from source:

1. **All monetary values are 100% client-supplied and never server-verified** — no invariant ties `Subtotal/Tax/Discount/Total` to the invoice lines, no maximum is enforced (decimal(10,2) allows up to 99,999,999.99 per field, and nothing stops a client from POSTing that on a $10 subscription).
2. **Financial history is mutable after issuance and after payment** — lines can be added and deleted, and even paid invoices expose mutatable amounts at the DB layer with no immutability guard.
3. **`MarkPaid` has zero payment evidence and no concurrency protection** — any user with `Invoices.Update` can mark any tenant invoice Paid with no amount, no payment record, and no RowVersion; concurrent transitions race freely.
4. **The `PlatformPayment` aggregate is dead code** — entity, EF config, migration, and DTO exist, but no controller/command/handler/service creates or reads it. The `GatewayRef` index is non-unique, there is no `TenantId`, and if the intended "mark paid" flow ever goes live without a unique external-reference constraint, duplicate/replayed gateway transactions become possible.
5. **Zero tests** — the entire test project contains no single test referencing Invoice, Billing, TenantCredit, or PlatformPayment.

Tenant isolation itself is the strongest part of the module: a fail-closed global query filter, a guard middleware that verifies active membership, server-side permission resolution (no JWT trust), and a 402 expiry gate. Cross-tenant reads/writes are blocked on every proven path.

## 2. Scope

Audited the current working tree. In scope: `Invoice`, `InvoiceLine`, `PlatformPayment`, `TenantCredit` domain entities; their EF configurations; `InvoicesController`, `TenantCreditsController`; all 7 commands + 4 queries + 1 validator under `Centerix.Application/Platform/Billing`; `AppDbContext` tenant filter; `TenantInterceptor`; `AuditableEntityInterceptor`; `TenantGuardMiddleware`; `PermissionPolicyProvider` / `PermissionAuthorizationHandler` / `FeatureAuthorizationHandler`; `TenantPermissionResolver`; `GlobalExceptionHandler`; `Permissions` / `PermissionCatalog`; `AppDbContextModelSnapshot`; migration set; full test project. Out of scope per instruction: re-audit of Students/Teachers modules (narrow regression check performed — no shared-infrastructure regression found: billing files do not touch shared filter/interceptor logic).

## 3. Inventory

| Category | Items |
|---|---|
| Entities | `Invoice` (AuditableEntity, IHasTenantId), `InvoiceLine` (Entity — **NOT IHasTenantId**), `PlatformPayment` (Entity — **NOT IHasTenantId**, no creation path), `TenantCredit` (AuditableEntity, IHasTenantId) |
| Commands | `CreateInvoiceCommand`, `IssueInvoiceCommand`, `AddInvoiceLineCommand`, `RemoveInvoiceLineCommand`, `MarkInvoicePaidCommand`, `CancelInvoiceCommand`, `CreateTenantCreditCommand` |
| Queries | `GetInvoicesQuery`, `GetInvoiceByIdQuery`, `GetInvoiceLinesQuery`, `GetTenantCreditsQuery` |
| Validators | **Only** `CreateInvoiceValidator`. No validators for the other 6 commands |
| Controllers | `InvoicesController` (10 endpoints), `TenantCreditsController` (2 endpoints) |
| Services | None billing-specific. `IAuditWriter` (generic), `IFeatureAccessService` (unused by billing) |
| EF configurations | `InvoiceConfiguration`, `InvoiceLineConfiguration`, `PlatformPaymentConfiguration`, `TenantCreditConfiguration` |
| DbSets | `Invoices`, `InvoiceLines`, `PlatformPayments`, `TenantCredits` in `AppDbContext` |
| Migrations | All four tables exist in `AppDbContextModelSnapshot`. No dedicated billing migration file — tables created in earlier migrations |
| Permissions | `Invoices.Create/Read/Update/Delete`, `TenantCredits.Create/Read` — present in `Permissions` and `PermissionCatalog` |
| Features | **None.** No `[RequireFeature]` on any billing endpoint; `FeatureCodes` contains only `Students` and `Teachers` |
| Tests | **None.** Grep for `Invoice|Billing|TenantCredit|PlatformPayment|payment` across `tests/` yields zero billing matches |
| Domain events | `InvoicePaidEvent` — defined and raised in `Invoice.MarkPaid()`, dispatched by `AppDbContext.SaveChangesAsync`. **No handler subscribed anywhere in src/** |

## 4. Financial Domain Model

What is actually implemented:

- **Invoice** is a header-first financial document: `Subtotal`, `DiscountAmount`, `TaxAmount`, `TotalAmount` are all **stored fields set exclusively from the client request** in `CreateInvoiceCommand`. There is no code anywhere that derives any of them from `InvoiceLine` records (verified: no calculation of `TotalAmount` from lines exists in src/).
- **InvoiceLine** is append-only detail: `Quantity × UnitPrice = LineTotal` computed server-side in `AddInvoiceLineCommand` (L31). Lines are **not** linked back to the invoice totals. There is no `Currency` field anywhere in the module.
- **PlatformPayment** exists as a table and entity (`Amount`, `Method`, `GatewayRef`, `PaidAt`, `Status`) but is **orphaned**: no API, no command, no handler, no service references `PlatformPayment.Create` or `dbContext.PlatformPayments` outside the DbSet declaration. `InvoicePaidEvent` has no consumer.
- **TenantCredit** supports only `Create` + read. `Apply`, `Expire`, `Revoke`, `Reverse` domain methods exist on `TenantCredit.cs` but have **no application-side caller** — credits can be minted but never consumed, expired, or reversed through any endpoint.
- No refunds, no taxes engine, no discounts engine, no pricing tiers, no renewal billing, no add-on charges. Nothing in the module references `TenantPlan`/`Subscription` — billing is fully decoupled from the subscription lifecycle (renewal does not create invoices; invoice payment does not extend `ValidUpTo`).

## 5. Invoice State Machine (as implemented)

States in enum: `Draft(0), Issued(1), Sent(2), Paid(3), PartiallyPaid(4), Overdue(5), Cancelled(6)`.

| Transition | Enforced? | Actor gate | Notes |
|---|---|---|---|
| — → Draft | `Create` factory | `Invoices.Create` | Always starts Draft |
| Draft → Issued | `Issue()` | `Invoices.Update` | `IssuedAt`/`DueAt` **client-supplied** |
| Draft → Cancelled | `Cancel()` | `Invoices.Update` **and** `Invoices.Delete` (DELETE endpoint reuses cancel) | Two permissions, one action |
| Issued → Paid | `MarkPaid()` | `Invoices.Update` | **No payment evidence required** |
| Sent → Paid | `MarkPaid()` | `Invoices.Update` | `Sent` is **unreachable** — no command exists that sets it |
| Paid → anything | blocked | — | `MarkPaid` from Paid fails (`CannotPayNotIssued`), `Cancel` from Paid fails |
| Cancelled → anything | blocked | — | Terminal |
| Draft → Draft (re-Issue) | blocked | — | `CannotIssueDraftOnly` fires on second call |
| Paid→Unpaid, Cancelled→Paid, etc. | **impossible** | — | All terminal transitions domain-enforced |

Domain enforcement is real: every transition mutates only via `Invoice` methods, and handlers return `Result.Errors` on failure (409 via `ErrorKind.Conflict` → `ApiController` L28–36). Controllers cannot bypass state rules; no handler sets `Status` directly. **However**, `Sent`, `PartiallyPaid`, and `Overdue` are dead states — declared in the enum but unreachable by any code path.

**Critical gap:** state is protected, but *amounts and lines are not state-gated*. `AddInvoiceLineCommand` and `RemoveInvoiceLineCommand` load the invoice by ID and mutate it **regardless of Status** — lines can be added to, and hard-deleted from, an `Issued` or even `Paid` invoice. `Subtotal/Discount/Tax/Total` are likewise client-writable at creation with no cap, and since there is no update command, the only mutation of "financial truth" is lines + creation values.

## 6. Payment State Machine (as implemented)

**Not implemented.** `PlatformPaymentStatus` = `Pending/Completed/Failed/Refunded` exists as an enum and a table, but:

- No endpoint, command, or service can create a `PlatformPayment` (verified: the only reference to `PlatformPayment.Create` in src/ is its own factory; no `PlatformPayments.Add` anywhere).
- Therefore no client can create a Payment as already-Paid, set `PaidAt`, or replay a gateway transaction **through this module** — because the surface does not exist.
- Conversely, `Invoice.MarkPaid()` does **not** create a `PlatformPayment`. Marking paid leaves no financial record of *who paid, how much, or via which reference*.

Answers to the required questions: client-created Paid payment — **NOT APPLICABLE (no creation path)**; same external transaction twice — **NOT APPLICABLE**, but see finding F-11/F-08 on the missing uniqueness that this design would need.

## 7. Critical Findings

| ID | Severity | Finding | Evidence | Impact |
|---|---|---|---|---|
| F-01 | HIGH | Invoice totals are fully client-controlled with no server verification and no upper bound | `CreateInvoiceCommand` (L13–16) accepts `Subtotal/DiscountAmount/TaxAmount/TotalAmount` raw; validator only checks `>= 0`; no invariant vs lines | Client can record arbitrary financial values (up to 9,999,999.99/field) that never match reality |
| F-02 | HIGH | Paid/Issued invoices remain financially mutable (lines add/remove; no immutability) | `AddInvoiceLineHandler`/`RemoveInvoiceLineHandler` never check `invoice.Status`; hard-delete via `dbContext.InvoiceLines.Remove` | Post-issuance and post-payment records can be altered or erased — breaks auditability of settled financial history |
| F-03 | HIGH | `MarkPaid` requires no payment evidence, creates no payment record, and has no concurrency token | `MarkInvoicePaidCommand.cs`; `Invoice` has no `RowVersion` (absent from snapshot L351–431) | Any `Invoices.Update` holder can declare any issued invoice paid; repeated/concurrent calls race; audit trail claims a payment with no traceable money movement |
| F-04 | HIGH | No tests exist for the entire billing module | Full grep of `tests/` — zero matches for Invoice/Billing/TenantCredit/PlatformPayment | None of the 18 required coverage categories is proven; state machine, isolation, and money rules are unverified by executable evidence |
| F-05 | MEDIUM | `InvoiceNumber` unique index is **global**, not tenant-scoped | `UX_Invoices_InvoiceNumber` on `InvoiceNumber` alone (snapshot L420–422) | Tenants collide on numbers → `DbUpdateException` → generic **500** (unhandled type in `GlobalExceptionHandler.cs`), leaking that another tenant's number exists (500 vs 404 differential) |
| F-06 | MEDIUM | Auto-generated invoice number `INV-yyyyMMdd-HHmmss` is collision-prone | `CreateInvoiceCommand.cs` L27 — two invoices in the same second + same second clock = same number → unique violation → 500 | Legitimate concurrent creates fail with 500; no retry/sequence/idempotency |
| F-07 | MEDIUM | No validators for 6 of 7 commands; amounts on lines/credits rely solely on domain checks | Only `CreateInvoiceValidator` exists. `AddInvoiceLineCommand` (negative `Quantity`/`UnitPrice` → negative `LineTotal`), `CreateTenantCreditCommand` (no max — credit of 9,999,999.99 mintable), `IssueInvoiceCommand` (client-supplied `IssuedAt` can be backdated arbitrarily; `DueAt` can be in the past) | Validation layer is uneven; some financial inputs only partially constrained |
| F-08 | MEDIUM | `PlatformPayment` shipped as orphaned schema: no `TenantId`, non-unique `GatewayRef`, no API | `PlatformPaymentConfiguration.cs`; snapshot L476–512 | If/when the payment flow is activated, replayed/duplicate gateway callbacks cannot be deduplicated and the table violates the platform's own tenant-partitioning rule for financial entities |
| F-09 | MEDIUM | Permission/scope classification inconsistency: billing permissions are tenant-scoped but granted to **no** tenant role by default, while `PlatformAdmin` blanket-passes them | `GetTenantAdminPermissions()`/`GetTenantUserPermissions()` (Permissions.cs L228–251) exclude all `Invoices.*`/`TenantCredits.*`; `PermissionAuthorizationHandler` L76–81 auto-succeeds for `PlatformAdmin` role | A tenant admin is locked out of their own invoices unless a role is custom-granted; a platform admin acting under any tenant context can perform all financial mutations for that tenant with one permission set — scope of platform finance authority is undefined |
| F-10 | MEDIUM | Unique-constraint violations and state-conflict DB errors surface as generic 500 | `GlobalExceptionHandler` handles only `ValidationException` and `DbUpdateConcurrencyException`; `DbUpdateException` (unique violations from F-05/F-06) falls to 500 | Financial conflicts are not mapped to the established 409 convention; 500-vs-404 differentials aid enumeration |
| F-11 | MEDIUM | DELETE endpoint performs Cancel with a distinct (`Invoices.Delete`) permission, and only Draft invoices can be cancelled | `InvoicesController.cs` L122–131 reuses `CancelInvoiceCommand` | Two permissions for one action; "delete" of a non-draft invoice silently becomes 409 "cannot cancel", a confusing contract. Not data loss (cancel is the action), but contract is incoherent |
| F-12 | LOW | `Sent`, `PartiallyPaid`, `Overdue` are dead enum states; `InvoicePaidEvent` has no subscriber; `TenantCredit.Apply/Expire/Revoke/Reverse` have no callers | Enum vs handler inventory | Dead financial vocabulary; payment event pipeline exists but terminates in a no-op |
| F-13 | LOW | `GetInvoicesQuery` returns the full tenant invoice set with no pagination | `GetInvoices.cs` | Unbounded response growth over time |
| F-14 | INFO | No `RequireFeature` gating on billing endpoints | `FeatureCodes` has only `Students`/`Teachers`; no `[RequireFeature]` in either controller | Whether billing should be a subscription-gated feature is a **business decision** — current gating is permission + tenant-active + tenant-not-expired only |

## 8. Tenant Isolation — Detailed

**PASS (with the noted medium caveats).** Evidence:

- **Query filter:** `AppDbContext.ApplyTenantQueryFilter` applies `e.TenantId == _currentTenant.TenantId` to every non-owned `IHasTenantId` entity, including `Invoice` and `TenantCredit`. `CurrentTenant.TenantId` returns `""` until `AuthorizeTenant()` — **fail-closed**. The lambda is evaluated against the executing context per query (context-cached model safe). Filter composition is safe: no billing entity is `SoftDeletableEntity`, so the soft-delete composition branch never overwrites the tenant filter for these types; no `IgnoreQueryFilters` call exists in any billing file (verified by grep; the 14 usages are in Students/Teachers/Platform command files only).
- **Guard:** `TenantGuardMiddleware` — invoice/credit permissions are **not** in `PlatformScope.PermissionCodes`, so billing requests go through the full tenant path: tenant must be resolved, user must hold an **active TenantMembership** for the resolved tenant, tenant must be active, and expiry yields 402. Client-selected tenant (`ResolvedTenantId`) is never trusted directly; it is locked in only after membership verification (`AuthorizeTenant()`).
- **Writes:** `TenantInterceptor` stamps `TenantId` from the **authorized** context on `Added` entities only; client cannot set `TenantId` (no command accepts it — verified in all 7 command records).
- **Conceptual cross-tenant tests:** Tenant A user with Tenant B `InvoiceId`: GET → 404 (filter strips the row before `FirstOrDefaultAsync`); Issue/MarkPaid/Cancel/AddLine/RemoveLine → `FindAsync` applies the filter → 404; CREATE → stamped to A's tenant. All blocked. **PASS.**
- **Mutations rely on query filter + membership check** (no explicit `invoice.TenantId == currentTenant.TenantId` re-check in handlers) — acceptable given the fail-closed filter and the middleware membership gate; the only caveat is F-09's platform-admin blanket pass (a `PlatformAdmin` calling these endpoints needs a resolvable tenant header and then acts as a tenant user with all permissions — the filter still confines them to that one tenant's rows).
- **`InvoiceLine`/`PlatformPayment` have no `TenantId`:** reachable only via the tenant-filtered `Invoice` parent and by explicit `InvoiceId` inside filtered commands, so no independent cross-tenant path is provable. **NOT A FAIL** — noted as structural fragility (they would be exposed if any future handler queried them by raw ID without an invoice filter).
- **Existence-leak differential:** the global `InvoiceNumber` unique index produces a 500 on cross-tenant number collision vs 404 on lookup misses (F-05/F-10). This is a **MEDIUM** information leak, not data access.

## 9. Authorization & Feature Gating — Endpoint Level

| Method | Route | Auth | Permission | Feature | Ownership | Verdict |
|---|---|---|---|---|---|---|
| GET | `/api/invoices` | ✔ | `Invoices.Read` | — | tenant filter | OK (no paging — F-13) |
| GET | `/api/invoices/{id}` | ✔ | `Invoices.Read` | — | filter → 404 cross-tenant | OK |
| POST | `/api/invoices` | ✔ | `Invoices.Create` | — | interceptor stamps tenant | OK, but client money (F-01) |
| POST | `/api/invoices/{id}/issue` | ✔ | `Invoices.Update` | — | filter; route/body id matched | OK, but client `IssuedAt` backdatable (F-07) |
| POST | `/api/invoices/{id}/lines` | ✔ | `Invoices.Update` | — | filter; **no status gate** (F-02) | FAIL-gate |
| GET | `/api/invoices/{id}/lines` | ✔ | `Invoices.Read` | — | filter via InvoiceId | OK |
| DELETE | `/api/invoices/{id}/lines/{lineId}` | ✔ | `Invoices.Update` | — | **hard delete, no status gate** (F-02) | FAIL-gate |
| POST | `/api/invoices/{id}/pay` | ✔ | `Invoices.Update` | — | filter; **no evidence, no concurrency** (F-03) | FAIL-gate |
| POST | `/api/invoices/{id}/cancel` | ✔ | `Invoices.Update` | — | filter; domain-locked to Draft | OK |
| DELETE | `/api/invoices/{id}` | ✔ | `Invoices.Delete` | — | performs **Cancel**, Draft only (F-11) | Contract issue |
| GET | `/api/tenantcredits` | ✔ | `TenantCredits.Read` | — | filter | OK |
| POST | `/api/tenantcredits` | ✔ | `TenantCredits.Create` | — | interceptor | OK, unbounded amount (F-07) |

**Pipeline traced, not just attributed:** `HasPermissionAttribute` → `PermissionPolicyProvider` (no claim-based policy — builds a `PermissionRequirement`) → `PermissionAuthorizationHandler`, which reads DB-resolved permissions from `HttpContext.Items["TenantPermissions"]` (populated by the guard) or falls back to a live `TenantMembership → Role → RolePermission → Permission` join — **never from JWT claims**. Fail-closed on any exception. This satisfies "permissions must not be trusted from JWT claims."

**Feature gating:** absent by design currently (F-14). **Expiry/suspension:** guarded globally by the middleware (402 on `ValidUpTo < now`, 403 on inactive) — so expired/suspended tenants **cannot** create financial operations. **PASS** for that sub-question.

## 10. Money Correctness

| Field | C# | Column | Prec/Scale | Nullable | Validation | Max | Negative |
|---|---|---|---|---|---|---|---|
| Invoice.Subtotal/Discount/Tax/Total | decimal | decimal(10,2) | 10,2 | no | `>= 0` (validator + domain) | 9,999,999.99 | blocked |
| InvoiceLine.Quantity | int | int | — | no | **none** (no validator) | int max | **allowed** |
| InvoiceLine.UnitPrice | decimal | decimal(10,2) | 10,2 | no | **none** | 9,999,999.99 | **allowed** |
| InvoiceLine.LineTotal | decimal | decimal(10,2) | 10,2 | no | server-computed `Qty × Price` (can be negative) | — | **possible** |
| TenantCredit.Amount | decimal | decimal(10,2) | 10,2 | no | domain `> 0` only | 9,999,999.99 | blocked |
| PlatformPayment.Amount | decimal | decimal(10,2) | 10,2 | no | n/a (dead) | — | — |

- `decimal` used everywhere — **no float**. Rounding is deterministic (single `int × decimal` multiplication for lines; nothing else is computed).
- **No currency column exists** anywhere in the module — single-currency assumption is implicit, not a defect unless the business requires multi-currency (not established by implementation).
- **`Subtotal + Tax − Discount = Total` is NOT enforced** — the invariant is simply absent (F-01). Business rule ambiguity vs defect: since the fields exist and a total exists, the missing check is an implementation gap, not just ambiguity.
- Payment amount vs invoice amount: not applicable (no payment creation path).
- Values **can be modified after posting**: yes — lines on any-status invoice (F-02). Header amounts are immutable after creation (no update command), which partially mitigates.

## 11. Idempotency & Duplicate Protection

Classification: **(C) No idempotency protection** for any command.

- No `IdempotencyKey` anywhere in billing (grep: none).
- Retrying `POST /api/invoices` with the same client-supplied `InvoiceNumber` → second insert hits `UX_Invoices_InvoiceNumber` → 500 (accidental partial protection, class B, and a poor one — see F-10).
- Retrying with no number → two distinct auto numbers (`INV-...` differs if seconds differ; same second → 500 collision, F-06). **Duplicate invoices ARE possible** via plain retry with blank number.
- `MarkPaid` retried: second call fails with 409 `CannotPayNotIssued` (state machine self-heals, but **not** atomically — see concurrency).
- GatewayRef dedup: index exists but is **non-unique** (F-08).
- Duplicate renewals: n/a — renewal does not touch billing.

## 12. Transactions

Every command performs exactly **one** `SaveChangesAsync` around its single logical write set → atomic at the statement level. No multi-`SaveChanges` sequences, no explicit `BeginTransactionAsync` needed, no external provider calls inside DB work (no provider exists). Domain event dispatch (`InvoicePaidEvent`) happens **inside** `SaveChangesAsync` before commit — a failing event handler would roll back the save (safe direction). `auditWriter.WriteAsync` runs **after** commit; a failed audit write leaves the financial mutation committed without its audit row — acceptable per the interface contract ("fire-and-forget safe") but worth knowing: **audit is best-effort, not transactional**.

Partial-failure scenarios asked about: *payment saved but invoice update fails* — cannot occur (single statement, and payment creation doesn't exist). *Invoice created but renewal fails* — cannot occur (they are unrelated operations today). **No broken-transaction finding.**

## 13. Concurrency

- **No `RowVersion`/concurrency token on `Invoice`** (verified against snapshot: no `rowversion` on Invoices; the five RowVersion properties in the snapshot belong to TenantPlan/Attendance/Student-adjacent/Salary entities). `GlobalExceptionHandler` does map `DbUpdateConcurrencyException` → 409, but there is nothing on the billing side to raise it.
- Race A: two concurrent `MarkPaid` on an Issued invoice → both `FindAsync` see `Issued` → both `SaveChanges` → **both win**, two audit rows, final state Paid (benign for state, corrupt for audit; proves the check-then-act is unsynchronized).
- Race B: concurrent `Issue` + `Cancel` on a Draft → last writer wins silently; e.g., Cancel commits, then Issue's save overwrites with Issued — **a cancelled invoice can be resurrected to Issued** depending on commit order. This is a real state-machine bypass through interleaving, since the guard is in-memory read-modify-write.
- Race C: two concurrent `AddInvoiceLine` → both succeed (append-only, benign). Two concurrent `RemoveInvoiceLine` on same line → second gets 404 or a delete conflict; benign.
- Race D: two `CreateInvoice` same second, blank number → unique violation 500 (F-06).
- **Verdict: unprotected where it matters** (F-03/F-13). Severity MEDIUM-to-HIGH: the Issue/Cancel interleave can violate the documented state machine, but no direct money loss is provable because money is not moved by these operations.

## 14. Database / EF

Model ↔ configuration ↔ snapshot compared for all four entities: **consistent** (columns, precision, nullability, indexes all match; `dotnet ef` not run, snapshot inspected directly). No drift found.

Gaps:
- No `CHECK` constraints on amounts or status values (status is `nvarchar(20)` string-converted — the domain enum is NOT enforced at DB level; a direct SQL write could set `Status='Hacked'`). Same pattern is used across the platform, so this is platform-wide, not billing-specific — noted, not escalated.
- `UX_Invoices_InvoiceNumber` global (F-05). No per-tenant uniqueness for the billing period `(TenantId, PeriodStart, PeriodEnd)` index is **non-unique** → duplicate-period invoices allowed.
- `PlatformPayments.GatewayRef` non-unique, no `TenantId` (F-08).
- FK: `InvoiceLines.InvoiceId` and `PlatformPayments.InvoiceId` → Cascade delete. Invoice rows are never deleted by any command (only cancel), so cascade is dormant; no physical-delete path is exposed. **Financial records cannot be deleted through the API. PASS.**
- No soft-delete on any billing entity (`SoftDeletableEntity` not inherited) → **soft-delete/unique-index interaction is N/A**, and historical records cannot "disappear" from queries via a tombstone. The tenant filter is the only visibility control and it composes correctly.

## 15. Soft Delete

**Not applicable by design** — no billing entity implements `SoftDeletableEntity`, and no filter branch touches them. Consequences: (a) the DELETE endpoint actually *cancels* (F-11), consistent with financial immutability; (b) `RemoveInvoiceLine` is a **hard delete** of a financial line — the opposite of the platform's tombstone convention and the root of F-02's post-payment mutability. No `IgnoreQueryFilters` in billing code.

## 16. Auditability

- `AuditableEntityInterceptor` stamps `CreatedAtUtc/CreatedBy/LastModifiedUtc/LastModifiedBy` on `Invoice` and `TenantCredit` (both `AuditableEntity`). **`InvoiceLine` and `PlatformPayment` have no audit fields at all** — line creation/modification/deletion leaves no actor in the row (only the `IAuditWriter` log entry on create/delete).
- `IAuditWriter` entries exist for: invoice create, issue (old/new status+dates), markPaid (old/new status), cancel (old/new status), line create, line delete, credit create. Missing: **no audit of status-legal failures**, no dedicated audit for amount (amounts never change post-create, so OK), no refund/adjustment (n/a).
- Business-level transition **history** does not exist: the audit log records who changed status when, but there is no per-invoice transition table, and `InvoicePaidEvent` (the natural hook for payment reconciliation) has **no subscriber** — it is dispatched into the void.
- Classification: technical audit fields mostly present (MEDIUM gap on line-level actors); business-level transition history absent (MEDIUM for a financial module).

## 17. API Contract

- Route/body id mismatch guarded for `issue` and `lines` (good); not needed elsewhere (id from route only).
- `DELETE /api/invoices/{id}` = Cancel (F-11) — semantic inconsistency with the `Invoices.Delete` permission name; harmless data-wise since cancel is Draft-only, but the contract misleads clients into expecting hard delete.
- `POST /pay` with no body — state command, fine; but it is an unconditional "paid" assertion (F-03).
- Status codes: 404/409/400 all correctly produced via the `ErrorKind` mapping; validation → 400 problem; **unique violations → 500** (F-10). No 422/402 confusion (402 is reserved by the guard for expiry — correct).
- GETs expose no provider/payment internals (there is nothing to leak; `GatewayRef` has no read endpoint).
- No endpoint reveals existence of cross-tenant objects by ID (all 404) — except the number-collision 500 differential (F-05/F-10).

## 18. External Payment Integration

**No external payment provider integration found.** No webhook, no callback, no signature verification, no provider client anywhere in src/. (F-08 notes the orphaned `GatewayRef` column implies one was planned.)

## 19. Test Coverage & Test Quality

**Zero billing tests.** The test project (SQL Server integration factory + WebApplicationFactory HTTP harness — capable infrastructure, demonstrated by Phase 2–5 suites) contains no test referencing any billing type. All 18 required categories are untested: tenant isolation, authorization, feature gating, invoice creation/lifecycle, payment lifecycle, invalid transitions, amount validation, duplicate payment, idempotency, concurrency, rollback, constraints, migration consistency, soft delete, subscription interaction, cross-tenant mutation, unauthorized mutation.

What the infrastructure *could* prove (SQL Server available): unique-index behavior, decimal precision, FK cascade, RowVersion-less race outcomes, query-filter composition. **None of it is proven today.** This is reported as a missing-tests finding (F-04), and per the rules, missing tests are distinguished from proven production defects — but combined with F-01/F-02/F-03 they remove the last verification layer over unproven financial behavior.

## 20. Business Decisions Required

1. **Should invoice totals be server-computed from lines?** Implementation does not establish it (stored vs computed). Needed to close F-01 properly.
2. **Are multi-currency values in scope?** No `Currency` field exists; not established.
3. **Is billing a subscription-gated feature (`RequireFeature`)?** Not established.
4. **Who may mark an invoice Paid — tenant staff, platform, or both, and with what evidence?** Not established; current design says "anyone with `Invoices.Update`, no evidence."
5. **When do `Sent`/`Overdue`/`PartiallyPaid` become reachable, or should they be removed?** Not established.
6. **What is the intended end-state of `PlatformPayment` and `TenantCredit` consumption (Apply/Reverse)?** Currently dead — either finish or remove.
7. **Should duplicate-period invoices per tenant be unique?** Not established.
8. **Is client-supplied `IssuedAt`/`DueAt` on Issue intended (backdating)?** Not established.

## 21. Complete Findings Register

**CRITICAL** — none provable. (No cross-tenant access, no fake-payment creation path, no money duplication path is reachable from any endpoint.)

**HIGH**
- **F-01** — Client-controlled invoice money, no invariants, no caps. Files: `CreateInvoiceCommand.cs`, `CreateInvoiceValidator.cs`, `InvoiceConfiguration.cs`. Scenario: POST invoice with `TotalAmount=9999999.99` for a $10 service; no layer objects. Remediation: server-derive totals from validated lines (or enforce the declared invariant + business max), reject mismatched client totals. Tests: amount invariant unit tests + HTTP 400 tests. No migration required (optionally add CHECK constraint).
- **F-02** — Post-issuance/post-payment mutation & hard-delete of lines. Files: `AddInvoiceLineCommand.cs`, `RemoveInvoiceLineCommand.cs`. Scenario: delete a line from a Paid invoice to falsify history. Remediation: status gate (lines mutable only in Draft) or soft-delete lines per platform convention; tests: 409 on non-Draft, visibility of tombstones. Migration: add `DeletedAtUtc` if soft-delete chosen.
- **F-03** — `MarkPaid` without evidence + no concurrency token. Files: `MarkInvoicePaidCommand.cs`, `Invoice.cs`, `InvoiceConfiguration.cs`. Remediation: require an associated payment/amount record (or at minimum an audited amount), add `RowVersion` to `Invoice`, handle `DbUpdateConcurrencyException` (handler already maps it). Tests: double-MarkPaid race on SQL Server; 409 on stale RowVersion. Migration: add `rowversion` column (no data migration).
- **F-04** — No tests at all for the module. Remediation: new billing test suite on the existing SQL Server factory covering at minimum: cross-tenant 404s, permission denial, full state-machine matrix, negative/oversized amounts, duplicate number 500/409, concurrent MarkPaid/Issue/Cancel.

**MEDIUM**
- **F-05** — Global `UX_Invoices_InvoiceNumber` → cross-tenant collision 500 + existence leak. Files: `InvoiceConfiguration.cs`, `GlobalExceptionHandler.cs`. Remediation: tenant-scoped uniqueness `(TenantId, InvoiceNumber)` and/or map `DbUpdateException` for known unique index to 409 with generic message. Migration: replace unique index (requires checking existing data for duplicates).
- **F-06** — `INV-yyyyMMdd-HHmmss` auto-number collision. File: `CreateInvoiceCommand.cs`. Remediation: GUID suffix, per-tenant sequence, or retry-on-conflict. No migration.
- **F-07** — Missing validators for 6 commands; negative line quantities/prices; backdatable `IssuedAt`; unbounded credit. Files: `AddInvoiceLineCommand.cs`, `IssueInvoiceCommand.cs`, `CreateTenantCreditCommand.cs`, `MarkInvoicePaidCommand.cs`, `CancelInvoiceCommand.cs`, `RemoveInvoiceLineCommand.cs`. Remediation: FluentValidation validators for each (pipeline already wired); tests: 400 cases. No migration.
- **F-08** — Orphaned `PlatformPayment`: no `TenantId`, non-unique `GatewayRef`. Files: `PlatformPayment.cs`, `PlatformPaymentConfiguration.cs`. Remediation: either implement the payment flow with `TenantId` + unique `(TenantId, GatewayRef)` (filter nulls) before any activation, or remove the entity/table. Migration required either way.
- **F-09** — Permission scope: billing perms absent from tenant role defaults while `PlatformAdmin` blanket-passes; platform financial authority undefined. Files: `Permissions.cs`, `PermissionAuthorizationHandler`. Remediation: business decision on role grants + explicit platform-override design for financial endpoints. Tests: role-matrix HTTP tests.
- **F-10** — `DbUpdateException` → 500. File: `GlobalExceptionHandler.cs`. Remediation: map unique-violation to 409 with generic detail. Tests: collision returns 409, no data leak.
- **F-11** — DELETE = Cancel under a Delete permission; dual permission for one action. Files: `InvoicesController.cs`, `Permissions.cs`. Remediation: align contract (DELETE → 405/409 semantics or rename permission) — business/API decision. No migration.

**LOW**
- **F-12** — Dead states (`Sent`, `PartiallyPaid`, `Overdue`), unsubscribed `InvoicePaidEvent`, dead credit transitions. Remediation: implement or remove.
- **F-13** — No pagination on `GET /api/invoices`.
- **F-14** — (INFO) No feature gating on billing — flagged as business decision, not a defect.

## 22. Recommended Remediation Plan (risk-ordered)

1. **F-03** — Add `RowVersion` to `Invoice`; require payment evidence/amount on MarkPaid. Files: `Invoice.cs`, `InvoiceConfiguration.cs`, `MarkInvoicePaidCommand.cs`. Tests: concurrency race + 409 stale-token on SQL Server. **Migration: add `rowversion`; no data migration.**
2. **F-01** — Server-authoritative amounts (or strict invariant + max). Files: `CreateInvoiceCommand.cs`, `AddInvoiceLineCommand.cs`, `CreateInvoiceValidator.cs`, domain `Invoice`. Tests: invariant matrix. No migration (optional CHECK).
3. **F-02** — Status-gate line mutation/deletion; prefer soft-delete for lines. Files: `AddInvoiceLineCommand.cs`, `RemoveInvoiceLineCommand.cs`, `InvoiceLine.cs`, `InvoiceLineConfiguration.cs`. Tests: 409 non-Draft, tombstone visibility. **Migration: soft-delete columns if adopted (no data migration).**
4. **F-04** — Full billing test suite on the existing SQL Server integration factory. Files: new tests under `tests/Centerix.SecurityTests/`. No migration.
5. **F-05 + F-10** — Tenant-scoped invoice-number uniqueness + unique-violation → 409. Files: `InvoiceConfiguration.cs`, `GlobalExceptionHandler.cs`. **Migration: index change; requires duplicate-number check across existing data (only if production data exists).**
6. **F-06** — Collision-proof auto numbering. File: `CreateInvoiceCommand.cs`. No migration.
7. **F-07** — Validators for the remaining six commands. Files: new validator classes under `Platform/Billing/Commands/`. No migration.
8. **F-08** — Decide platform payment fate; if kept, add `TenantId` + unique gateway ref before activation. **Migration required.**
9. **F-09** — Role/permission matrix decision + platform-admin financial-override design. Files: `Permissions.cs`, seeding. Tests: role matrix HTTP.
10. **F-11 / F-12 / F-13** — Contract alignment, dead-code removal, pagination. No migrations.

## 23. Final Verdict

**NOT APPROVED**

### FINAL DECISION

- **VERDICT:** NOT APPROVED
- **CRITICAL FINDINGS:** 0
- **HIGH FINDINGS:** 4 (F-01 client-controlled money, F-02 post-payment mutability, F-03 evidence-free/unprotected MarkPaid, F-04 zero tests)
- **MEDIUM FINDINGS:** 7 (F-05...F-11)
- **LOW FINDINGS:** 3 (F-12, F-13, F-14)
- **TEST STATUS:** FAIL — no billing tests exist; 0/18 coverage categories
- **MIGRATION STATUS:** schema/model/snapshot consistent (no drift); migrations *required* for remediations 1, 3 (optional), 5, 8
- **PRODUCTION READINESS:** Not production-ready. Tenant isolation and the state machine are sound, but the module accepts unverified client money, permits post-payment history mutation, asserts "paid" with no evidence or concurrency safety, and ships an orphaned payment table — all unverified by any test. Remediate F-01...F-04 (and ideally F-05/F-06/F-07) and re-audit before approval.
