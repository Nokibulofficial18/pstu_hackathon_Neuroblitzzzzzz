using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NCash.Application.Contracts.Persistence;
using NCash.Application.Contracts.Security;
using NCash.Application.Modules.PaymentEngine;
using NCash.Application.Modules.PaymentEngine.DTOs;
using NCash.Application.Modules.RiskShield;
using NCash.Domain.Common;
using NCash.Domain.Entities;
using NCash.Domain.Enums;
using NCash.Infrastructure.Persistence;
using NCash.Infrastructure.Repositories;
using NCash.Infrastructure.Security;
using Xunit;

namespace NCash.Tests;

public class PaymentEngineTests
{
    private readonly NCashDbContext _context;
    private readonly IAccountRepository _accountRepository;
    private readonly ITransactionRepository _transactionRepository;
    private readonly ILedgerRepository _ledgerRepository;
    private readonly IIdempotencyRepository _idempotencyRepository;
    private readonly IRiskShieldService _riskShieldService;
    private readonly IPaymentEngine _paymentEngine;

    public PaymentEngineTests()
    {
        var options = new DbContextOptionsBuilder<NCashDbContext>()
            .UseInMemoryDatabase(databaseName: $"NCash_Test_{Guid.NewGuid():N}")
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
    }

    private async Task<(User User1, Account Account1, User User2, Account Account2)> SeedTwoAccountsAsync(decimal balance1 = 10000m, decimal balance2 = 5000m)
    {
        var u1 = new User("Alice Tester", "alice", "alice@test.local", $"+88017{new Random().Next(1000000, 9999999)}", "hash");
        var a1 = new Account(u1.Id, $"ACC-{new Random().Next(100000, 999999)}", balance1, "BDT");
        u1.SetAccount(a1);

        var u2 = new User("Bob Tester", "bob", "bob@test.local", $"+88017{new Random().Next(1000000, 9999999)}", "hash");
        var a2 = new Account(u2.Id, $"ACC-{new Random().Next(100000, 999999)}", balance2, "BDT");
        u2.SetAccount(a2);

        await _context.Users.AddRangeAsync(u1, u2);
        await _context.Accounts.AddRangeAsync(a1, a2);
        await _context.SaveChangesAsync();

        return (u1, a1, u2, a2);
    }

    [Fact]
    public async Task ExecutePayment_ValidTransfer_AtomicallyDeductsSenderAndCreditsReceiver()
    {
        // Arrange
        var (_, a1, _, a2) = await SeedTwoAccountsAsync(10000m, 2000m);
        var transferAmount = 2500m;
        var idempotencyKey = $"IDEMP-{Guid.NewGuid():N}";

        var command = new ExecuteTransferCommand(
            a1.Id,
            a2.Id,
            transferAmount,
            idempotencyKey,
            TransactionType.Transfer,
            "Dinner split");

        // Act
        var result = await _paymentEngine.ExecutePaymentAsync(command);

        // Assert
        result.Should().NotBeNull();
        result.Status.Should().Be("Succeeded");
        result.PreviousSenderBalance.Should().Be(10000m);
        result.SenderNewBalance.Should().Be(7500m);
        result.ReceiverNewBalance.Should().Be(4500m);
        result.ZeroVarianceVerified.Should().BeTrue();
        result.LedgerDelta.Should().Be(0m);
        result.IsCached.Should().BeFalse();

        // Verify in DB
        var freshA1 = await _context.Accounts.FindAsync(a1.Id);
        var freshA2 = await _context.Accounts.FindAsync(a2.Id);
        freshA1!.Balance.Should().Be(7500m);
        freshA2!.Balance.Should().Be(4500m);

        // Verify Double-Entry Ledger Entries
        var ledgerEntries = await _context.LedgerEntries.Where(l => l.TransactionId == result.TransactionId).ToListAsync();
        ledgerEntries.Should().HaveCount(2);

        var debit = ledgerEntries.Single(l => l.Direction == LedgerDirection.Debit);
        debit.AccountId.Should().Be(a1.Id);
        debit.Amount.Should().Be(transferAmount);
        debit.BalanceAfter.Should().Be(7500m);

        var credit = ledgerEntries.Single(l => l.Direction == LedgerDirection.Credit);
        credit.AccountId.Should().Be(a2.Id);
        credit.Amount.Should().Be(transferAmount);
        credit.BalanceAfter.Should().Be(4500m);
    }

    [Fact]
    public async Task ExecutePayment_InsufficientFunds_ThrowsDomainExceptionAndRollsBack()
    {
        // Arrange
        var (_, a1, _, a2) = await SeedTwoAccountsAsync(1000m, 500m);
        var transferAmount = 5000m; // Exceeds balance
        var idempotencyKey = $"IDEMP-{Guid.NewGuid():N}";

        var command = new ExecuteTransferCommand(
            a1.Id,
            a2.Id,
            transferAmount,
            idempotencyKey,
            TransactionType.Transfer,
            "Excessive spend");

        // Act
        Func<Task> act = async () => await _paymentEngine.ExecutePaymentAsync(command);

        // Assert
        await act.Should().ThrowAsync<DomainException>()
            .Where(e => e.ErrorCode == ErrorCodes.InsufficientFunds);

        // Balances must remain completely untouched
        var freshA1 = await _context.Accounts.FindAsync(a1.Id);
        var freshA2 = await _context.Accounts.FindAsync(a2.Id);
        freshA1!.Balance.Should().Be(1000m);
        freshA2!.Balance.Should().Be(500m);
    }

    [Fact]
    public async Task ExecutePayment_DuplicateIdempotencyKey_ReturnsCachedResultWithoutDoubleDebit()
    {
        // Arrange
        var (_, a1, _, a2) = await SeedTwoAccountsAsync(10000m, 1000m);
        var transferAmount = 2000m;
        var idempotencyKey = $"IDEMP-DUP-{Guid.NewGuid():N}";

        var command = new ExecuteTransferCommand(
            a1.Id,
            a2.Id,
            transferAmount,
            idempotencyKey,
            TransactionType.Transfer,
            "First attempt");

        // Act 1: Initial Transfer
        var result1 = await _paymentEngine.ExecutePaymentAsync(command);
        result1.IsCached.Should().BeFalse();
        result1.SenderNewBalance.Should().Be(8000m);

        // Act 2: Duplicate retry with same idempotency key
        var result2 = await _paymentEngine.ExecutePaymentAsync(command);
        result2.Should().NotBeNull();
        result2.IsCached.Should().BeTrue();
        result2.TransactionId.Should().Be(result1.TransactionId);

        // Assert: Sender must NOT be debited a second time
        var freshA1 = await _context.Accounts.FindAsync(a1.Id);
        freshA1!.Balance.Should().Be(8000m);
    }

    [Fact]
    public async Task ExecutePayment_SelfTransfer_ThrowsDomainException()
    {
        // Arrange
        var (_, a1, _, _) = await SeedTwoAccountsAsync(5000m, 5000m);
        var command = new ExecuteTransferCommand(
            a1.Id,
            a1.Id,
            100m,
            $"IDEMP-{Guid.NewGuid():N}",
            TransactionType.Transfer,
            "Self transfer attempt");

        // Act
        Func<Task> act = async () => await _paymentEngine.ExecutePaymentAsync(command);

        // Assert
        await act.Should().ThrowAsync<DomainException>()
            .Where(e => e.ErrorCode == ErrorCodes.SelfTransferNotAllowed);
    }

    [Fact]
    public async Task ExecutePayment_InvalidPrecision_ThrowsDomainException()
    {
        // Arrange
        var (_, a1, _, a2) = await SeedTwoAccountsAsync(5000m, 1000m);
        var command = new ExecuteTransferCommand(
            a1.Id,
            a2.Id,
            12.3456m, // 4 decimal places
            $"IDEMP-{Guid.NewGuid():N}",
            TransactionType.Transfer,
            "Invalid precision");

        // Act
        Func<Task> act = async () => await _paymentEngine.ExecutePaymentAsync(command);

        // Assert
        await act.Should().ThrowAsync<DomainException>()
            .Where(e => e.ErrorCode == ErrorCodes.InvalidAmount);
    }

    [Fact]
    public async Task ExecutePayment_SimultaneousTransfers_OverdraftScenarioHandledSafely()
    {
        // Scenario: Initial Balance = 5000. Request A = 4000. Request B = 4000.
        // Arrange
        var (_, a1, _, a2) = await SeedTwoAccountsAsync(5000m, 0m);

        var cmdA = new ExecuteTransferCommand(a1.Id, a2.Id, 4000m, $"IDEMP-A-{Guid.NewGuid():N}", TransactionType.Transfer);
        var cmdB = new ExecuteTransferCommand(a1.Id, a2.Id, 4000m, $"IDEMP-B-{Guid.NewGuid():N}", TransactionType.Transfer);

        // Act
        var resA = await _paymentEngine.ExecutePaymentAsync(cmdA);
        resA.Status.Should().Be("Succeeded");

        Func<Task> actB = async () => await _paymentEngine.ExecutePaymentAsync(cmdB);

        // Assert: Second request must fail with InsufficientFunds
        await actB.Should().ThrowAsync<DomainException>()
            .Where(e => e.ErrorCode == ErrorCodes.InsufficientFunds);

        // Final balance must be exactly 1000 BDT
        var freshA1 = await _context.Accounts.FindAsync(a1.Id);
        freshA1!.Balance.Should().Be(1000m);
    }
}
