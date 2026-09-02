namespace Centerix.Application.Teachers.SalaryPayments.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Teachers.Enums;
using Centerix.Domain.Teachers.SalaryPayments;
using Centerix.Domain.Teachers.Teachers;

using FluentValidation;

using MediatR;

using Microsoft.EntityFrameworkCore;

public record CreateSalaryPaymentCommand(
    Guid TeacherId,
    byte PeriodMonth,
    short PeriodYear,
    decimal GrossAmount,
    decimal NetAmount,
    SalaryPaymentStatus Status) : IRequest<Result<Created>>;

public class CreateSalaryPaymentValidator : AbstractValidator<CreateSalaryPaymentCommand>
{
    public CreateSalaryPaymentValidator()
    {
        RuleFor(x => x.TeacherId).NotEmpty();
        RuleFor(x => x.PeriodMonth).InclusiveBetween((byte)1, (byte)12);
        RuleFor(x => x.PeriodYear).InclusiveBetween((short)2000, (short)2100);
        RuleFor(x => x.GrossAmount).GreaterThan(0);
        RuleFor(x => x.NetAmount).GreaterThan(0);
    }
}

public class CreateSalaryPaymentHandler(
    IAppDbContext dbContext,
    ICurrentTenant currentTenant,
    IAuditWriter auditWriter) : IRequestHandler<CreateSalaryPaymentCommand, Result<Created>>
{
    public async Task<Result<Created>> Handle(
        CreateSalaryPaymentCommand request,
        CancellationToken cancellationToken)
    {
        var teacherExists = await dbContext.Teachers
            .AsNoTracking()
            .AnyAsync(t => t.Id == request.TeacherId, cancellationToken);
        if (!teacherExists)
            return TeacherErrors.NotFound;

        var result = SalaryPayment.Create(
            Guid.NewGuid(),
            request.TeacherId,
            request.PeriodMonth,
            request.PeriodYear,
            request.GrossAmount,
            request.NetAmount,
            request.Status,
            paidAt: null);

        if (!result.IsSuccess)
            return result.Errors!;

        dbContext.SalaryPayments.Add(result.Value);
        dbContext.StampAddedTenantIds(currentTenant.TenantId!);
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "SalaryPayment.Create",
            entityType: nameof(SalaryPayment),
            entityId: result.Value.Id.ToString(),
            newValue: AuditPayload.Serialize(new
            {
                result.Value.TeacherId,
                result.Value.PeriodMonth,
                result.Value.PeriodYear,
                result.Value.GrossAmount,
                result.Value.NetAmount,
                result.Value.Status
            }),
            cancellationToken: cancellationToken);

        return Result.Created;
    }
}

public record MarkSalaryPaymentPaidCommand(Guid Id) : IRequest<Result<Updated>>;

public class MarkSalaryPaymentPaidHandler(
    IAppDbContext dbContext,
    IAuditWriter auditWriter) : IRequestHandler<MarkSalaryPaymentPaidCommand, Result<Updated>>
{
    public async Task<Result<Updated>> Handle(
        MarkSalaryPaymentPaidCommand request,
        CancellationToken cancellationToken)
    {
        var payment = await dbContext.SalaryPayments
            .FirstOrDefaultAsync(p => p.Id == request.Id, cancellationToken);
        if (payment is null)
            return SalaryPaymentErrors.NotFound;

        var oldValue = AuditPayload.Serialize(new
        {
            payment.Status,
            payment.PaidAt
        });

        var result = payment.MarkPaid(DateTime.UtcNow);
        if (!result.IsSuccess)
            return result.Errors!;

        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "SalaryPayment.MarkPaid",
            entityType: nameof(SalaryPayment),
            entityId: payment.Id.ToString(),
            oldValue: oldValue,
            newValue: AuditPayload.Serialize(new
            {
                payment.Status,
                payment.PaidAt
            }),
            cancellationToken: cancellationToken);

        return Result.Updated;
    }
}

public record CancelSalaryPaymentCommand(Guid Id) : IRequest<Result<Updated>>;

public class CancelSalaryPaymentHandler(
    IAppDbContext dbContext,
    IAuditWriter auditWriter) : IRequestHandler<CancelSalaryPaymentCommand, Result<Updated>>
{
    public async Task<Result<Updated>> Handle(
        CancelSalaryPaymentCommand request,
        CancellationToken cancellationToken)
    {
        var payment = await dbContext.SalaryPayments
            .FirstOrDefaultAsync(p => p.Id == request.Id, cancellationToken);
        if (payment is null)
            return SalaryPaymentErrors.NotFound;

        var oldValue = AuditPayload.Serialize(new
        {
            payment.Status,
            payment.PaidAt
        });

        var result = payment.Cancel();
        if (!result.IsSuccess)
            return result.Errors!;

        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "SalaryPayment.Cancel",
            entityType: nameof(SalaryPayment),
            entityId: payment.Id.ToString(),
            oldValue: oldValue,
            newValue: AuditPayload.Serialize(new
            {
                payment.Status,
                payment.PaidAt
            }),
            cancellationToken: cancellationToken);

        return Result.Updated;
    }
}