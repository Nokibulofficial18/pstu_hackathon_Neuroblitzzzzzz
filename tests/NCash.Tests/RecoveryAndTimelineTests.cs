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

public class RecoveryAndTimelineTests
{
    private readonly NCashDbContext _context;
    private readonly IAccountRepository _accountRepository;
    private readonly ITransactionRepository _transactionRepository;
    private readonly ILedgerRepository _ledgerRepository;
    private readonly IIdempotencyRepository _idempotencyRepository;
    private readonly IRiskShieldService _riskShieldService;
    private readonly IPaymentEngine _paymentEngine;
    private readonly IRecoveryCenterService _recoveryService;
    private readonly ITransferService _transferService;

    public RecoveryAndTimelineTests()
    {
        var options = new DbContextOptionsBuilder<NCashDbContext>()
            .UseInMemoryDatabase(databaseName: $"NCash_Recov_{Guid.NewGuid():N}")
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

        _recoveryService = new RecoveryCenterService(
            _context,
            _transactionRepository,
            _ledgerRepository,
            _accountRepository,
            _paymentEngine,
            NullLogger<RecoveryCenterService>.Instance);

        _transferService = new TransferService(
            _paymentEngine,
            _accountRepository,
            _transactionRepository,
            _riskShieldService,
            NullLogger<TransferService>.Instance);
    }

    private async Task<(User User1, Account Acc1, User User2, Account Acc2)> SeedUsersAsync()
    {
        var u1 = new User("Sender User", "sender1", "sender1@rec.local", "+8801700000081", "pass");
        var a1 = new Account(u1.Id, "ACC-REC-01", 20000m, "BDT");
        u1.SetAccount(a1);

        var u2 = new User("Receiver User", "receiver1", "receiver1@rec.local", "+8801700000082", "pass");
        var a2 = new Account(u2.Id, "ACC-REC-02", 5000m, "BDT");
        u2.SetAccount(a2);

        await _context.Users.AddRangeAsync(u1, u2);
        await _context.Accounts.AddRangeAsync(a1, a2);
        await _context.SaveChangesAsync();

        return (u1, a1, u2, a2);
    }

    [Fact]
    public async Task Transaction_RecordsGranularLifecycleEvents_InTimeline()
    {
        // Arrange
        var (u1, a1, u2, a2) = await SeedUsersAsync();

        // Act: Execute successful transfer
        var result = await _transferService.SendMoneyAsync(
            u1.Id,
            new InitiateTransferDto(a2.AccountNumber, 2500m, "Timeline verification test", false),
            $"IDEMP-TIME-{Guid.NewGuid():N}");

        // Assert: Get full transaction detail and verify all timeline events recorded in DB
        var detail = await _transferService.GetTransactionDetailAsync(result.TransactionId, u1.Id);
        detail.Should().NotBeNull();
        detail.Status.Should().Be("Succeeded");

        var eventTypes = detail.Timeline.Select(e => e.EventType).ToList();
        eventTypes.Should().Contain(TransactionEventTypes.Created);
        eventTypes.Should().Contain(TransactionEventTypes.Validated);
        eventTypes.Should().Contain(TransactionEventTypes.Processing);
        eventTypes.Should().Contain(TransactionEventTypes.Debited);
        eventTypes.Should().Contain(TransactionEventTypes.Credited);
        eventTypes.Should().Contain(TransactionEventTypes.Completed);
    }

    [Fact]
    public async Task RecoveryCenter_FileCase_CreatesOpenCaseWithAuditDiagnosis()
    {
        // Arrange
        var (u1, a1, u2, a2) = await SeedUsersAsync();
        var transfer = await _transferService.SendMoneyAsync(
            u1.Id,
            new InitiateTransferDto(a2.AccountNumber, 1000m, "Inquiry transfer", false),
            $"IDEMP-{Guid.NewGuid():N}");

        // Act: File recovery case
        var recoveryCase = await _recoveryService.FileRecoveryCaseAsync(
            u1.Id,
            new CreateRecoveryCaseDto(transfer.TransactionId, "DEDUCTED_NOT_RECEIVED", "Recipient claimed delay in receipt."));

        // Assert
        recoveryCase.Should().NotBeNull();
        recoveryCase.TransactionId.Should().Be(transfer.TransactionId);
        recoveryCase.RecoveryStatus.Should().Be("Open");
        recoveryCase.AuditDiagnosis.Should().Contain("State: Succeeded");
    }

    [Fact]
    public async Task RecoveryCenter_InvestigateUnknownTransaction_WithBalancedLedger_RecoversToCompleted()
    {
        // Arrange: Simulate an UNKNOWN state transaction where ledger commit completed
        var (u1, a1, u2, a2) = await SeedUsersAsync();
        var txn = new Transaction("TXN-UNKNOWN-01", a1.Id, a2.Id, 3000m, "KEY-UNK-1", TransactionType.Transfer);
        txn.MarkUnknown("Simulated network timeout during client acknowledgment");
        await _context.Transactions.AddAsync(txn);

        // Add 2 balanced double-entry ledger entries
        var debit = new LedgerEntry(txn.Id, a1.Id, LedgerDirection.Debit, 3000m, 17000m, "Transfer out");
        var credit = new LedgerEntry(txn.Id, a2.Id, LedgerDirection.Credit, 3000m, 8000m, "Transfer in");
        await _context.LedgerEntries.AddRangeAsync(debit, credit);

        var recCase = new DisputeCase(txn.Id, u1.Id, "TRANSACTION_STUCK", "Stuck in unknown status");
        await _context.DisputeCases.AddAsync(recCase);
        await _context.SaveChangesAsync();

        // Act: Run automated investigation & recovery
        var resolvedCase = await _recoveryService.InvestigateAndResolveCaseAsync(recCase.Id);

        // Assert: Resolved to Succeeded/Completed with zero variance audit confirmation
        resolvedCase.RecoveryStatus.Should().Be("Resolved");
        resolvedCase.Resolution.Should().Contain("complete atomic settlement");

        var updatedTxn = await _context.Transactions.FindAsync(txn.Id);
        updatedTxn!.Status.Should().Be(TransactionStatus.Succeeded);
    }

    [Fact]
    public async Task RecoveryCenter_InvestigateUnknownTransaction_WithNoLedger_RecoversToFailedWithoutDeduction()
    {
        // Arrange: Simulate an UNKNOWN state transaction where database rolled back (0 ledger entries)
        var (u1, a1, u2, a2) = await SeedUsersAsync();
        var txn = new Transaction("TXN-UNKNOWN-02", a1.Id, a2.Id, 4000m, "KEY-UNK-2", TransactionType.Transfer);
        txn.MarkUnknown("Simulated network disconnection before DB commit");
        await _context.Transactions.AddAsync(txn);

        var recCase = new DisputeCase(txn.Id, u1.Id, "TRANSACTION_STUCK", "Stuck uncommitted");
        await _context.DisputeCases.AddAsync(recCase);
        await _context.SaveChangesAsync();

        // Act: Run automated investigation & recovery
        var resolvedCase = await _recoveryService.InvestigateAndResolveCaseAsync(recCase.Id);

        // Assert: Safely resolved to FAILED and sender balance is intact
        resolvedCase.RecoveryStatus.Should().Be("Resolved");
        resolvedCase.Resolution.Should().Contain("safely aborted prior to balance deduction");

        var updatedTxn = await _context.Transactions.FindAsync(txn.Id);
        updatedTxn!.Status.Should().Be(TransactionStatus.Failed);
    }

    [Fact]
    public async Task RecoveryCenter_NonParticipantUser_BlockedFromFilingCase()
    {
        // Arrange
        var (u1, a1, u2, a2) = await SeedUsersAsync();
        var u3 = new User("Third Party", "third", "third@rec.local", "+8801700000083", "pass");
        var a3 = new Account(u3.Id, "ACC-REC-03", 1000m, "BDT");
        u3.SetAccount(a3);
        await _context.Users.AddAsync(u3);
        await _context.Accounts.AddAsync(a3);

        var transfer = await _transferService.SendMoneyAsync(
            u1.Id,
            new InitiateTransferDto(a2.AccountNumber, 500m, "Private transfer", false),
            $"IDEMP-{Guid.NewGuid():N}");

        // Act: Third party attempts to file recovery case for u1->u2 transfer
        Func<Task> act = async () => await _recoveryService.FileRecoveryCaseAsync(
            u3.Id,
            new CreateRecoveryCaseDto(transfer.TransactionId, "UNRECOGNIZED_TRANSACTION", "Not my transaction"));

        // Assert
        await act.Should().ThrowAsync<DomainException>()
            .Where(e => e.ErrorCode == ErrorCodes.UnauthorizedAccess);
    }
}
