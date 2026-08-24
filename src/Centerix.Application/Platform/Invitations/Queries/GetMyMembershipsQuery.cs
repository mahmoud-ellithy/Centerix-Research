namespace Centerix.Application.Platform.Invitations.Queries;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Tenants.Enums;

using MediatR;
using Microsoft.EntityFrameworkCore;

public record GetMyMembershipsQuery : IRequest<Result<List<MembershipDto>>>;

public class MembershipDto
{
    public string TenantId { get; init; } = string.Empty;
    public string RoleName { get; init; } = string.Empty;
    public TenantMembershipStatus Status { get; init; }
    public DateTimeOffset JoinedAtUtc { get; init; }
}

public class GetMyMembershipsHandler(
    IAppDbContext dbContext,
    ICurrentUser currentUser) : IRequestHandler<GetMyMembershipsQuery, Result<List<MembershipDto>>>
{
    public async Task<Result<List<MembershipDto>>> Handle(
        GetMyMembershipsQuery request,
        CancellationToken cancellationToken)
    {
        var memberships = await dbContext.TenantMemberships
            .Where(m => m.UserId == currentUser.UserId)
            .OrderByDescending(m => m.JoinedAtUtc)
            .Select(m => new MembershipDto
            {
                TenantId = m.TenantId,
                RoleName = m.RoleName,
                Status = m.Status,
                JoinedAtUtc = m.JoinedAtUtc
            })
            .ToListAsync(cancellationToken);

        return memberships;
    }
}
