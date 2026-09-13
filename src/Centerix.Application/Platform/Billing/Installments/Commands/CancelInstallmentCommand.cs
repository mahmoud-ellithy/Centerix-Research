namespace Centerix.Application.Platform.Billing.Installments.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.Installments;
using MediatR;
using Microsoft.EntityFrameworkCore;

public record CancelInstallmentCommand(Guid Id) : IRequest<Result<Updated>>;

public class CancelInstallmentHandler(
    IAppDbContext dbContext,
    IAuditWriter auditWriter) : IRequestHandler<CancelInstallmentCommand, Result<Updated>>
{
    public async Task<Result<Updated>> Handle(
        CancelInstallmentCommand request,
        CancellationToken cancellationToken)
    {
        var tenantId = dbContext.TenantId;
        if (string.IsNullOrWhiteSpace(tenantId))
            return InstallmentErrors.TenantIdRequired;

        var installment = await dbContext.Installments
            .Include(i => i.PaymentAllocations)
            .FirstOrDefaultAsync(i => i.Id == request.Id, cancellationToken);

        if (installment is null)
            return InstallmentErrors.NotFound;

        if (installment.TenantId != tenantId)
            return InstallmentErrors.CrossTenantAccess;

        var cancelResult = installment.Cancel(DateTime.UtcNow);
        if (!cancelResult.IsSuccess)
            return cancelResult.Errors!;

        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "Installment.Cancel",
            entityType: nameof(Installment),
            entityId: request.Id.ToString(),
            cancellationToken: cancellationToken);

        return Result.Updated;
    }
}
