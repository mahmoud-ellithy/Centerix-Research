namespace Centerix.Application.Platform.Billing.Commands;

using System.Data;
using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.Credits;
using Centerix.Domain.Platform.Billing.Credits.Enums;
using Centerix.Domain.Platform.Billing.Invoicing;
using Centerix.Domain.Platform.Billing.Invoicing.Enums;
using Centerix.Domain.Platform.Billing.Payments;
using Centerix.Domain.Platform.Billing.Payments.Enums;

using MediatR;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

public record CreateTenantCreditCommand(
    decimal Amount,
    byte SourceType,
    Guid? SourceId,
    string CurrencyCode = "EGP",
    string? IdempotencyKey = null) : IRequest<Result<Created>>;

public class CreateTenantCreditHandler(
    IAppDbContext dbContext,
    IAuditWriter auditWriter) : IRequestHandler<CreateTenantCreditCommand, Result<Created>>
{
    public async Task<Result<Created>> Handle(
        CreateTenantCreditCommand request,
        CancellationToken cancellationToken)
    {
        // Task 19 — IDEMPOTENCY CHECK (key-based, BEFORE insert).
        // The existing UX_TenantCredits_TenantId_SourceType_SourceId filtered unique index
        // is a structural guard (one credit per source). A client-supplied IdempotencyKey
        // is the authoritative logical retry token, and is required for callers that
        // create credits without a SourceId (e.g. Manual / Compensation credits).
        if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            var existingCredit = await dbContext.TenantCredits
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    tc => tc.IdempotencyKey == request.IdempotencyKey,
                    cancellationToken);

            if (existingCredit is not null)
            {
                if (existingCredit.Amount == request.Amount
                    && existingCredit.SourceType == (CreditSourceType)request.SourceType
                    && existingCredit.SourceId == request.SourceId
                    && existingCredit.CurrencyCode == request.CurrencyCode)
                {
                    return Result.Created;
                }

                return Error.Conflict(
                    "TenantCredit.IdempotencyKeyConflict",
                    "A tenant credit with the same IdempotencyKey but different parameters already exists.");
            }
        }

        var creditResult = TenantCredit.Create(
            Guid.NewGuid(),
            request.Amount,
            (CreditSourceType)request.SourceType,
            request.SourceId,
            request.CurrencyCode,
            request.IdempotencyKey);

        if (!creditResult.IsSuccess)
        {
            return creditResult.Errors!;
        }

        dbContext.TenantCredits.Add(creditResult.Value);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
            {
                if (dbContext is DbContext dbc)
                {
                    dbc.ChangeTracker.Clear();
                }

                var persisted = await dbContext.TenantCredits
                    .AsNoTracking()
                    .FirstOrDefaultAsync(
                        tc => tc.IdempotencyKey == request.IdempotencyKey,
                        cancellationToken);

                if (persisted is not null)
                {
                    if (persisted.Amount == request.Amount
                        && persisted.SourceType == (CreditSourceType)request.SourceType
                        && persisted.SourceId == request.SourceId
                        && persisted.CurrencyCode == request.CurrencyCode)
                    {
                        return Result.Created;
                    }

                    return Error.Conflict(
                        "TenantCredit.IdempotencyKeyConflict",
                        "A tenant credit with the same IdempotencyKey but different parameters already exists.");
                }
            }

            throw;
        }

        await auditWriter.WriteAsync(
            action: "TenantCredit.Create",
            entityType: nameof(TenantCredit),
            entityId: creditResult.Value.Id.ToString(),
            newValue: AuditPayload.Serialize(new
            {
                creditResult.Value.Amount,
                SourceType = creditResult.Value.SourceType.ToString(),
                creditResult.Value.SourceId,
                Status = creditResult.Value.Status.ToString()
            }),
            cancellationToken: cancellationToken);

        return Result.Created;
    }
}

public record ApplyCreditToInvoiceCommand(
    Guid CreditId,
    Guid InvoiceId,
    decimal Amount,
    string IdempotencyKey) : IRequest<Result<Updated>>;

public class ApplyCreditToInvoiceHandler(
    IAppDbContext dbContext,
    IAuditWriter auditWriter) : IRequestHandler<ApplyCreditToInvoiceCommand, Result<Updated>>
{
    private const int MaxDeadlockRetries = 3;

    public async Task<Result<Updated>> Handle(
        ApplyCreditToInvoiceCommand request,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt <= MaxDeadlockRetries; attempt++)
        {
            if (dbContext is DbContext dbc)
            {
                dbc.ChangeTracker.Clear();
            }

            var result = await TryHandleAsync(request, cancellationToken);

            if (result.IsSuccess || !IsRetryableError(result))
            {
                return result;
            }

            if (attempt < MaxDeadlockRetries)
            {
                var delay = TimeSpan.FromMilliseconds(50 * Math.Pow(2, attempt));
                await Task.Delay(delay, cancellationToken);
            }
        }

        return Error.Conflict("CreditApplication.ConcurrencyConflict",
            "This credit application conflicted with another concurrent request. Please retry.");
    }

    private static bool IsRetryableError(Result<Updated> result)
    {
        return result.Errors?.Any(e => e.Code == "CreditApplication.ConcurrencyConflict") ?? false;
    }

    private async Task<Result<Updated>> TryHandleAsync(
        ApplyCreditToInvoiceCommand request,
        CancellationToken cancellationToken)
    {
        await using var transaction = dbContext.IsRelational
            ? await dbContext.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            : null;

        try
        {
            return await ExecuteAsync(request, cancellationToken, transaction);
        }
        catch (Exception ex) when (IsDeadlockException(ex))
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }
            return Error.Conflict("CreditApplication.ConcurrencyConflict",
                "This credit application conflicted with another concurrent request. Please retry.");
        }
    }

    private async Task<Result<Updated>> ExecuteAsync(
        ApplyCreditToInvoiceCommand request,
        CancellationToken cancellationToken,
        IDbContextTransaction? transaction)
    {
        // Use UPDLOCK, ROWLOCK, HOLDLOCK on the credit read to serialize concurrent
        // requests targeting the same credit. Without this, both transactions acquire
        // shared locks (Serializable default), then both need exclusive locks for the
        // UPDATE → deadlock (SQL 1205). With UPDLOCK the second transaction blocks
        // until the first commits, then re-reads the committed state and finds the
        // winner's CreditApplication via the idempotency check → IdempotencyKeyConflict.
        TenantCredit? credit;
        if (dbContext.IsRelational)
        {
            credit = await dbContext.TenantCredits
                .FromSqlRaw(
                    "SELECT * FROM [Platform].[TenantCredits] WITH (UPDLOCK, ROWLOCK, HOLDLOCK) WHERE [TenantCreditId] = @p0",
                    request.CreditId)
                .FirstOrDefaultAsync(cancellationToken);
        }
        else
        {
            credit = await dbContext.TenantCredits
                .FirstOrDefaultAsync(c => c.Id == request.CreditId, cancellationToken);
        }

        if (credit is null)
        {
            return TenantCreditErrors.NotFound;
        }

        // ── IDEMPOTENCY CHECK (key-based, BEFORE financial validations) ─
        // This MUST happen before any amount/capacity checks so that a retry
        // after the credit has been partially consumed by the winning request
        // still returns the correct idempotent result instead of failing with
        // InvalidApplicationAmount.
        //
        // The IdempotencyKey identifies the logical client operation.
        // Same key + same parameters → idempotent retry (return success).
        // Same key + different parameters → conflict (reject).
        // Different keys → legitimate separate operations.
        var existingApplication = await dbContext.CreditApplications
            .Where(ca => ca.TenantId == credit.TenantId
                && ca.IdempotencyKey == request.IdempotencyKey)
            .FirstOrDefaultAsync(cancellationToken);

        if (existingApplication is not null)
        {
            // Existing application found — check if parameters match
            if (existingApplication.CreditId == credit.Id
                && existingApplication.InvoiceId == request.InvoiceId
                && existingApplication.Amount == request.Amount)
            {
                // Idempotent retry — same logical request. Return success.
                if (transaction is not null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }
                return Result.Updated;
            }

            // Idempotency key conflict — same key, different parameters.
            return TenantCreditErrors.IdempotencyKeyConflict;
        }

        // ── FINANCIAL VALIDATIONS (only when no idempotent match exists) ──

        if (credit.Status != CreditStatus.Available && credit.Status != CreditStatus.PartiallyApplied)
        {
            return TenantCreditErrors.NotAvailable;
        }

        if (request.Amount <= 0)
        {
            return Error.Validation("TenantCredit.InvalidApplicationAmount",
                "Application amount must be greater than zero.");
        }

        if (request.Amount > credit.RemainingAmount)
        {
            return Error.Validation("TenantCredit.InvalidApplicationAmount",
                "Application amount exceeds the available credit remaining.");
        }

        // ── INVOICE ROW LOCK (SQL Server) ───────────────────────────────
        // Serialize all settlement operations against this invoice. AllocatePayment
        // acquires the same UPDLOCK before reading invoice state, so a concurrent credit
        // application and payment allocation cannot both validate against a remaining
        // balance that ignores the other's settlement and over-settle the invoice.
        if (dbContext.IsRelational && dbContext is DbContext efDb)
        {
            var conn = efDb.Database.GetDbConnection();
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction!.GetDbTransaction();
            cmd.CommandText = "SELECT 1 FROM Platform.Invoices WITH (UPDLOCK, ROWLOCK, HOLDLOCK) WHERE InvoiceId = @p0 AND TenantId = @p1";
            var p0 = cmd.CreateParameter(); p0.ParameterName = "@p0"; p0.Value = request.InvoiceId;
            var p1 = cmd.CreateParameter(); p1.ParameterName = "@p1"; p1.Value = credit.TenantId!;
            cmd.Parameters.Add(p0); cmd.Parameters.Add(p1);
            if (conn.State != System.Data.ConnectionState.Open)
            {
                await conn.OpenAsync(cancellationToken);
            }
            await cmd.ExecuteScalarAsync(cancellationToken);
        }

        var invoice = await dbContext.Invoices
            .FirstOrDefaultAsync(i => i.Id == request.InvoiceId, cancellationToken);

        if (invoice is null)
        {
            return Error.NotFound("Invoice.NotFound", $"Invoice with id '{request.InvoiceId}' was not found.");
        }

        if (invoice.Status == InvoiceStatus.Draft || invoice.Status == InvoiceStatus.Cancelled)
        {
            return Error.Conflict("Invoice.CannotApplyCreditToDraftOrCancelled",
                "Credit cannot be applied to draft or cancelled invoices.");
        }

        // Tenant isolation: credit and invoice must belong to the same tenant
        if (credit.TenantId != invoice.TenantId)
        {
            return TenantCreditErrors.CrossTenant;
        }

        // ── CURRENCY INTEGRITY ──────────────────────────────────────────
        // Determine the authoritative currency for the invoice via its Contract.
        // Invoices without a contract (legacy) pass currency validation; the system
        // does not assume a hardcoded currency.
        if (invoice.ContractId.HasValue)
        {
            var contract = await dbContext.Contracts
                .FirstOrDefaultAsync(c => c.Id == invoice.ContractId.Value, cancellationToken);

            if (contract is not null &&
                !string.Equals(credit.CurrencyCode, contract.CurrencyCode, StringComparison.OrdinalIgnoreCase))
            {
                return TenantCreditErrors.CurrencyMismatch;
            }
        }

        // Load invoice with credit applications to compute remaining
        var invoiceCreditApplications = await dbContext.CreditApplications
            .Where(ca => ca.InvoiceId == request.InvoiceId && ca.TenantId == credit.TenantId)
            .ToListAsync(cancellationToken);

        // Compute remaining by re-loading invoice with payment allocations
        var invoiceWithAllocations = await dbContext.Invoices
            .Include(i => i.PaymentAllocations)
            .FirstOrDefaultAsync(i => i.Id == request.InvoiceId, cancellationToken);

        // Manually set the credit applications on the loaded invoice by computing
        var totalCreditApplied = invoiceCreditApplications.Sum(ca => ca.Amount);
        var totalPaid = invoiceWithAllocations!.PaymentAllocations
            .Where(a => a.IsActive)
            .Sum(a => a.AllocatedAmount);
        var remainingAmount = invoice.TotalAmount - totalPaid - totalCreditApplied;

        if (request.Amount > remainingAmount)
        {
            return Error.Validation("TenantCredit.ExceedsInvoiceRemaining",
                "Credit amount exceeds the invoice outstanding balance.");
        }

        // Consume the credit amount (supports partial)
        var consumeResult = credit.ConsumeAmount(request.Amount);
        if (!consumeResult.IsSuccess)
        {
            return consumeResult.Errors!;
        }

        // Create immutable CreditApplication record
        var creditApplicationResult = CreditApplication.Create(
            Guid.NewGuid(),
            credit.Id,
            invoice.Id,
            request.Amount,
            DateTime.UtcNow,
            request.IdempotencyKey);

        if (!creditApplicationResult.IsSuccess)
        {
            return creditApplicationResult.Errors!;
        }

        var creditApplication = creditApplicationResult.Value;
        dbContext.CreditApplications.Add(creditApplication);

        // Create CreditUsage ledger entry (NOT CreditCreation)
        var previousBalance = await dbContext.CustomerLedgerEntries
            .Where(e => e.TenantId == credit.TenantId)
            .SumAsync(e => e.EntryType == LedgerEntryType.InvoiceCharge ? e.Amount : -e.Amount, cancellationToken);

        var ledgerEntry = CustomerLedgerEntry.CreateCreditUsage(
            Guid.NewGuid(),
            credit.Id,
            creditApplication.Id,
            invoice.Id,
            request.Amount,
            credit.CurrencyCode,
            previousBalance,
            DateTime.UtcNow);

        if (ledgerEntry.IsSuccess)
        {
            dbContext.CustomerLedgerEntries.Add(ledgerEntry.Value);
        }

        // Update invoice payment status to reflect credit settlement
        var updateResult = invoice.UpdatePaymentStatus();
        if (!updateResult.IsSuccess)
        {
            return updateResult.Errors!;
        }

        dbContext.StampAddedTenantIds(credit.TenantId!);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }
            return Error.Conflict("CreditApplication.ConcurrencyConflict",
                "This credit application conflicted with another concurrent request. Please retry.");
        }
        catch (DbUpdateException ex) when (IsDuplicateKeyException(ex))
        {
            // Concurrent insert created an application with the same IdempotencyKey
            // between our check and our insert. Re-read the persisted application
            // and compare the complete payload to distinguish:
            //   - Same key + same payload → idempotent retry (return success)
            //   - Same key + different payload → conflict (reject)
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            // Clear ChangeTracker to discard stale tracked state from the rolled-back
            // transaction. Without this, the re-read may return the locally-tracked
            // (uncommitted) entity instead of the persisted one.
            if (dbContext is DbContext dbc)
            {
                dbc.ChangeTracker.Clear();
            }

            // Re-read the persisted CreditApplication using the unique constraint
            // (TenantId, IdempotencyKey).
            var persistedApp = await dbContext.CreditApplications
                .Where(ca => ca.TenantId == credit.TenantId
                    && ca.IdempotencyKey == request.IdempotencyKey)
                .FirstOrDefaultAsync(cancellationToken);

            if (persistedApp is null)
            {
                // Defensive: should not happen since we got a duplicate key error.
                return Error.Conflict("CreditApplication.ConcurrencyConflict",
                    "Credit application could not be verified after concurrent insert.");
            }

            // Compare the complete logical request payload.
            if (persistedApp.CreditId == credit.Id
                && persistedApp.InvoiceId == request.InvoiceId
                && persistedApp.Amount == request.Amount)
            {
                // Idempotent retry — same logical request. Return success without
                // consuming additional credit or creating another application.
                return Result.Updated;
            }

            // Same IdempotencyKey but different payload — deterministic conflict.
            return TenantCreditErrors.IdempotencyKeyConflict;
        }
        catch (Exception ex) when (IsDeadlockException(ex))
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }
            return Error.Conflict("CreditApplication.ConcurrencyConflict",
                "This credit application conflicted with another concurrent request. Please retry.");
        }

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        await auditWriter.WriteAsync(
            action: "TenantCredit.ApplyToInvoice",
            entityType: nameof(TenantCredit),
            entityId: credit.Id.ToString(),
            newValue: AuditPayload.Serialize(new
            {
                CreditId = credit.Id,
                CreditApplicationId = creditApplication.Id,
                InvoiceId = request.InvoiceId,
                request.Amount,
                request.IdempotencyKey,
                CreditRemaining = credit.RemainingAmount,
                CreditStatus = credit.Status.ToString()
            }),
            cancellationToken: cancellationToken);

        return Result.Updated;
    }

    private static bool IsDuplicateKeyException(DbUpdateException ex)
    {
        if (ex.InnerException is SqlException sqlEx)
        {
            return sqlEx.Number == 2601 || sqlEx.Number == 2627;
        }
        return false;
    }

    private static bool IsDeadlockException(Exception ex)
    {
        var current = ex;
        while (current is not null)
        {
            if (current is SqlException sqlEx && sqlEx.Number == 1205)
            {
                return true;
            }
            current = current.InnerException;
        }
        return false;
    }
}
