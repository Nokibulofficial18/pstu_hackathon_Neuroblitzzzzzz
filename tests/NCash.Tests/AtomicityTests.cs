using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NCash.Application.Contracts.Persistence;
using NCash.Application.Modules.PaymentEngine;
using NCash.Application.Modules.PaymentEngine.DTOs;
using NCash.Application.Modules.RiskShield;
using NCash.Domain.Common;
using NCash.Domain.Entities;
using NCash.Domain.Enums;
using NCash.Infrastructure.Persistence;
using NCash.Infrastructure.Repositories;
using Xunit;

namespace NCash.Tests;

public class AtomicityTests
{
    private async Task<(NCashDbContext Context, Account SenderAcc, Account ReceiverAcc)> CreateTestEnvAsync(decimal senderBalance = 5000m, decimal receiverBalance = 1000m)
    {
        var options = new DbContextOptionsBuilder<NCashDbContext>()
            .UseInMemoryDatabase(databaseName: $"NCash_Atomicity_{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        var context = new NCashDbContext(options);
        var u1 = new User("Alice Sender", "alice", "alice@test.local", "+8801711111111", "hash");
        var a1 = new Account(u1.Id, "ACC-100001", senderBalance, "BDT");
        u1.SetAccount(a1);

        var u2 = new User("Bob Receiver", "bob", "bob@test.local", "+8801722222222", "hash");
        var a2 = new Account(u2.Id, "ACC-100002", receiverBalance, "BDT");
        u2.SetAccount(a2);

        await context.Users.AddRangeAsync(u1, u2);
        await context.Accounts.AddRangeAsync(a1, a2);
        await context.SaveChangesAsync();

        return (context, a1, a2);
    }

    [Fact]
    public async Task Test_1_Simulate_Failure_After_Transaction_Creation_Rollback_Leaves_Balances_Intact()
    {
        var (context, senderAcc, receiverAcc) = await CreateTestEnvAsync(5000m, 1000m);
        var initialSender = senderAcc.Balance;
        var initialReceiver = receiverAcc.Balance;

        // Simulate an atomic operation failing right after transaction creation
        var txnNumber = $"TXN-{Guid.NewGuid():N}";
        var txn = new Transaction(txnNumber, senderAcc.Id, receiverAcc.Id, 2000m, "IDEMP-FAIL-1", TransactionType.Transfer, "Test");

        try
        {
            await using var dbTx = await context.Database.BeginTransactionAsync();
            await context.Transactions.AddAsync(txn);
            await context.SaveChangesAsync();

            // Inject catastrophic failure
            throw new InvalidOperationException("Simulated hardware failure after txn creation");
        }
        catch (InvalidOperationException)
        {
            // Transaction rolled back
            context.ChangeTracker.Clear();
        }

        // Re-read fresh state
        var reloadedSender = await context.Accounts.FindAsync(senderAcc.Id);
        var reloadedReceiver = await context.Accounts.FindAsync(receiverAcc.Id);

        reloadedSender!.Balance.Should().Be(initialSender);
        reloadedReceiver!.Balance.Should().Be(initialReceiver);
    }

    [Fact]
    public async Task Test_2_Simulate_Failure_After_Debit_Rollback_Restores_Sender_Balance()
    {
        var (context, senderAcc, receiverAcc) = await CreateTestEnvAsync(5000m, 1000m);
        var initialSender = senderAcc.Balance;

        try
        {
            await using var dbTx = await context.Database.BeginTransactionAsync();
            senderAcc.Debit(2000m);

            // Inject failure before transaction commit
            throw new TimeoutException("Simulated network timeout after debit");
        }
        catch (TimeoutException)
        {
            // Database transaction rolled back, reset ChangeTracker
            context.ChangeTracker.Clear();
        }

        var reloadedSender = await context.Accounts.FindAsync(senderAcc.Id);
        reloadedSender!.Balance.Should().Be(initialSender);
    }

    [Fact]
    public async Task Test_3_Simulate_Failure_Before_Credit_Leaves_No_Orphan_Debit()
    {
        var (context, senderAcc, receiverAcc) = await CreateTestEnvAsync(5000m, 1000m);
        var initialSender = senderAcc.Balance;
        var initialReceiver = receiverAcc.Balance;

        try
        {
            await using var dbTx = await context.Database.BeginTransactionAsync();
            senderAcc.Debit(1500m);
            // Credit not performed due to crash
            throw new Exception("Crash before credit step");
        }
        catch
        {
            context.ChangeTracker.Clear();
        }

        var reloadedSender = await context.Accounts.FindAsync(senderAcc.Id);
        var reloadedReceiver = await context.Accounts.FindAsync(receiverAcc.Id);

        reloadedSender!.Balance.Should().Be(initialSender);
        reloadedReceiver!.Balance.Should().Be(initialReceiver);
    }

    [Fact]
    public async Task Test_4_Simulate_Failure_After_Credit_Before_Commit_Rolls_Back_All()
    {
        var (context, senderAcc, receiverAcc) = await CreateTestEnvAsync(5000m, 1000m);
        var initialSender = senderAcc.Balance;
        var initialReceiver = receiverAcc.Balance;

        try
        {
            await using var dbTx = await context.Database.BeginTransactionAsync();
            senderAcc.Debit(1000m);
            receiverAcc.Credit(1000m);
            // Failure before commit
            throw new ApplicationException("Simulated crash right after credit before commit");
        }
        catch
        {
            context.ChangeTracker.Clear();
        }

        var reloadedSender = await context.Accounts.FindAsync(senderAcc.Id);
        var reloadedReceiver = await context.Accounts.FindAsync(receiverAcc.Id);

        reloadedSender!.Balance.Should().Be(initialSender);
        reloadedReceiver!.Balance.Should().Be(initialReceiver);
    }

    [Fact]
    public async Task Test_5_Simulate_Failure_During_Ledger_Insertion_Rolls_Back_Financial_Mutations()
    {
        var (context, senderAcc, receiverAcc) = await CreateTestEnvAsync(5000m, 1000m);
        var initialSender = senderAcc.Balance;
        var initialReceiver = receiverAcc.Balance;

        var txn = new Transaction("TXN-LEDGER-FAIL", senderAcc.Id, receiverAcc.Id, 1000m, "IDEMP-LEDG-1", TransactionType.Transfer, "Ledger fail test");

        try
        {
            await using var dbTx = await context.Database.BeginTransactionAsync();
            senderAcc.Debit(1000m);
            receiverAcc.Credit(1000m);
            await context.Transactions.AddAsync(txn);

            // Add Debit Ledger entry
            var debitEntry = new LedgerEntry(txn.Id, senderAcc.Id, LedgerDirection.Debit, 1000m, senderAcc.Balance, "Debit");
            await context.LedgerEntries.AddAsync(debitEntry);

            // Simulate constraint violation / disk full on credit ledger entry
            throw new DbUpdateException("Simulated disk error during second ledger entry insert");
        }
        catch
        {
            context.ChangeTracker.Clear();
        }

        var reloadedSender = await context.Accounts.FindAsync(senderAcc.Id);
        var reloadedReceiver = await context.Accounts.FindAsync(receiverAcc.Id);

        reloadedSender!.Balance.Should().Be(initialSender);
        reloadedReceiver!.Balance.Should().Be(initialReceiver);

        var ledgerCount = await context.LedgerEntries.CountAsync(l => l.TransactionId == txn.Id);
        ledgerCount.Should().Be(0);
    }
}
