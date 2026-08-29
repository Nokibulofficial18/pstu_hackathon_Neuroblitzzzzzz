using Microsoft.Extensions.Logging;
using NCash.Application.Contracts.Persistence;
using NCash.Application.Modules.PaymentEngine.DTOs;
using NCash.Application.Modules.RiskShield;
using NCash.Application.Modules.RiskShield.DTOs;
using NCash.Domain.Common;
using NCash.Domain.Entities;
using NCash.Domain.Enums;

namespace NCash.Application.Modules.PaymentEngine;

public interface ITransferService
{
    Task<TransferResultDto> SendMoneyAsync(
        Guid senderUserId,
        InitiateTransferDto request,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    Task<RiskAssessmentResultDto> PreCheckTransferRiskAsync(
        Guid senderUserId,
        InitiateTransferDto request,
        CancellationToken cancellationToken = default);

    Task<TransactionDetailDto> GetTransactionDetailAsync(
        Guid transactionId,
        Guid currentUserId,
        CancellationToken cancellationToken = default);

    Task<List<TransactionDetailDto>> GetUserTransactionHistoryAsync(
        Guid currentUserId,
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default);
}

public class TransferService : ITransferService
{
    private readonly IPaymentEngine _paymentEngine;
    private readonly IAccountRepository _accountRepository;
    private readonly ITransactionRepository _transactionRepository;
    private readonly IRiskShieldService _riskShieldService;
    private readonly ILogger<TransferService> _logger;

    public TransferService(
        IPaymentEngine paymentEngine,
        IAccountRepository accountRepository,
        ITransactionRepository transactionRepository,
        IRiskShieldService riskShieldService,
        ILogger<TransferService> logger)
    {
        _paymentEngine = paymentEngine;
        _accountRepository = accountRepository;
        _transactionRepository = transactionRepository;
        _riskShieldService = riskShieldService;
        _logger = logger;
    }

    public async Task<RiskAssessmentResultDto> PreCheckTransferRiskAsync(
        Guid senderUserId,
        InitiateTransferDto request,
        CancellationToken cancellationToken = default)
    {
        var senderAccount = await _accountRepository.GetByUserIdAsync(senderUserId, cancellationToken);
        if (senderAccount == null || senderAccount.UserId != senderUserId)
            throw new DomainException(ErrorCodes.AccountNotFound, "Authenticated caller's wallet account does not exist.", 404);

        var receiverAccount = await _accountRepository.GetByAccountNumberAsync(request.ReceiverAccountNumber.Trim(), cancellationToken);
        if (receiverAccount == null)
            throw new DomainException(ErrorCodes.RecipientNotFound, $"Recipient account '{request.ReceiverAccountNumber}' does not exist.", 404);

        if (senderAccount.Id == receiverAccount.Id)
            throw new DomainException(ErrorCodes.SelfTransferNotAllowed, "You cannot transfer funds to your own account.");

        return await _riskShieldService.AssessTransferRiskAsync(senderAccount.Id, receiverAccount.Id, request.Amount, cancellationToken);
    }

    public async Task<TransferResultDto> SendMoneyAsync(
        Guid senderUserId,
        InitiateTransferDto request,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        // Step 1 & 2: Authenticate caller & Validate sender ownership
        var senderAccount = await _accountRepository.GetByUserIdAsync(senderUserId, cancellationToken);
        if (senderAccount == null || senderAccount.UserId != senderUserId)
            throw new DomainException(ErrorCodes.AccountNotFound, "Authenticated caller's wallet account does not exist.", 404);

        // Step 3: Validate recipient exists
        var receiverAccount = await _accountRepository.GetByAccountNumberAsync(request.ReceiverAccountNumber.Trim(), cancellationToken);
        if (receiverAccount == null)
            throw new DomainException(ErrorCodes.RecipientNotFound, $"Recipient account '{request.ReceiverAccountNumber}' does not exist.", 404);

        // Step 4: Reject self-transfer
        if (senderAccount.Id == receiverAccount.Id)
            throw new DomainException(ErrorCodes.SelfTransferNotAllowed, "You cannot transfer funds to your own account.");

        // Step 5: Validate amount > 0
        if (request.Amount <= 0)
            throw new DomainException(ErrorCodes.InvalidAmount, "Transfer amount must be strictly greater than zero.");

        // Step 6: Validate amount precision
        if (decimal.Round(request.Amount, 2) != request.Amount)
            throw new DomainException(ErrorCodes.InvalidAmount, "Transfer amount cannot have more than 2 decimal places.");

        // Step 7: Validate account status
        if (senderAccount.Status != AccountStatus.Active)
            throw new DomainException(ErrorCodes.AccountInactive, $"Your account is {senderAccount.Status}. Transfers are blocked.");

        if (receiverAccount.Status != AccountStatus.Active)
            throw new DomainException(ErrorCodes.AccountInactive, $"Recipient account is {receiverAccount.Status}. Transfers are blocked.");

        // Step 9: Pre-check risk if not already confirmed
        var risk = await _riskShieldService.AssessTransferRiskAsync(senderAccount.Id, receiverAccount.Id, request.Amount, cancellationToken);
        if (risk.RequiresStepUpConfirmation && !request.ConfirmHighRisk)
        {
            throw new DomainException(ErrorCodes.RiskAssessmentHigh,
                $"{risk.Explanation} Please review the risk signals and confirm explicitly.");
        }

        var command = new ExecuteTransferCommand(
            senderAccount.Id,
            receiverAccount.Id,
            request.Amount,
            idempotencyKey,
            TransactionType.Transfer,
            request.Purpose,
            Fee: 0m,
            BypassRiskCheck: false);

        // Step 10-28: Execute complete atomic transfer in PaymentEngine
        return await _paymentEngine.ExecutePaymentAsync(command, cancellationToken);
    }

    public async Task<TransactionDetailDto> GetTransactionDetailAsync(
        Guid transactionId,
        Guid currentUserId,
        CancellationToken cancellationToken = default)
    {
        var txn = await _transactionRepository.GetByIdAsync(transactionId, cancellationToken);
        if (txn == null)
            throw new DomainException(ErrorCodes.TransactionNotFound, "Transaction not found.", 404);

        var currentUserAccount = await _accountRepository.GetByUserIdAsync(currentUserId, cancellationToken);
        if (currentUserAccount == null)
            throw new DomainException(ErrorCodes.AccountNotFound, "User account not found.");

        if (txn.SenderAccountId != currentUserAccount.Id && txn.ReceiverAccountId != currentUserAccount.Id)
        {
            throw new DomainException(ErrorCodes.UnauthorizedAccess, "You are not authorized to view this transaction.", 403);
        }

        return MapToDetailDto(txn);
    }

    public async Task<List<TransactionDetailDto>> GetUserTransactionHistoryAsync(
        Guid currentUserId,
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var account = await _accountRepository.GetByUserIdAsync(currentUserId, cancellationToken);
        if (account == null)
            throw new DomainException(ErrorCodes.AccountNotFound, "User account not found.");

        var transactions = await _transactionRepository.GetAccountHistoryAsync(account.Id, page, pageSize, cancellationToken);
        return transactions.Select(MapToDetailDto).ToList();
    }

    private static TransactionDetailDto MapToDetailDto(Transaction txn)
    {
        var timeline = txn.Events.Select(e => new TransactionEventDto(
            e.EventType,
            e.Description,
            e.MetadataJson,
            e.CreatedAtUtc)).ToList();

        var riskSignals = txn.RiskSignals.Select(r => new RiskSignalDto(
            r.RuleCode,
            r.ScoreImpact,
            r.Reason,
            r.Severity)).ToList();

        var ledgerEntries = txn.LedgerEntries.Select(l => new LedgerEntrySummaryDto(
            l.Id,
            l.Account?.AccountNumber ?? "ACC-UNKNOWN",
            l.Direction.ToString(),
            l.Amount,
            l.BalanceAfter,
            l.Description,
            l.CreatedAtUtc)).ToList();

        var totalDebits = txn.LedgerEntries.Where(l => l.Direction == LedgerDirection.Debit).Sum(l => l.Amount);
        var totalCredits = txn.LedgerEntries.Where(l => l.Direction == LedgerDirection.Credit).Sum(l => l.Amount);
        var variance = totalDebits - totalCredits;
        var isZeroVariance = variance == 0m;
        var reconciliation = new ReconciliationSummaryDto(
            isZeroVariance,
            totalDebits,
            totalCredits,
            variance,
            isZeroVariance ? "Zero Variance Confirmed (Debits == Credits)" : $"Variance Detected ({variance:N2} BDT)");

        return new TransactionDetailDto(
            txn.Id,
            txn.TransactionNumber,
            txn.SenderAccount?.User?.Username,
            txn.SenderAccount?.AccountNumber,
            txn.ReceiverAccount?.User?.Username ?? "Recipient",
            txn.ReceiverAccount?.AccountNumber ?? "ACC-UNKNOWN",
            txn.Amount,
            txn.Fee,
            txn.Status.ToString(),
            txn.Type.ToString(),
            txn.IdempotencyKey,
            txn.Purpose,
            txn.RiskScore,
            txn.RiskLevel.ToString(),
            txn.FailureReason,
            txn.CreatedAtUtc,
            txn.CompletedAtUtc,
            timeline,
            riskSignals,
            ledgerEntries,
            reconciliation);
    }
}
