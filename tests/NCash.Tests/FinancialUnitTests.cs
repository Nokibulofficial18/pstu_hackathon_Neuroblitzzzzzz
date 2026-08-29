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

public class FinancialUnitTests
{
    private readonly NCashDbContext _context;
    private readonly IAccountRepository _accountRepository;
    private readonly ITransactionRepository _transactionRepository;
    private readonly ILedgerRepository _ledgerRepository;
    private readonly IIdempotencyRepository _idempotencyRepository;
    private readonly IRiskShieldService _riskShieldService;
    private readonly IPaymentEngine _paymentEngine;

    public FinancialUnitTests()
    {
        var options = new DbContextOptionsBuilder<NCashDbContext>()
            .UseInMemoryDatabase(databaseName: $"NCash_FinancialUnits_{Guid.NewGuid():N}")
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

    private async Task<(User Sender, Account SenderAcc, User Receiver, Account ReceiverAcc)> SetupAccountsAsync(decimal senderBalance = 5000m, decimal receiverBalance = 1000m)
    {
        var senderUser = new User("Alice Sender", "alice", "alice@test.local", "+8801711111111", "hash");
        var senderAccount = new Account(senderUser.Id, $"ACC-{Guid.NewGuid().ToString("N")[..6]}", senderBalance, "BDT");
        senderUser.SetAccount(senderAccount);

        var receiverUser = new User("Bob Receiver", "bob", "bob@test.local", "+8801722222222", "hash");
        var receiverAccount = new Account(receiverUser.Id, $"ACC-{Guid.NewGuid().ToString("N")[..6]}", receiverBalance, "BDT");
        receiverUser.SetAccount(receiverAccount);

        await _context.Users.AddRangeAsync(senderUser, receiverUser);
        await _context.Accounts.AddRangeAsync(senderAccount, receiverAccount);
        await _context.SaveChangesAsync();

        return (senderUser, senderAccount, receiverUser, receiverAccount);
    }

    [Fact]
    public async Task Test_1_Reject_Zero_Amount_Transfer()
    {
        var (_, senderAcc, _, receiverAcc) = await SetupAccountsAsync(5000m, 1000m);
        var command = new ExecuteTransferCommand(
            SenderAccountId: senderAcc.Id,
            ReceiverAccountId: receiverAcc.Id,
            Amount: 0m,
            IdempotencyKey: Guid.NewGuid().ToString(),
            Type: TransactionType.Transfer,
            Purpose: "Zero Amount Test");

        var act = async () => await _paymentEngine.ExecutePaymentAsync(command);
        var ex = await act.Should().ThrowAsync<DomainException>();
        ex.Which.ErrorCode.Should().Be(ErrorCodes.InvalidAmount);

        senderAcc.Balance.Should().Be(5000m);
        receiverAcc.Balance.Should().Be(1000m);
    }

    [Fact]
    public async Task Test_2_Reject_Negative_Amount_Transfer()
    {
        var (_, senderAcc, _, receiverAcc) = await SetupAccountsAsync(5000m, 1000m);
        var command = new ExecuteTransferCommand(
            SenderAccountId: senderAcc.Id,
            ReceiverAccountId: receiverAcc.Id,
            Amount: -500m,
            IdempotencyKey: Guid.NewGuid().ToString(),
            Type: TransactionType.Transfer,
            Purpose: "Negative Amount Test");

        var act = async () => await _paymentEngine.ExecutePaymentAsync(command);
        var ex = await act.Should().ThrowAsync<DomainException>();
        ex.Which.ErrorCode.Should().Be(ErrorCodes.InvalidAmount);

        senderAcc.Balance.Should().Be(5000m);
        receiverAcc.Balance.Should().Be(1000m);
    }

    [Fact]
    public async Task Test_3_Reject_Self_Transfer()
    {
        var (_, senderAcc, _, _) = await SetupAccountsAsync(5000m, 1000m);
        var command = new ExecuteTransferCommand(
            SenderAccountId: senderAcc.Id,
            ReceiverAccountId: senderAcc.Id,
            Amount: 1000m,
            IdempotencyKey: Guid.NewGuid().ToString(),
            Type: TransactionType.Transfer,
            Purpose: "Self Transfer Test");

        var act = async () => await _paymentEngine.ExecutePaymentAsync(command);
        var ex = await act.Should().ThrowAsync<DomainException>();
        ex.Which.ErrorCode.Should().Be(ErrorCodes.SelfTransferNotAllowed);

        senderAcc.Balance.Should().Be(5000m);
    }

    [Fact]
    public async Task Test_4_Reject_Unknown_Receiver()
    {
        var (_, senderAcc, _, _) = await SetupAccountsAsync(5000m, 1000m);
        var unknownReceiverId = Guid.NewGuid();

        var command = new ExecuteTransferCommand(
            SenderAccountId: senderAcc.Id,
            ReceiverAccountId: unknownReceiverId,
            Amount: 1000m,
            IdempotencyKey: Guid.NewGuid().ToString(),
            Type: TransactionType.Transfer,
            Purpose: "Unknown Receiver Test");

        var act = async () => await _paymentEngine.ExecutePaymentAsync(command);
        var ex = await act.Should().ThrowAsync<DomainException>();
        ex.Which.ErrorCode.Should().Be(ErrorCodes.RecipientNotFound);

        senderAcc.Balance.Should().Be(5000m);
    }

    [Fact]
    public async Task Test_5_Reject_Insufficient_Balance()
    {
        var (_, senderAcc, _, receiverAcc) = await SetupAccountsAsync(1000m, 500m);
        var command = new ExecuteTransferCommand(
            SenderAccountId: senderAcc.Id,
            ReceiverAccountId: receiverAcc.Id,
            Amount: 1500m,
            IdempotencyKey: Guid.NewGuid().ToString(),
            Type: TransactionType.Transfer,
            Purpose: "Insufficient Funds Test");

        var act = async () => await _paymentEngine.ExecutePaymentAsync(command);
        var ex = await act.Should().ThrowAsync<DomainException>();
        ex.Which.ErrorCode.Should().Be(ErrorCodes.InsufficientFunds);

        senderAcc.Balance.Should().Be(1000m);
        receiverAcc.Balance.Should().Be(500m);
    }

    [Fact]
    public async Task Test_6_Risk_Score_Calculation_All_5_Rules_And_Bands()
    {
        var (senderUser, senderAcc, _, receiverAcc) = await SetupAccountsAsync(100000m, 1000m);

        // Rule 1: New Recipient (+30) -> Score 30 (LOW Band: 0-30)
        var risk1 = await _riskShieldService.AssessTransferRiskAsync(senderAcc.Id, receiverAcc.Id, 1000m);
        risk1.TotalScore.Should().Be(30);
        risk1.Level.Should().Be(RiskLevel.Low);
        risk1.Signals.Should().Contain(s => s.RuleCode == "NEW_RECIPIENT");

        // Rule 2: Unusually Large Amount (+25) with New Recipient (+30) -> Score 55 (MEDIUM Band: 31-60)
        var risk2 = await _riskShieldService.AssessTransferRiskAsync(senderAcc.Id, receiverAcc.Id, 30000m);
        risk2.TotalScore.Should().Be(55);
        risk2.Level.Should().Be(RiskLevel.Medium);
        risk2.Signals.Should().Contain(s => s.RuleCode == "LARGE_AMOUNT");
    }

    [Fact]
    public void Test_7_MoneyRequest_State_Transitions()
    {
        var requesterId = Guid.NewGuid();
        var payerId = Guid.NewGuid();

        // 1. Initial creation -> Pending
        var req = new MoneyRequest(requesterId, payerId, 5000m, "Trip share", DateTime.UtcNow.AddDays(7));
        req.Status.Should().Be(MoneyRequestStatus.Pending);
        req.PaidAmount.Should().Be(0m);
        req.RemainingAmount.Should().Be(5000m);

        // 2. Partial payment -> PartiallyPaid
        req.ApplyPayment(2000m);
        req.Status.Should().Be(MoneyRequestStatus.PartiallyPaid);
        req.PaidAmount.Should().Be(2000m);
        req.RemainingAmount.Should().Be(3000m);

        // 3. Full remaining payment -> Paid
        req.ApplyPayment(3000m);
        req.Status.Should().Be(MoneyRequestStatus.Paid);
        req.PaidAmount.Should().Be(5000m);
        req.RemainingAmount.Should().Be(0m);

        // 4. Overpayment rejection
        var overpayReq = new MoneyRequest(requesterId, payerId, 1000m, "Test");
        var overpayAction = () => overpayReq.ApplyPayment(1500m);
        overpayAction.Should().Throw<DomainException>();

        // 5. Rejection & Cancellation
        var rejectReq = new MoneyRequest(requesterId, payerId, 1000m, "Test");
        rejectReq.Reject();
        rejectReq.Status.Should().Be(MoneyRequestStatus.Rejected);

        var cancelReq = new MoneyRequest(requesterId, payerId, 1000m, "Test");
        cancelReq.Cancel();
        cancelReq.Status.Should().Be(MoneyRequestStatus.Cancelled);
    }

    [Fact]
    public void Test_8_Transaction_State_Transitions()
    {
        var senderId = Guid.NewGuid();
        var receiverId = Guid.NewGuid();
        var txn = new Transaction("TXN-001", senderId, receiverId, 1000m, "IDEMP-001", TransactionType.Transfer, "Test");

        // 1. Initial State -> Created
        txn.Status.Should().Be(TransactionStatus.Created);

        // 2. Mark Processing
        txn.MarkProcessing();
        txn.Status.Should().Be(TransactionStatus.Processing);

        // 3. Mark Succeeded
        txn.MarkSucceeded();
        txn.Status.Should().Be(TransactionStatus.Succeeded);
        txn.CompletedAtUtc.Should().NotBeNull();

        // 4. Recovery State Machine: Created -> Processing -> Unknown -> Recovering -> Succeeded
        var stuckTxn = new Transaction("TXN-002", senderId, receiverId, 1000m, "IDEMP-002", TransactionType.Transfer, "Stuck");
        stuckTxn.MarkProcessing();
        stuckTxn.MarkUnknown("Network timeout simulated");
        stuckTxn.Status.Should().Be(TransactionStatus.Unknown);

        stuckTxn.MarkRecovering();
        stuckTxn.Status.Should().Be(TransactionStatus.Recovering);

        stuckTxn.MarkSucceeded();
        stuckTxn.Status.Should().Be(TransactionStatus.Succeeded);
    }
}
