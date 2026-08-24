using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Invitations.Commands;
using Centerix.Application.Platform.Invitations.Queries;
using Centerix.Infrastructure.Auth;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Centerix.API.Controllers;

[Route("api/invitations")]
public class InvitationsController(ILocalizer localizer, IMediator mediator) : ApiController(localizer)
{
    [HttpPost]
    [HasPermission(Permissions.Invitations.Create)]
    public async Task<IActionResult> CreateInvitation(CreateInvitationCommand command, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(command, cancellationToken);

        return result.Match(
            id => StatusCode(StatusCodes.Status201Created, new { id }),
            Problem);
    }

    [HttpGet]
    [HasPermission(Permissions.Invitations.Read)]
    public async Task<IActionResult> GetInvitations(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetInvitationsQuery(), cancellationToken);

        return result.Match(
            invitations => Ok(invitations),
            Problem);
    }

    [HttpPost("{token}/accept")]
    // Existing users must authenticate first: the handler binds the invitation email to the
    // authenticated principal (Invitation.UserMismatch). Falls back to the RequireAuthenticatedUser
    // policy today; made explicit here so the contract is not an accident of configuration.
    [Authorize]
    public async Task<IActionResult> AcceptInvitation(string token, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new AcceptInvitationCommand(token), cancellationToken);

        return result.Match(
            _ => Ok(new { message = "Invitation accepted successfully" }),
            Problem);
    }

    [HttpPost("register")]
    // Token-capability endpoint: the invitee has no account yet, so there is no principal to
    // authenticate. The random 256-bit token IS the credential; it is validated server-side
    // against its SHA-256 hash with status and expiry checks in RegisterFromInvitationHandler.
    // The controller-wide fallback policy (RequireAuthenticatedUser) must not apply here,
    // otherwise brand-new invited users can never register.
    [AllowAnonymous]
    public async Task<IActionResult> RegisterFromInvitation(RegisterFromInvitationCommand command, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(command, cancellationToken);

        return result.Match(
            _ => StatusCode(StatusCodes.Status201Created, new { message = "Account created and invitation accepted" }),
            Problem);
    }

    [HttpPost("{id:guid}/revoke")]
    [HasPermission(Permissions.Invitations.Revoke)]
    public async Task<IActionResult> RevokeInvitation(Guid id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new RevokeInvitationCommand(id), cancellationToken);

        return result.Match(
            _ => NoContent(),
            Problem);
    }
}
