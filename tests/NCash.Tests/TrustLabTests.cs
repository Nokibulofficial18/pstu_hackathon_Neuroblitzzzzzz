using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NCash.Application.Contracts.Persistence;
using NCash.Application.Modules.Ledger;
using NCash.Application.Modules.MoneyRequests;
using NCash.Application.Modules.PaymentEngine;
using NCash.Application.Modules.PaymentEngine.DTOs;
using NCash.Application.Modules.RecoveryCenter;
using NCash.Application.Modules.RiskShield;
using NCash.Application.Modules.TrustLab;
using NCash.Domain.Entities;
using NCash.Domain.Enums;
using NCash.Infrastructure.Persistence;
using NCash.Infrastructure.Repositories;
using Xunit;

namespace NCash.Tests;

public class TrustLabTests
{
    private readonly NCashDbContext _context;
    private readonly IAccountRepository _accountRepository;
    private readonly ITransactionRepository _transactionRepository;
    private readonly ILedgerRepository _ledgerRepository;
    private readonly IIdempotencyRepository _idempotencyRepository;
    private readonly IRiskShieldService _riskShieldService;
    private readonly IPaymentEngine _paymentEngine;
    private readonly ILedgerService _ledgerService;
    private readonly IMoneyRequestService _moneyRequestService;
    private readonly IRecoveryCenterService _recoveryService;
    private readonly ITrustLabService _trustLabService;

    public TrustLabTests()
    {
        var options = new DbContextOptionsBuilder<NCashDbContext>()
            .UseInMemoryDatabase(databaseName: $"NCash_TrustLab_{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _context = new NCashDbContext(options);
        _accountRepository = new AccountRepository(_context);
        _transactionRepository = new TransactionRepository(_context);
        _ledgerRepository = new LedgerRepository(_context);
        _idempotencyRepository = new IdempotencyRepository(_context);
        _riskShieldService = new RiskShieldService(_context, _transactionRepository, NullLogger<RiskShieldService>.Instance);

        _paymentEngine = new PaymentEngine(
            _context,
            _accountRepository,
            _transactionRepository,
            _ledgerRepository,
            _idempotencyRepository,
            _riskShieldService,
            NullLogger<PaymentEngine>.Instance);

        _ledgerService = new LedgerService(_context, _accountRepository, _ledgerRepository);

        _moneyRequestService = new MoneyRequestService(
            _context,
            _accountRepository,
            _paymentEngine,
            NullLogger<MoneyRequestService>.Instance);

        _recoveryService = new RecoveryCenterService(
            _context,
            _transactionRepository,
            _ledgerRepository,
            _accountRepository,
            _paymentEngine,
            NullLogger<RecoveryCenterService>.Instance);

        _trustLabService = new TrustLabService(
            _context,
            _paymentEngine,
            _accountRepository,
            _ledgerRepository,
            _transactionRepository,
            _moneyRequestService,
            _recoveryService,
            NullLogger<TrustLabService>.Instance);
    }

    private async Task<(User User1, Account Acc1, User User2, Account Acc2)> SeedUsersAsync(decimal balance1 = 100000m, decimal balance2 = 100000m)
    {
        var u1 = new User("Lab User A", "lab_a", "laba@test.local", "+8801700000091", "pass");
        var a1 = new Account(u1.Id, "ACC-LAB-01", balance1, "BDT");
        u1.SetAccount(a1);

        var u2 = new User("Lab User B", "lab_b", "labb@test.local", "+8801700000092", "pass");
        var a2 = new Account(u2.Id, "ACC-LAB-02", balance2, "BDT");
        u2.SetAccount(a2);

        await _context.Users.AddRangeAsync(u1, u2);
        await _context.Accounts.AddRangeAsync(a1, a2);
        await _context.SaveChangesAsync();

        return (u1, a1, u2, a2);
    }

    [Fact]
    public async Task TrustLab_DuplicateTest_5Requests_ProducesExactly1DebitAnd4CachedReceipts()
    {
        // Arrange
        var (u1, _, _, _) = await SeedUsersAsync();

        // Act: Run duplicate simulation with 5 requests
        var result = await _trustLabService.RunDuplicateTestAsync(u1.Id, 2500m);

        // Assert
        result.Passed.Should().BeTrue();
        result.RequestedAttempts.Should().Be(5);
        result.SuccessfulFinancialEffects.Should().Be(1);
        result.DuplicateAttemptsBlocked.Should().Be(4);
        result.TotalDeducted.Should().Be(2500m);
    }

    [Fact]
    public async Task TrustLab_ConcurrencyTest_Two700SpendOn1000Balance_ProducesExactly1SuccessAnd1Rejection()
    {
        // Arrange
        var (u1, _, _, _) = await SeedUsersAsync();

        // Act: Run concurrency simulation
        var result = await _trustLabService.RunConcurrencyTestAsync(u1.Id);

        // Assert
        result.Passed.Should().BeTrue();
        result.InitialBalance.Should().Be(1000m);
        result.SucceededCount.Should().Be(1);
        result.FailedDueToInsufficientFundsCount.Should().Be(1);
        result.FinalBalance.Should().Be(300m);
        result.OverdraftOccurred.Should().BeFalse();
    }

    [Fact]
    public async Task TrustLab_NetworkRetryTest_RecognizesCommittedStateAndReturnsOriginal()
    {
        // Arrange
        var (u1, _, _, _) = await SeedUsersAsync();

        // Act: Run network retry simulation
        var result = await _trustLabService.RunNetworkRetryTestAsync(u1.Id, 1500m);

        // Assert
        result.Passed.Should().BeTrue();
        result.DeductionsCount.Should().Be(1);
        result.TotalDeducted.Should().Be(1500m);
        result.RetryAttemptStatus.Should().Contain("Cached");
    }

    [Fact]
    public async Task TrustLab_TimeoutRecoveryTest_TransitionsThroughUnknownToRecovered()
    {
        // Arrange
        var (u1, _, _, _) = await SeedUsersAsync();

        // Act: Run timeout test
        var result = await _trustLabService.RunTimeoutTestAsync(u1.Id, 3000m);

        // Assert
        result.Passed.Should().BeTrue();
        result.InitialState.Should().Be("Processing");
        result.SimulatedState.Should().Be("Unknown");
        result.FinalResolvedState.Should().Be("Succeeded");
        result.ZeroVarianceConfirmed.Should().BeTrue();
    }

    [Fact]
    public async Task TrustLab_InvalidInputTest_All6InputVectorsSafelyBlockedWithoutMutation()
    {
        // Arrange
        var (u1, _, _, _) = await SeedUsersAsync();

        // Act: Run invalid input test
        var result = await _trustLabService.RunInvalidInputTestAsync(u1.Id);

        // Assert
        result.Passed.Should().BeTrue();
        result.TotalAttempts.Should().Be(6);
        result.TotalSafelyRejected.Should().Be(6);
        result.FinancialMutationsCount.Should().Be(0);
    }

    [Fact]
    public async Task TrustLab_LedgerIntegrity_ZeroVarianceConfirmed()
    {
        // Arrange
        var (u1, a1, u2, a2) = await SeedUsersAsync();

        // Execute atomic P2P transfer (1 Debit, 1 Credit)
        await _paymentEngine.ExecutePaymentAsync(new ExecuteTransferCommand(a1.Id, a2.Id, 1000m, "KEY-INT-1", TransactionType.Transfer, BypassRiskCheck: true));

        // Act: Run ledger integrity audit
        var result = await _trustLabService.RunLedgerIntegrityAuditAsync();

        // Assert
        result.Passed.Should().BeTrue();
        result.HealthStatus.Should().Be("HEALTHY");
        result.Difference.Should().Be(0m);
        result.IsZeroVariance.Should().BeTrue();
    }

    [Fact]
    public async Task TrustLab_RepeatedRequestAcceptTest_BlocksExcessAndThirdPayment()
    {
        // Arrange
        var (u1, _, _, _) = await SeedUsersAsync();

        // Act: Run repeated request accept test
        var result = await _trustLabService.RunRepeatedRequestAcceptTestAsync(u1.Id);

        // Assert
        result.Passed.Should().BeTrue();
        result.RequestedAmount.Should().Be(5000m);
        result.Payment1Amount.Should().Be(2000m);
        result.Payment2Amount.Should().Be(3000m);
        result.TotalPaid.Should().Be(5000m);
        result.FinalRequestStatus.Should().Be("Paid");
        result.Payment3Status.Should().StartWith("Blocked Safely");
    }
}
