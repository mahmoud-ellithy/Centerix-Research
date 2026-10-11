# Known Debts

Accepted gaps in currently-shipped behaviour. These are deliberate trade-offs made during
Batches A/B, not bugs to silently work around — listed here so they are visible instead of
only discoverable by reading the code. Closing any of these is a deliberate future task, not
something to "fix" as a drive-by.

## 1. The Users usage counter is never released

`LimitService.ReleaseAsync` is called for Students, Branches and Teachers on delete
(`DeleteStudentCommand`, `DeleteBranchCommand`, `DeleteTeacherCommand`), but there is no
revoke/suspend-membership endpoint yet that calls it for `LimitTypeCodes.Users`. A tenant's
`UsersCount` only ever goes up (via `ReserveAsync`/`IncrementAsync` on invitation accept/
register) until the usage counter is next reconciled
(`ReconcileUsageCountersCommand`, platform-only). Until a membership-revoke/suspend endpoint
exists and is wired to `ReleaseAsync`, a tenant that removes a user stays "charged" for that
seat against its Users limit until someone runs a reconcile.

## 2. Provision-owner's revoke + re-issue is not one transaction

`ProvisionTenantOwnerCommand` (REV-003), when re-sending an owner invitation, revokes the
still-Pending invitation and commits that (`SaveChangesAsync`) BEFORE minting and persisting
the replacement through `IInvitationIssuer.IssueAsync` (its own separate persist). If the
process crashes or the issuer's send fails between those two steps, the tenant is left with
no Pending owner invitation at all (the old one was revoked, the new one never landed) —
recoverable by calling `POST /api/tenants/{id}/provision-owner` again, but not atomic.

## 3. Usage-counter reconcile is not atomic with concurrent activity

`ReconcileUsageCountersCommand` reads current counts (students/teachers/branches/active
memberships) and writes them in a single update, but the reads are a snapshot: a
create/delete/invitation-accept landing on the same tenant while reconcile is running can
race with it. It is a maintenance tool meant to be run during a quiet period for the
affected tenant, not a routine background job — see its XML doc for the full contract,
including the first-owner exemption's known race (two concurrent "first" invitation accepts
for a brand-new tenant can both skip the capacity check — see `AcceptInvitationCommand`/
`RegisterFromInvitationCommand`).
