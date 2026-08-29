using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NCash.Application.Contracts.Persistence;
using NCash.Application.Modules.MoneyRequests;
using NCash.Application.Modules.PaymentEngine;
using NCash.Application.Modules.PaymentEngine.DTOs;
using NCash.Application.Modules.RecoveryCenter;
using NCash.Application.Modules.RiskShield;
using NCash.Application.Modules.TrustLab;
using NCash.Domain.Common;
using NCash.Domain.Entities;
using NCash.Domain.Enums;
using NCash.Infrastructure.Persistence;
using NCash.Infrastructure.Repositories;
using Xunit;

namespace NCash.Tests;

public class LedgerAuditTests
{
    private (NCashDbContext Context, IPaymentEngine PaymentEngine, ITrustLabService TrustLab) CreateEnvironment()
    {
        var options = new DbContextOptionsBuilder<NCashDbContext>()
            .UseInMemoryDatabase(databaseName: $"NCash_LedgerAudit_{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        var context = new NCashDbContext(options);
        var accRepo = new AccountRepository(context);
        var txnRepo = new TransactionRepository(context);
        var ledRepo = new LedgerRepository(context);
        var idempRepo = new IdempotencyRepository(context);
        var risk = new RiskShieldService(context, txnRepo, NullLogger<RiskShieldService>.Instance);

        var paymentEngine = new PaymentEngine(
            context,
            accRepo,
            txnRepo,
            ledRepo,
            idempRepo,
            risk,
            NullLogger<PaymentEngine>.Instance);

        var moneyReqService = new MoneyRequestService(context, accRepo, paymentEngine, NullLogger<MoneyRequestService>.Instance);
        var recoveryService = new RecoveryCenterService(context, txnRepo, ledRepo, accRepo, paymentEngine, NullLogger<RecoveryCenterService>.Instance);

        var trustLab = new TrustLabService(
            context,
            paymentEngine,
            accRepo,
            ledRepo,
            txnRepo,
            moneyReqService,
            recoveryService,
            NullLogger<TrustLabService>.Instance);

        return (context, paymentEngine, trustLab);
    }

    [Fact]
    public async Task Test_1_Every_Completed_Transfer_Generates_Equal_Debit_And_Credit_Ledger_Entries()
    {
        var (context, paymentEngine, _) = CreateEnvironment();

        var u1 = new User("Alice", "alice", "alice@test.local", "+8801700000001", "hash");
        var a1 = new Account(u1.Id, "ACC-L1", 10000m, "BDT");
        u1.SetAccount(a1);

        var u2 = new User("Bob", "bob", "bob@test.local", "+8801700000002", "hash");
        var a2 = new Account(u2.Id, "ACC-L2", 5000m, "BDT");
        u2.SetAccount(a2);

        await context.Users.AddRangeAsync(u1, u2);
        await context.Accounts.AddRangeAsync(a1, a2);
        await context.SaveChangesAsync();

        // Execute Transfer of 3750.50 BDT
        var req = new ExecuteTransferCommand(a1.Id, a2.Id, 3750.50m, $"IDEMP-{Guid.NewGuid():N}", TransactionType.Transfer, "Ledger Entry Test");
        var res = await paymentEngine.ExecutePaymentAsync(req);
        res.TransactionId.Should().NotBeEmpty();

        var entries = await context.LedgerEntries.Where(l => l.TransactionId == res.TransactionId).ToListAsync();
        entries.Should().HaveCount(2);

        var debit = entries.Single(e => e.Direction == LedgerDirection.Debit);
        var credit = entries.Single(e => e.Direction == LedgerDirection.Credit);

        debit.Amount.Should().Be(3750.50m);
        credit.Amount.Should().Be(3750.50m);
        debit.Amount.Should().Be(credit.Amount);

        debit.BalanceAfter.Should().Be(6249.50m);
        credit.BalanceAfter.Should().Be(8750.50m);
    }

    [Fact]
    public async Task Test_2_System_Level_Ledger_Integrity_Audit_Guarantees_Zero_Variance()
    {
        var (context, paymentEngine, trustLab) = CreateEnvironment();

        // Create 5 accounts and execute 10 mixed transfers
        var users = new List<User>();
        var accounts = new List<Account>();

        for (int i = 0; i < 5; i++)
        {
            var u = new User($"User {i}", $"user{i}", $"u{i}@test.local", $"+88017000000{i}", "hash");
            var a = new Account(u.Id, $"ACC-AUDIT-{i}", 20000m, "BDT");
            u.SetAccount(a);
            users.Add(u);
            accounts.Add(a);
        }

        await context.Users.AddRangeAsync(users);
        await context.Accounts.AddRangeAsync(accounts);
        await context.SaveChangesAsync();

        var rng = new Random(42);
        for (int step = 0; step < 10; step++)
        {
            var senderIdx = rng.Next(0, 5);
            var receiverIdx = (senderIdx + rng.Next(1, 4)) % 5;
            var amount = rng.Next(100, 1500);

            var req = new ExecuteTransferCommand(
                accounts[senderIdx].Id,
                accounts[receiverIdx].Id,
                amount,
                $"IDEMP-STEP-{step}",
                TransactionType.Transfer,
                $"Step {step}");

            await paymentEngine.ExecutePaymentAsync(req);
        }

        // Run System-Level Ledger Audit
        var audit = await trustLab.RunLedgerIntegrityAuditAsync();

        audit.Passed.Should().BeTrue();
        audit.IsZeroVariance.Should().BeTrue();
        audit.Difference.Should().Be(0.00m);
        audit.TotalDebits.Should().Be(audit.TotalCredits);
        audit.TotalDebits.Should().BeGreaterThan(0m);
    }
}
