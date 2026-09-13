namespace Centerix.SecurityTests;

using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Billing.Installments;
using Centerix.Application.Platform.Billing.Installments.Commands;
using Centerix.Application.Platform.Billing.Installments.Queries;
using Centerix.Domain.Platform.Billing.Installments;
using Centerix.Domain.Platform.Billing.Payments;
using Centerix.Domain.Platform.Billing.Payments.Enums;
using Centerix.Domain.Platform.Contracts;
using Centerix.Domain.Platform.Contracts.Enums;
using Centerix.Domain.Platform.Tenants;
using Centerix.Domain.Platform.Tenants.Enums;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public class Phase8InstallmentCommandTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly IServiceScope _scope;
    private readonly IAppDbContext _dbContext;

    public Phase8InstallmentCommandTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _scope = factory.Services.CreateScope();
        _dbContext = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
    }

    private IServiceScope scope => _scope;

    [Fact]
    public async Task CreateInstallmentSchedule_TenantIdRequired_WhenTenantNotResolved()
    {
        // When no tenant context is resolved, handlers should reject with TenantIdRequired
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var contractId = Guid.NewGuid();

        var command = new CreateInstallmentScheduleCommand(
            contractId,
            [
                new(1, new DateTime(2026, 1, 1), new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), 12000m),
            ]);

        var result = await mediator.Send(command);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Installment.TenantId_Required");
    }

    [Fact]
    public async Task AddInstallment_TenantIdRequired_WhenTenantNotResolved()
    {
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var command = new AddInstallmentCommand(
            Guid.NewGuid(), 1,
            DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow.AddDays(30),
            1000m);

        var result = await mediator.Send(command);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Installment.TenantId_Required");
    }

    [Fact]
    public async Task UpdateInstallment_TenantIdRequired_WhenTenantNotResolved()
    {
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var command = new UpdateInstallmentCommand(
            Guid.NewGuid(),
            DateTime.UtcNow.AddDays(30),
            DateTime.UtcNow,
            DateTime.UtcNow.AddDays(30),
            1000m);

        var result = await mediator.Send(command);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Installment.TenantId_Required");
    }

    [Fact]
    public async Task CancelInstallment_TenantIdRequired_WhenTenantNotResolved()
    {
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var result = await mediator.Send(new CancelInstallmentCommand(Guid.NewGuid()));

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Installment.TenantId_Required");
    }

    [Fact]
    public async Task GetInstallmentSchedule_TenantIdRequired_WhenTenantNotResolved()
    {
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var result = await mediator.Send(new GetInstallmentScheduleQuery(Guid.NewGuid()));

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Installment.TenantId_Required");
    }

    [Fact]
    public async Task GetInstallment_TenantIdRequired_WhenTenantNotResolved()
    {
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var result = await mediator.Send(new GetInstallmentQuery(Guid.NewGuid()));

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Installment.TenantId_Required");
    }

    [Fact]
    public void InstallmentErrors_AllErrorCodes_AreWellFormed()
    {
        // Verify all error codes follow the naming convention
        var errors = new[]
        {
            InstallmentErrors.IdRequired,
            InstallmentErrors.TenantIdRequired,
            InstallmentErrors.ContractIdRequired,
            InstallmentErrors.ContractNotFound,
            InstallmentErrors.ContractNotActive,
            InstallmentErrors.AmountMustBePositive,
            InstallmentErrors.CurrencyRequired,
            InstallmentErrors.DueDateRequired,
            InstallmentErrors.CoveredPeriodRequired,
            InstallmentErrors.CoveredPeriodInvalid,
            InstallmentErrors.CoveredPeriodExceedsContract,
            InstallmentErrors.CoveredPeriodStartBeforeContract,
            InstallmentErrors.GapInSchedule,
            InstallmentErrors.TotalScheduleExceedsContractDuration,
            InstallmentErrors.NotFound,
            InstallmentErrors.CannotUpdatePaidOrCancelled,
            InstallmentErrors.CannotUpdateNonPending,
            InstallmentErrors.CannotCancelPaidOrCancelled,
            InstallmentErrors.CannotCancelHasAllocations,
            InstallmentErrors.CrossTenantAccess,
            InstallmentErrors.AllocationExceedsInstallment,
            InstallmentErrors.SequenceNumberMustBePositive,
        };

        foreach (var error in errors)
        {
            Assert.False(string.IsNullOrWhiteSpace(error.Code));
            Assert.StartsWith("Installment.", error.Code);
        }
    }

    [Fact]
    public void InstallmentErrors_ParameterizedErrors_ReturnCorrectCodes()
    {
        var overlapError = InstallmentErrors.OverlappingPeriod(Guid.NewGuid());
        Assert.Contains("Installment.OverlappingPeriod", overlapError.Code);

        var amountMismatchError = InstallmentErrors.TotalScheduleAmountMismatch(12000m, 10000m);
        Assert.Contains("Installment.TotalSchedule_AmountMismatch", amountMismatchError.Code);

        var currencyMismatchError = InstallmentErrors.CurrencyMismatch("EGP");
        Assert.Contains("Installment.Currency_Mismatch", currencyMismatchError.Code);

        var sequenceError = InstallmentErrors.DuplicateSequenceNumber(3);
        Assert.Contains("Installment.DuplicateSequenceNumber", sequenceError.Code);

        var stateError = InstallmentErrors.InvalidStateTransition(InstallmentStatus.Paid, "update");
        Assert.Contains("Installment.InvalidStateTransition", stateError.Code);
    }
}
