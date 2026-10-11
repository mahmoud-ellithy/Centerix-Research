namespace Centerix.Application.Platform.Invitations;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Tenants;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using System.Security.Cryptography;

/// <summary>
/// Shared invitation-issuing primitive (REV-003). Extracted from
/// <c>CreateInvitationHandler</c> so the platform tenant-owner provisioning path can mint
/// the tenant's first invitation without depending on <c>ICurrentTenant</c> or on an
/// inviter <c>TenantMembership</c> (a new tenant has no members yet, and a PlatformAdmin
/// can never hold a platform-role membership per SEC-001).
/// Behaviour: token generation (32 random bytes, base64url), SHA-256 hash persistence
/// (raw token never stored), <c>TenantInvitation.Create</c> domain validation (SEC-001/F3),
/// persist → commit → send, and compensation-on-send-failure (revoke so nothing stays
/// Pending, returning <c>InvitationDeliveryFailed</c>). No Outbox/DATA-001 — same bounded
/// consistency contract as the original handler.
/// </summary>
public interface IInvitationIssuer
{
    Task<Result<Guid>> IssueAsync(
        string tenantId,
        string email,
        string roleName,
        string invitedByUserId,
        int expirationDays = 7,
        CancellationToken cancellationToken = default);
}

public class InvitationIssuer(
    IAppDbContext dbContext,
    IEmailSender emailSender,
    IInvitationLinkBuilder invitationLinkBuilder,
    ILogger<InvitationIssuer> logger) : IInvitationIssuer
{
    public async Task<Result<Guid>> IssueAsync(
        string tenantId,
        string email,
        string roleName,
        string invitedByUserId,
        int expirationDays = 7,
        CancellationToken cancellationToken = default)
    {
        var trimmedEmail = email.Trim();

        // REV-003: cryptographically secure invitation token (same shape as the original
        // handler — 32 random bytes, base64url, no padding). The raw token is returned only
        // via the e-mail link; only its SHA-256 hash is persisted.
        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToBase64String(tokenBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var tokenHash = HashToken(token);

        var invitationResult = TenantInvitation.Create(
            id: Guid.NewGuid(),
            tenantId: tenantId,
            email: trimmedEmail,
            invitedByUserId: invitedByUserId,
            roleName: roleName,
            tokenHash: tokenHash,
            expiresAtUtc: DateTimeOffset.UtcNow.AddDays(expirationDays));

        if (!invitationResult.IsSuccess)
            return invitationResult.Errors!;

        dbContext.TenantInvitations.Add(invitationResult.Value);
        var invitationId = invitationResult.Value.Id;
        var invitationNormalizedEmail = invitationResult.Value.NormalizedEmail;
        await dbContext.SaveChangesAsync(cancellationToken);

        var acceptUrl = invitationLinkBuilder.BuildAcceptLink(token);
        try
        {
            await emailSender.SendAsync(
                trimmedEmail,
                "You've been invited to join a center",
                $"<p>You've been invited to join a center as <strong>{roleName}</strong>.</p>" +
                $"<p><a href=\"{acceptUrl}\">Click here to accept the invitation</a></p>" +
                $"<p>This invitation expires in {expirationDays} days.</p>",
                cancellationToken);
        }
        catch (Exception sendEx)
        {
            // Delivery failed AFTER commit: attempt compensation so the invitation does
            // not remain Pending, but report FAILURE either way — never success.
            try
            {
                var pending = await dbContext.TenantInvitations
                    .FirstOrDefaultAsync(i => i.Id == invitationId, cancellationToken);
                if (pending is not null && pending.Status == Domain.Platform.Tenants.Enums.InvitationStatus.Pending)
                {
                    pending.Revoke(invitedByUserId);
                    await dbContext.SaveChangesAsync(cancellationToken);
                }

                logger.LogWarning(
                    sendEx,
                    "Invitation email delivery failed; compensation succeeded. " +
                    "InvitationId={InvitationId}, TenantId={TenantId}, NormalizedEmail={NormalizedEmail}.",
                    invitationId, tenantId, invitationNormalizedEmail);
            }
            catch (Exception compensationEx)
            {
                logger.LogError(
                    compensationEx,
                    "Invitation email delivery failed AND compensation failed. " +
                    "InvitationId={InvitationId}, TenantId={TenantId}, NormalizedEmail={NormalizedEmail}, " +
                    "SendException={SendException}. Operator reconciliation required: revoke or re-send the Pending invitation.",
                    invitationId, tenantId, invitationNormalizedEmail, sendEx.ToString());
            }

            return TenantMembershipErrors.InvitationDeliveryFailed;
        }

        return invitationId;
    }

    internal static string HashToken(string token)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
