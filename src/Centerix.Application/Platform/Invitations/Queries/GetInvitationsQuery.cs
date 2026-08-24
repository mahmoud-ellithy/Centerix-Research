namespace Centerix.Application.Platform.Invitations.Queries;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Tenants.Enums;

using MediatR;
using Microsoft.EntityFrameworkCore;

public record GetInvitationsQuery : IRequest<Result<List<InvitationDto>>>;

public class InvitationDto
{
    public Guid Id { get; init; }
    public string Email { get; init; } = string.Empty;
    public string RoleName { get; init; } = string.Empty;
    public InvitationStatus Status { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset ExpiresAtUtc { get; init; }
    public DateTimeOffset? AcceptedAtUtc { get; init; }
    public DateTimeOffset? RevokedAtUtc { get; init; }
}

public class GetInvitationsHandler(
    IAppDbContext dbContext,
    ICurrentTenant currentTenant) : IRequestHandler<GetInvitationsQuery, Result<List<InvitationDto>>>
{
    public async Task<Result<List<InvitationDto>>> Handle(
        GetInvitationsQuery request,
        CancellationToken cancellationToken)
    {
        var invitations = await dbContext.TenantInvitations
            .Where(i => i.TenantId == currentTenant.TenantId)
            .OrderByDescending(i => i.CreatedAtUtc)
            .Select(i => new InvitationDto
            {
                Id = i.Id,
                Email = i.Email,
                RoleName = i.RoleName,
                Status = i.Status,
                CreatedAtUtc = i.CreatedAtUtc,
                ExpiresAtUtc = i.ExpiresAtUtc,
                AcceptedAtUtc = i.AcceptedAtUtc,
                RevokedAtUtc = i.RevokedAtUtc
            })
            .ToListAsync(cancellationToken);

        return invitations;
    }
}
