using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NCash.Application.Contracts.Persistence;
using NCash.Application.Modules.Ledger;
using NCash.Application.Modules.PaymentEngine;
using NCash.Application.Modules.PaymentEngine.DTOs;
using NCash.Application.Modules.RiskShield;
using NCash.Domain.Entities;
using NCash.Domain.Enums;
using NCash.Infrastructure.Persistence;
using NCash.Infrastructure.Repositories;
using Xunit;

namespace NCash.Tests;

public class RiskShieldAndLedgerTests
{
    private readonly NCashDbContext _context;
    private readonly ITransactionRepository _transactionRepository;
    private readonly IAccountRepository _accountRepository;
    private readonly ILedgerRepository _ledgerRepository;
    private readonly IIdempotencyRepository _idempotencyRepository;
    private readonly IRiskShieldService _riskShieldService;
    private readonly ILedgerService _ledgerService;
    private readonly IPaymentEngine _paymentEngine;

    public RiskShieldAndLedgerTests()
    {
        var options = new DbContextOptionsBuilder<NCashDbContext>()
            .UseInMemoryDatabase(databaseName: $"NCash_RiskLedger_{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _context = new NCashDbContext(options);
        _accountRepository = new AccountRepository(_context);
        _transactionRepository = new TransactionRepository(_context);
        _ledgerRepository = new LedgerRepository(_context);
        _idempotencyRepository = new IdempotencyRepository(_context);
        _riskShieldService = new RiskShieldService(_context, _transactionRepository, NullLogger<RiskShieldService>.Instance);
        _ledgerService = new LedgerService(_context, _accountRepository, _ledgerRepository);

        _paymentEngine = new PaymentEngine(
            _context,
            _accountRepository,
            _transactionRepository,
            _ledgerRepository,
            _idempotencyRepository,
            _riskShieldService,
            NullLogger<PaymentEngine>.Instance);
    }

    [Fact]
    public async Task RiskShield_NewRecipient_FlagsNewRecipientSignal()
    {
        // Arrange
        var u1 = new User("User A", "usera", "a@test.local", "+8801700000001", "pass");
        var a1 = new Account(u1.Id, "ACC-A", 10000m, "BDT");
        var u2 = new User("User B", "userb", "b@test.local", "+8801700000002", "pass");
        var a2 = new Account(u2.Id, "ACC-B", 10000m, "BDT");

        await _context.Users.AddRangeAsync(u1, u2);
        await _context.Accounts.AddRangeAsync(a1, a2);
        await _context.SaveChangesAsync();

        // Act: 1000 BDT to new recipient
        var risk = await _riskShieldService.AssessTransferRiskAsync(a1.Id, a2.Id, 1000m);

        // Assert: 30 pts -> Low risk band (0-30), no confirmation needed
        risk.Should().NotBeNull();
        risk.Signals.Should().Contain(s => s.RuleCode == "NEW_RECIPIENT");
        risk.TotalScore.Should().Be(30);
        risk.Level.Should().Be(RiskLevel.Low);
        risk.RequiresStepUpConfirmation.Should().BeFalse();
    }

    [Fact]
    public async Task RiskShield_LargeAmount_FlagsLargeAmountSignal()
    {
        // Arrange
        var u1 = new User("User A", "usera", "a@test.local", "+8801700000001", "pass");
        var a1 = new Account(u1.Id, "ACC-A", 100000m, "BDT");
        var u2 = new User("User B", "userb", "b@test.local", "+8801700000002", "pass");
        var a2 = new Account(u2.Id, "ACC-B", 10000m, "BDT");

        await _context.Users.AddRangeAsync(u1, u2);
        await _context.Accounts.AddRangeAsync(a1, a2);
        await _context.SaveChangesAsync();

        // Act: 50,000 BDT transfer (> 25,000 threshold) to a new recipient
        // New recipient (+30) + Large amount (+25) = 55 pts -> Medium risk (31-60)
        var risk = await _riskShieldService.AssessTransferRiskAsync(a1.Id, a2.Id, 50000m);

        // Assert
        risk.Signals.Should().Contain(s => s.RuleCode == "LARGE_AMOUNT");
        risk.Signals.Should().Contain(s => s.RuleCode == "NEW_RECIPIENT");
        risk.TotalScore.Should().Be(55);
        risk.Level.Should().Be(RiskLevel.Medium);
        risk.RequiresStepUpConfirmation.Should().BeTrue();
        risk.RequiresPinVerification.Should().BeFalse();
    }

    [Fact]
    public async Task RiskShield_BurstVelocity_FlagsBurstVelocitySignal()
    {
        // Arrange
        var u1 = new User("User Velocity", "vel", "vel@test.local", "+8801700000030", "pass");
        var a1 = new Account(u1.Id, "ACC-VEL", 50000m, "BDT");
        var u2 = new User("User Recv", "recv", "recv@test.local", "+8801700000040", "pass");
        var a2 = new Account(u2.Id, "ACC-RECV", 10000m, "BDT");

        await _context.Users.AddRangeAsync(u1, u2);
        await _context.Accounts.AddRangeAsync(a1, a2);
        await _context.SaveChangesAsync();

        // Seed 3 rapid recent transactions in the last 1 minute
        for (int i = 0; i < 3; i++)
        {
            var txn = new Transaction($"TXN-BURST-{i}", a1.Id, a2.Id, 100m, $"KEY-{i}", TransactionType.Transfer);
            txn.MarkSucceeded();
            await _context.Transactions.AddAsync(txn);
        }
        await _context.SaveChangesAsync();

        // Act
        var risk = await _riskShieldService.AssessTransferRiskAsync(a1.Id, a2.Id, 500m);

        // Assert: Burst velocity (+20) flagged
        risk.Signals.Should().Contain(s => s.RuleCode == "BURST_VELOCITY");
    }

    [Fact]
    public async Task RiskShield_FailedPinAttempts_FlagsFailedPinSignal()
    {
        // Arrange
        var u1 = new User("User FailedPIN", "fpin", "fpin@test.local", "+8801700000050", "pass");
        var a1 = new Account(u1.Id, "ACC-FPIN", 50000m, "BDT");
        var u2 = new User("User Recv2", "recv2", "recv2@test.local", "+8801700000060", "pass");
        var a2 = new Account(u2.Id, "ACC-RECV2", 10000m, "BDT");

        await _context.Users.AddRangeAsync(u1, u2);
        await _context.Accounts.AddRangeAsync(a1, a2);

        // Seed failed PIN attempt audit log in last 5 minutes
        var failedLog = new SystemAuditLog(u1.Id, "FAILED_PIN", "User", u1.Id.ToString());
        await _context.SystemAuditLogs.AddAsync(failedLog);
        await _context.SaveChangesAsync();

        // Act
        var risk = await _riskShieldService.AssessTransferRiskAsync(a1.Id, a2.Id, 500m);

        // Assert: New recipient (+30) + Failed PIN (+15) = 45 pts
        risk.Signals.Should().Contain(s => s.RuleCode == "FAILED_PIN_ATTEMPTS");
        risk.TotalScore.Should().Be(45);
        risk.Level.Should().Be(RiskLevel.Medium);
    }

    [Fact]
    public async Task RiskShield_CombinedHighRisk_TriggersHighRiskBandAndRequiresPin()
    {
        // Arrange: New recipient (+30) + Large amount (+25) + Failed PIN (+15) = 70 pts -> HIGH RISK (61-100)
        var u1 = new User("High Risk Sender", "hr", "hr@test.local", "+8801700000070", "pass");
        var a1 = new Account(u1.Id, "ACC-HR", 200000m, "BDT");
        var u2 = new User("Recv High", "rh", "rh@test.local", "+8801700000080", "pass");
        var a2 = new Account(u2.Id, "ACC-RH", 10000m, "BDT");

        await _context.Users.AddRangeAsync(u1, u2);
        await _context.Accounts.AddRangeAsync(a1, a2);

        var failedLog = new SystemAuditLog(u1.Id, "FAILED_LOGIN", "User", u1.Id.ToString());
        await _context.SystemAuditLogs.AddAsync(failedLog);
        await _context.SaveChangesAsync();

        // Act: 60,000 BDT transfer
        var risk = await _riskShieldService.AssessTransferRiskAsync(a1.Id, a2.Id, 60000m);

        // Assert
        risk.TotalScore.Should().Be(70);
        risk.Level.Should().Be(RiskLevel.High);
        risk.RequiresStepUpConfirmation.Should().BeTrue();
        risk.RequiresPinVerification.Should().BeTrue();
        risk.Explanation.Should().Contain("HIGH RISK");
    }

    [Fact]
    public async Task Ledger_MultipleTransfers_MaintainsGlobalZeroVariance()
    {
        // Arrange
        var u1 = new User("User 1", "u1", "u1@test.local", "+880171", "pass");
        var a1 = new Account(u1.Id, "ACC-1", 50000m, "BDT");
        var u2 = new User("User 2", "u2", "u2@test.local", "+880172", "pass");
        var a2 = new Account(u2.Id, "ACC-2", 50000m, "BDT");
        var u3 = new User("User 3", "u3", "u3@test.local", "+880173", "pass");
        var a3 = new Account(u3.Id, "ACC-3", 50000m, "BDT");

        await _context.Users.AddRangeAsync(u1, u2, u3);
        await _context.Accounts.AddRangeAsync(a1, a2, a3);
        await _context.SaveChangesAsync();

        // Execute multiple transfers: A1 -> A2, A2 -> A3, A3 -> A1
        await _paymentEngine.ExecutePaymentAsync(new ExecuteTransferCommand(a1.Id, a2.Id, 10000m, "KEY-1", TransactionType.Transfer));
        await _paymentEngine.ExecutePaymentAsync(new ExecuteTransferCommand(a2.Id, a3.Id, 5000m, "KEY-2", TransactionType.Transfer));
        await _paymentEngine.ExecutePaymentAsync(new ExecuteTransferCommand(a3.Id, a1.Id, 2500m, "KEY-3", TransactionType.Transfer));

        // Act
        var reconciliation = await _ledgerService.GetGlobalReconciliationAsync();

        // Assert: Total debits must strictly equal total credits
        reconciliation.TotalDebits.Should().Be(17500m);
        reconciliation.TotalCredits.Should().Be(17500m);
        reconciliation.NetVariance.Should().Be(0m);
        reconciliation.IsZeroVariance.Should().BeTrue();
    }
}
