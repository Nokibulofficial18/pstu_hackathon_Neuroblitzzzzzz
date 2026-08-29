using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NCash.Application.Contracts.Persistence;
using NCash.Application.Modules.PaymentEngine;
using NCash.Application.Modules.PaymentEngine.DTOs;
using NCash.Application.Modules.RecoveryCenter;
using NCash.Application.Modules.RiskShield;
using NCash.Domain.Common;
using NCash.Domain.Entities;
using NCash.Domain.Enums;
using NCash.Infrastructure.Persistence;
using NCash.Infrastructure.Repositories;
using Xunit;

namespace NCash.Tests;

public class RecoveryTests
{
    private (NCashDbContext Context, IRecoveryCenterService RecoveryService, IPaymentEngine PaymentEngine) CreateEnvironment()
    {
        var options = new DbContextOptionsBuilder<NCashDbContext>()
            .UseInMemoryDatabase(databaseName: $"NCash_RecoveryTests_{Guid.NewGuid():N}")
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

        var recoveryService = new RecoveryCenterService(
            context,
            txnRepo,
            ledRepo,
            accRepo,
            paymentEngine,
            NullLogger<RecoveryCenterService>.Instance);

        return (context, recoveryService, paymentEngine);
    }

    [Fact]
    public async Task Test_1_Unknown_Transaction_With_Valid_Ledger_Is_Diagnosed_And_Recovered_To_Succeeded()
    {
        var (context, recoveryService, _) = CreateEnvironment();

        var u1 = new User("Alice", "alice", "alice@rec.local", "+8801700000001", "hash");
        var a1 = new Account(u1.Id, "ACC-REC-1", 5000m, "BDT");
        u1.SetAccount(a1);

        var u2 = new User("Bob", "bob", "bob@rec.local", "+8801700000002", "hash");
        var a2 = new Account(u2.Id, "ACC-REC-2", 2000m, "BDT");
        u2.SetAccount(a2);

        await context.Users.AddRangeAsync(u1, u2);
        await context.Accounts.AddRangeAsync(a1, a2);

        // Transaction stuck in UNKNOWN state but debit & credit were committed
        var txn = new Transaction("TXN-REC-001", a1.Id, a2.Id, 1000m, "IDEMP-REC-1", TransactionType.Transfer, "Stuck transfer");
        txn.MarkProcessing();
        txn.MarkUnknown("Client timed out before receiving ACK");
        await context.Transactions.AddAsync(txn);

        var debit = new LedgerEntry(txn.Id, a1.Id, LedgerDirection.Debit, 1000m, 4000m, "Debit");
        var credit = new LedgerEntry(txn.Id, a2.Id, LedgerDirection.Credit, 1000m, 3000m, "Credit");
        await context.LedgerEntries.AddRangeAsync(debit, credit);
        await context.SaveChangesAsync();

        // File Recovery Case
        var fileRes = await recoveryService.FileRecoveryCaseAsync(u1.Id, new CreateRecoveryCaseDto(
            txn.Id.ToString(),
            "MONEY_DEDUCTED_NOT_RECEIVED",
            "Deducted from balance but recipient claims pending"));

        fileRes.CaseId.Should().NotBeEmpty();
        var caseId = fileRes.CaseId;

        // Run automated diagnostic investigation & reconciliation
        var diagRes = await recoveryService.InvestigateAndResolveCaseAsync(caseId);
        diagRes.RecoveryStatus.Should().Be("Resolved");

        // Reload Transaction
        var reloadedTxn = await context.Transactions.FindAsync(txn.Id);
        reloadedTxn!.Status.Should().Be(TransactionStatus.Succeeded);

        // Confirm Recovery Started and Recovered timeline events were recorded
        var events = await context.TransactionEvents.Where(e => e.TransactionId == txn.Id).ToListAsync();
        events.Should().Contain(e => e.EventType == TransactionEventTypes.RecoveryStarted);
        events.Should().Contain(e => e.EventType == TransactionEventTypes.Recovered);
    }

    [Fact]
    public async Task Test_2_Unknown_Transaction_Without_Ledger_Entries_Is_Diagnosed_As_Failed_Without_Double_Debit()
    {
        var (context, recoveryService, _) = CreateEnvironment();

        var u1 = new User("Alice", "alice", "alice@rec.local", "+8801700000003", "hash");
        var a1 = new Account(u1.Id, "ACC-REC-3", 5000m, "BDT");
        u1.SetAccount(a1);

        var u2 = new User("Bob", "bob", "bob@rec.local", "+8801700000004", "hash");
        var a2 = new Account(u2.Id, "ACC-REC-4", 2000m, "BDT");
        u2.SetAccount(a2);

        await context.Users.AddRangeAsync(u1, u2);
        await context.Accounts.AddRangeAsync(a1, a2);

        // Transaction stuck in UNKNOWN before any ledger debit occurred
        var txn = new Transaction("TXN-REC-002", a1.Id, a2.Id, 1500m, "IDEMP-REC-2", TransactionType.Transfer, "Stuck before debit");
        txn.MarkProcessing();
        txn.MarkUnknown("Database connection dropped during lock");
        await context.Transactions.AddAsync(txn);
        await context.SaveChangesAsync();

        var fileRes = await recoveryService.FileRecoveryCaseAsync(u1.Id, new CreateRecoveryCaseDto(
            txn.Id.ToString(),
            "TRANSACTION_STUCK",
            "Transaction stuck in unknown"));

        fileRes.CaseId.Should().NotBeEmpty();
        var caseId = fileRes.CaseId;

        // Run automated diagnostic reconciliation
        var diagRes = await recoveryService.InvestigateAndResolveCaseAsync(caseId);

        var reloadedTxn = await context.Transactions.FindAsync(txn.Id);
        reloadedTxn!.Status.Should().Be(TransactionStatus.Failed);

        // Account balances strictly untouched
        a1.Balance.Should().Be(5000m);
        a2.Balance.Should().Be(2000m);
    }
}
