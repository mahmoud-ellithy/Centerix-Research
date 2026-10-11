# Production First-PlatformAdmin Bootstrap

How the operator creates the FIRST PlatformAdmin account in production. This mechanism is
operationally distinct from development sample-data seeding
(`DatabaseInitialization:SeedDevelopmentData`, which MUST stay `false` in production).

## 1. Configuration / secrets to supply

Set the dedicated `PlatformAdminBootstrap` section through secure configuration —
environment variables (or a secret store) on the production host. Never commit secrets.

```bash
PlatformAdminBootstrap__Enabled=true
PlatformAdminBootstrap__Email=ops-admin@example.com
PlatformAdminBootstrap__TemporaryPassword=<operator-chosen-strong-temporary-password>
```

Rules:

- No hardcoded password, no `Admin@123`, no default password exists anywhere in the codebase.
- `TemporaryPassword` must satisfy the Identity password policy
  (min 8 chars, digit, upper, lower, symbol, 2 unique chars); a weak value fails startup.
- `SeedDevelopmentData` is NOT involved and stays `false`.

## 2. How the first PlatformAdmin is created

Automatic at startup during the required database seed
(`DatabaseInitialization:Seed` must be `true`, the default). No separate command:

1. The seed validates the enabled configuration (missing/invalid email or password aborts startup).
2. It ensures the `PlatformAdmin` Identity role with the production permission matrix.
3. It creates the Identity user, assigns the `PlatformAdmin` role, and stamps the
   `password.change_required=true` claim — first login MUST rotate the password via
   `POST /api/auth/change-password` (login returns HTTP 403 + a single-use flow token until then).
4. No tenant membership row is created: platform authority flows from the Identity role
   plus `IPlatformAdminVerifier`, never from a membership row. Tenant access is granted
   later per tenant through the explicit owner-provisioning step below
   (`POST /api/tenants/{id}/provision-owner`), never through ad-hoc SQL.

## 2b. Provisioning a new tenant's first TenantAdmin (REV-003)

`POST /api/tenants` creates the tenant but no member: `CreateInvitationHandler` requires
the inviter to hold an active membership, which a PlatformAdmin can never hold (SEC-001).
The explicit platform step that closes this gap is:

1. Approve the tenant: `POST /api/tenants/{id}/approve` (plus `POST /api/tenants/{id}/activate`
   when it must serve traffic).
2. Provision the owner: `POST /api/tenants/{id}/provision-owner` (platform permission
   `Tenants.Update`, no request body). This issues a 7-day `TenantAdmin` invitation to the
   tenant's `OwnerEmail` and e-mails the accept link. Re-calling it revokes a still-Pending
   owner invitation and issues a fresh one (resend semantics); if the owner is already an
   Active member it answers 409.
3. The owner registers via `POST /api/invitations/register` (new account) or accepts via
   `POST /api/invitations/{token}/accept` (existing account), then logs in and calls tenant
   endpoints with the `tenant` header.

Approval does NOT auto-provision the owner: keeping it an explicit step makes re-sends,
delivery-failure compensation and audit (`Tenant.ProvisionOwner`) visible operator actions
rather than hidden side effects of approval.

## 3. Missing configuration

Startup fails fast with `InvalidOperationException` (logged, host does not start):

- `Enabled=true` + missing/invalid `Email` → refused, no account created.
- `Enabled=true` + missing `TemporaryPassword` → refused, no account created.
- `Enabled=true` + weak `TemporaryPassword` → Identity validators reject it, no account created.
- `Enabled=true` + email already belongs to a NON-admin user → refused; the bootstrap
  never elevates an existing user to PlatformAdmin through this mechanism.

## 4. Subsequent startups (idempotency)

- When the configured email already belongs to a PlatformAdmin, the bootstrap logs and
  does nothing: no password reset, no claim/role rewrite. Re-running startup is a no-op.
- Recommended: after the first successful bootstrap + password rotation, set
  `PlatformAdminBootstrap__Enabled=false` (and remove the temporary password from
  configuration). Later startups then skip the bootstrap entirely.

## 5. Accidental recreation / reset protection

- Existence is checked by email before anything is written; an existing PlatformAdmin
  short-circuits the whole path.
- The temporary password is used ONLY on the create path and is never logged.
- Elevating a normal user through this mechanism is refused (startup failure, section 3).
