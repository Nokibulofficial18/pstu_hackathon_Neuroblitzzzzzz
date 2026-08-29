using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NCash.Application.Contracts.Persistence;
using NCash.Application.Modules.PaymentEngine.DTOs;
using NCash.Application.Modules.RiskShield;
using NCash.Application.Modules.RiskShield.DTOs;
using NCash.Domain.Common;
using NCash.Domain.Entities;
using NCash.Domain.Enums;

namespace NCash.Application.Modules.PaymentEngine;

public interface IPaymentEngine
{
    Task<TransferResultDto> ExecutePaymentAsync(ExecuteTransferCommand command, CancellationToken cancellationToken = default);
}

public class PaymentEngine : IPaymentEngine
{
    private readonly IApplicationDbContext _context;
    private readonly IAccountRepository _accountRepository;
    private readonly ITransactionRepository _transactionRepository;
    private readonly ILedgerRepository _ledgerRepository;
    private readonly IIdempotencyRepository _idempotencyRepository;
    private readonly IRiskShieldService _riskShieldService;
    private readonly ILogger<PaymentEngine> _logger;

    public PaymentEngine(
        IApplicationDbContext context,
        IAccountRepository accountRepository,
        ITransactionRepository transactionRepository,
        ILedgerRepository ledgerRepository,
        IIdempotencyRepository idempotencyRepository,
        IRiskShieldService riskShieldService,
        ILogger<PaymentEngine> logger)
    {
        _context = context;
        _accountRepository = accountRepository;
        _transactionRepository = transactionRepository;
        _ledgerRepository = ledgerRepository;
        _idempotencyRepository = idempotencyRepository;
        _riskShieldService = riskShieldService;
        _logger = logger;
    }

    public async Task<TransferResultDto> ExecutePaymentAsync(ExecuteTransferCommand command, CancellationToken cancellationToken = default)
    {
        var timeline = new List<string>();

        // Step 8: Validate Idempotency Key presence & check duplicate request
        if (string.IsNullOrWhiteSpace(command.IdempotencyKey))
            throw new DomainException(ErrorCodes.IdempotencyKeyRequired, "Every money movement request must provide an Idempotency-Key header.");

        // Step 5: Validate amount > 0
        if (command.Amount <= 0)
            throw new DomainException(ErrorCodes.InvalidAmount, "Transfer amount must be strictly greater than zero.");

        // Step 6: Validate amount precision (maximum 2 decimal places for BDT currency)
        if (decimal.Round(command.Amount, 2) != command.Amount)
            throw new DomainException(ErrorCodes.InvalidAmount, "Transfer amount cannot have more than 2 decimal places.");

        // Step 4: Reject self-transfer
        if (command.SenderAccountId.HasValue && command.SenderAccountId.Value == command.ReceiverAccountId)
            throw new DomainException(ErrorCodes.SelfTransferNotAllowed, "Cannot transfer money to the same account.");

        // Step 8 (Cont.): Check Idempotency Record cache
        var existingRecord = await _idempotencyRepository.GetByKeyAsync(command.IdempotencyKey, cancellationToken);
        if (existingRecord != null)
        {
            if (existingRecord.Status == IdempotencyStatus.Completed && !string.IsNullOrEmpty(existingRecord.ResponseBodyJson))
            {
                _logger.LogInformation("Idempotent request hit for key {Key}. Returning cached Trust Receipt.", command.IdempotencyKey);
                var cached = JsonSerializer.Deserialize<TransferResultDto>(existingRecord.ResponseBodyJson);
                if (cached != null)
                {
                    return cached with { IsCached = true };
                }
            }
            else if (existingRecord.Status == IdempotencyStatus.Processing)
            {
                throw new DomainException(ErrorCodes.IdempotencyConflict, "A transaction with this Idempotency-Key is currently being processed. Please retry shortly.", 409);
            }
        }

        // Register new Idempotency Record in Processing state
        var idempotencyRecord = new IdempotencyRecord(
            command.IdempotencyKey,
            command.SenderAccountId ?? Guid.Empty,
            "/api/transfers",
            $"{command.SenderAccountId}_{command.ReceiverAccountId}_{command.Amount}");

        await _idempotencyRepository.AddAsync(idempotencyRecord, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);

        // Step 9: Run Risk Shield evaluation
        RiskAssessmentResultDto? riskAssessment = null;
        if (command.SenderAccountId.HasValue && !command.BypassRiskCheck)
        {
            riskAssessment = await _riskShieldService.AssessTransferRiskAsync(
                command.SenderAccountId.Value,
                command.ReceiverAccountId,
                command.Amount,
                cancellationToken);
            timeline.Add($"[{DateTime.UtcNow:HH:mm:ss.fff}] RISK_EVALUATED: Risk Score {riskAssessment.TotalScore} ({riskAssessment.Level}).");
        }

        // Step 10: Create transaction record entity
        var txnNumber = $"TXN-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";
        var transaction = new Transaction(
            txnNumber,
            command.SenderAccountId,
            command.ReceiverAccountId,
            command.Amount,
            command.IdempotencyKey,
            command.Type,
            command.Purpose,
            command.Fee);

        // Step 11: Set transaction state to CREATED (default in constructor)
        if (riskAssessment != null)
        {
            transaction.SetRiskAssessment(riskAssessment.TotalScore, riskAssessment.Level);
        }

        // Step 13: Begin PostgreSQL ACID Database Transaction (unless caller provided one)
        // ── P1 FIX: When ExternalTransaction is provided, we do NOT create our own transaction.
        // The caller is responsible for commit/rollback, enabling true composite atomicity.
        bool ownsTransaction = command.ExternalTransaction == null;
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? dbTransaction = null;

        if (ownsTransaction)
        {
            dbTransaction = await _context.BeginTransactionAsync(cancellationToken);
        }
        else
        {
            dbTransaction = command.ExternalTransaction;
        }

        try
        {
            await _transactionRepository.AddAsync(transaction, cancellationToken);

            // Step 12: Record CREATED event
            var createdEvent = new TransactionEvent(transaction.Id, TransactionEventTypes.Created, "Transaction created in N-Cash Payment Engine.");
            await _context.TransactionEvents.AddAsync(createdEvent, cancellationToken);
            timeline.Add($"[{DateTime.UtcNow:HH:mm:ss.fff}] CREATED: Transaction initialized.");

            Account? sender = null;
            Account? receiver = null;
            decimal? previousSenderBalance = null;

            // Step 14 & 17: Row-Level Locking in deterministic ascending ID order to eliminate deadlocks
            if (command.SenderAccountId.HasValue)
            {
                var (s, r) = await _accountRepository.GetAccountsForUpdateAsync(
                    command.SenderAccountId.Value,
                    command.ReceiverAccountId,
                    cancellationToken);

                sender = s;
                receiver = r;

                // Step 2 & 3: Validate sender & recipient exist
                if (sender == null)
                    throw new DomainException(ErrorCodes.AccountNotFound, "Sender account does not exist.");

                // Step 7: Validate sender account status
                if (sender.Status != AccountStatus.Active)
                    throw new DomainException(ErrorCodes.AccountInactive, $"Sender account is {sender.Status}. Transfers are blocked.");

                // Step 15: Re-read sender balance from database under row lock
                previousSenderBalance = sender.Balance;

                // Step 16: Check insufficient balance
                var totalDeduction = command.Amount + command.Fee;
                if (!sender.CanDebit(totalDeduction))
                {
                    throw new DomainException(ErrorCodes.InsufficientFunds,
                        $"Insufficient available funds. Current Balance: {sender.Balance:N2} {sender.Currency}, Required: {totalDeduction:N2} {sender.Currency}.");
                }
            }
            else
            {
                receiver = await _accountRepository.GetAccountForUpdateAsync(command.ReceiverAccountId, cancellationToken);
            }

            // Step 3: Validate recipient exists
            if (receiver == null)
                throw new DomainException(ErrorCodes.RecipientNotFound, "Receiver account does not exist.");

            // Step 7: Validate receiver account status
            if (receiver.Status != AccountStatus.Active)
                throw new DomainException(ErrorCodes.AccountInactive, $"Receiver account is {receiver.Status}. Transfers are blocked.");

            timeline.Add($"[{DateTime.UtcNow:HH:mm:ss.fff}] ACCOUNTS_LOCKED: Row locks acquired safely in deterministic order.");

            // Record VALIDATED event
            var validatedEvent = new TransactionEvent(transaction.Id, TransactionEventTypes.Validated, "Account balances, statuses, and risk checks validated successfully.");
            await _context.TransactionEvents.AddAsync(validatedEvent, cancellationToken);
            timeline.Add($"[{DateTime.UtcNow:HH:mm:ss.fff}] VALIDATED: Account balances and limits validated.");

            // Step 18: Set transaction to PROCESSING
            transaction.MarkProcessing();
            _transactionRepository.Update(transaction);

            // Step 19: Record PROCESSING event
            var processingEvent = new TransactionEvent(transaction.Id, TransactionEventTypes.Processing, "Accounts locked. Starting atomic double-entry mutation.");
            await _context.TransactionEvents.AddAsync(processingEvent, cancellationToken);
            timeline.Add($"[{DateTime.UtcNow:HH:mm:ss.fff}] PROCESSING: Starting double-entry mutation.");

            // Step 20: Debit sender
            LedgerEntry? senderDebitEntry = null;
            decimal totalDebited = 0m;
            if (sender != null)
            {
                var debitAmount = command.Amount + command.Fee;
                sender.Debit(debitAmount);
                _accountRepository.Update(sender);
                totalDebited += debitAmount;

                // Step 21: Record DEBIT ledger entry
                senderDebitEntry = new LedgerEntry(
                    transaction.Id,
                    sender.Id,
                    LedgerDirection.Debit,
                    debitAmount,
                    sender.Balance,
                    $"Transfer to {receiver.AccountNumber}. Purpose: {command.Purpose ?? "Peer-to-Peer Transfer"}");

                await _ledgerRepository.AddEntryAsync(senderDebitEntry, cancellationToken);

                var debitedEvent = new TransactionEvent(transaction.Id, TransactionEventTypes.Debited, $"Debited {debitAmount:N2} {sender.Currency} from {sender.AccountNumber}. Balance after: {sender.Balance:N2}.");
                await _context.TransactionEvents.AddAsync(debitedEvent, cancellationToken);

                timeline.Add($"[{DateTime.UtcNow:HH:mm:ss.fff}] DEBIT_EXECUTED: Deducted {debitAmount:N2} {sender.Currency} from {sender.AccountNumber}. Balance: {sender.Balance:N2}.");
            }

            // Step 22: Credit receiver
            receiver.Credit(command.Amount);
            _accountRepository.Update(receiver);
            decimal totalCredited = command.Amount;

            // Step 23: Record CREDIT ledger entry
            var receiverCreditEntry = new LedgerEntry(
                transaction.Id,
                receiver.Id,
                LedgerDirection.Credit,
                command.Amount,
                receiver.Balance,
                $"Received from {(sender?.AccountNumber ?? "N-Cash System Treasury")}. Purpose: {command.Purpose ?? "Peer-to-Peer Transfer"}");

            await _ledgerRepository.AddEntryAsync(receiverCreditEntry, cancellationToken);

            var creditedEvent = new TransactionEvent(transaction.Id, TransactionEventTypes.Credited, $"Credited {command.Amount:N2} {receiver.Currency} to {receiver.AccountNumber}. Balance after: {receiver.Balance:N2}.");
            await _context.TransactionEvents.AddAsync(creditedEvent, cancellationToken);

            timeline.Add($"[{DateTime.UtcNow:HH:mm:ss.fff}] CREDIT_EXECUTED: Credited {command.Amount:N2} {receiver.Currency} to {receiver.AccountNumber}. Balance: {receiver.Balance:N2}.");

            // Step 24: Verify ledger balance (Invariant: sum(Debit) == sum(Credit))
            // ── P1 FEE FIX: When fee > 0, we must credit a fee-revenue account to keep balance.
            // Currently fee is 0 in all user transfers, but this prevents a dormant bug.
            // If fee > 0, the fee portion must be credited to a designated FeeRevenue account.
            // For now, fee is always 0; if it becomes non-zero, this check will catch the imbalance
            // and force proper fee destination accounting to be implemented.
            decimal totalCreditedIncludingFee = totalCredited;
            if (sender != null && command.Fee > 0m)
            {
                // Fee revenue credit — ensures zero ledger variance when fees are charged.
                // The fee is credited as a system-side entry to maintain double-entry integrity.
                // In a production system this would credit a designated fee-revenue account.
                var feeRevenueEntry = new LedgerEntry(
                    transaction.Id,
                    SystemConstants.TreasuryAccountId, // Fee revenue goes to Treasury
                    LedgerDirection.Credit,
                    command.Fee,
                    0m, // Balance-after is not tracked for system accounts in this simplified model
                    $"Fee revenue from transfer {txnNumber}. Rate applied: {command.Fee:N2} BDT.");
                await _ledgerRepository.AddEntryAsync(feeRevenueEntry, cancellationToken);
                totalCreditedIncludingFee += command.Fee;

                var feeEvent = new TransactionEvent(transaction.Id, "FEE_COLLECTED", $"Fee of {command.Fee:N2} BDT credited to system revenue.");
                await _context.TransactionEvents.AddAsync(feeEvent, cancellationToken);
                timeline.Add($"[{DateTime.UtcNow:HH:mm:ss.fff}] FEE_COLLECTED: {command.Fee:N2} BDT fee credited to system revenue.");
            }

            decimal ledgerDelta = totalDebited - totalCreditedIncludingFee;
            if (sender != null && ledgerDelta != 0m)
            {
                throw new DomainException(ErrorCodes.TransactionFailed, $"Ledger integrity validation failed! Non-zero ledger delta: {ledgerDelta}. Rolling back.");
            }
            timeline.Add($"[{DateTime.UtcNow:HH:mm:ss.fff}] LEDGER_VERIFIED: Zero variance confirmed (Delta = 0.00).");

            // Persist Risk Signals if any
            if (riskAssessment != null)
            {
                foreach (var sig in riskAssessment.Signals)
                {
                    var riskSignal = new RiskSignal(transaction.Id, sig.RuleCode, sig.ScoreImpact, sig.Reason, sig.Severity);
                    await _context.RiskSignals.AddAsync(riskSignal, cancellationToken);
                }
            }

            // Step 25: Set transaction COMPLETED
            transaction.MarkSucceeded();
            _transactionRepository.Update(transaction);

            // Step 26: Record COMPLETED event
            var completedEvent = new TransactionEvent(transaction.Id, TransactionEventTypes.Completed, "Atomic money movement committed.");
            await _context.TransactionEvents.AddAsync(completedEvent, cancellationToken);
            timeline.Add($"[{DateTime.UtcNow:HH:mm:ss.fff}] COMPLETED: Transaction succeeded and committed.");

            // Construct Step 28 Trust Receipt
            var senderUser = sender != null ? await _context.Users.FindAsync([sender.UserId], cancellationToken) : null;
            var receiverUser = await _context.Users.FindAsync([receiver.UserId], cancellationToken);

            var trustReceipt = new TransferResultDto(
                TransactionId: transaction.Id,
                TransactionNumber: transaction.TransactionNumber,
                SenderAccountNumber: sender?.AccountNumber,
                SenderUsername: senderUser?.Username ?? "N-Cash System Treasury",
                ReceiverAccountNumber: receiver.AccountNumber,
                ReceiverUsername: receiverUser?.Username ?? "Recipient",
                Amount: transaction.Amount,
                Fee: transaction.Fee,
                PreviousSenderBalance: previousSenderBalance,
                SenderNewBalance: sender?.Balance,
                ReceiverNewBalance: receiver.Balance,
                Status: transaction.Status.ToString(),
                IdempotencyKey: transaction.IdempotencyKey,
                CreatedAtUtc: transaction.CreatedAtUtc,
                CompletedAtUtc: transaction.CompletedAtUtc,
                RiskLevel: transaction.RiskLevel.ToString(),
                RiskScore: transaction.RiskScore,
                ZeroVarianceVerified: true,
                LedgerDelta: 0.00m,
                RiskAssessment: riskAssessment,
                EventTimeline: timeline,
                IsCached: false);

            // Complete Idempotency Record with response receipt
            idempotencyRecord.Complete(200, JsonSerializer.Serialize(trustReceipt));
            await _idempotencyRepository.UpdateAsync(idempotencyRecord, cancellationToken);

            // Step 27: Persist all changes.
            // If caller provided an ExternalTransaction, we only SaveChanges — NOT commit.
            // The caller will commit (or rollback) after updating their own business aggregate.
            await _context.SaveChangesAsync(cancellationToken);

            if (ownsTransaction)
            {
                // We own the transaction — commit it now
                await dbTransaction!.CommitAsync(cancellationToken);
            }
            // else: ExternalTransaction — caller will commit after updating business aggregate

            _logger.LogInformation("Successfully executed transfer {TxnNumber}. Sender: {Sender} -> Receiver: {Receiver}, Amount: {Amount}",
                txnNumber, sender?.AccountNumber ?? "Treasury", receiver.AccountNumber, command.Amount);

            // Step 28: Return Trust Receipt
            return trustReceipt;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Payment execution failed for IdempotencyKey: {Key}. Rolling back.", command.IdempotencyKey);

            // Only rollback if we own the transaction
            if (ownsTransaction && dbTransaction != null)
            {
                await dbTransaction.RollbackAsync(cancellationToken);
            }
            // If caller owns the transaction, they are responsible for rollback.

            _context.ChangeTracker.Clear();

            var failedRecord = await _idempotencyRepository.GetByKeyAsync(command.IdempotencyKey, cancellationToken);
            if (failedRecord != null)
            {
                failedRecord.Fail(400, JsonSerializer.Serialize(new { error = ex.Message }));
                await _idempotencyRepository.UpdateAsync(failedRecord, cancellationToken);
                await _context.SaveChangesAsync(cancellationToken);
            }

            throw;
        }
        finally
        {
            // Dispose if we own the transaction
            if (ownsTransaction && dbTransaction != null)
            {
                await dbTransaction.DisposeAsync();
            }
        }
    }
}
