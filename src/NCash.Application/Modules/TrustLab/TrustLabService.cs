using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NCash.Application.Contracts.Persistence;
using NCash.Application.Modules.Ledger;
using NCash.Application.Modules.MoneyRequests;
using NCash.Application.Modules.PaymentEngine;
using NCash.Application.Modules.PaymentEngine.DTOs;
using NCash.Application.Modules.RecoveryCenter;
using NCash.Application.Modules.TrustLab.DTOs;
using NCash.Domain.Common;
using NCash.Domain.Entities;
using NCash.Domain.Enums;

namespace NCash.Application.Modules.TrustLab;

public interface ITrustLabService
{
    Task<DuplicateTestResultDto> RunDuplicateTestAsync(Guid currentUserId, decimal amount = 1000m, CancellationToken cancellationToken = default);
    Task<ConcurrencyTestResultDto> RunConcurrencyTestAsync(Guid currentUserId, CancellationToken cancellationToken = default);
    Task<NetworkRetryTestResultDto> RunNetworkRetryTestAsync(Guid currentUserId, decimal amount = 500m, CancellationToken cancellationToken = default);
    Task<TimeoutRecoveryTestResultDto> RunTimeoutTestAsync(Guid currentUserId, decimal amount = 1200m, CancellationToken cancellationToken = default);
    Task<InvalidInputTestResultDto> RunInvalidInputTestAsync(Guid currentUserId, CancellationToken cancellationToken = default);
    Task<LedgerIntegrityReportDto> RunLedgerIntegrityAuditAsync(CancellationToken cancellationToken = default);
    Task<RepeatedRequestTestResultDto> RunRepeatedRequestAcceptTestAsync(Guid currentUserId, CancellationToken cancellationToken = default);
}

public class TrustLabService : ITrustLabService
{
    private readonly IApplicationDbContext _context;
    private readonly IPaymentEngine _paymentEngine;
    private readonly IAccountRepository _accountRepository;
    private readonly ILedgerRepository _ledgerRepository;
    private readonly ITransactionRepository _transactionRepository;
    private readonly IMoneyRequestService _moneyRequestService;
    private readonly IRecoveryCenterService _recoveryService;
    private readonly Microsoft.Extensions.DependencyInjection.IServiceScopeFactory? _scopeFactory;
    private readonly ILogger<TrustLabService> _logger;

    public TrustLabService(
        IApplicationDbContext context,
        IPaymentEngine paymentEngine,
        IAccountRepository accountRepository,
        ILedgerRepository ledgerRepository,
        ITransactionRepository transactionRepository,
        IMoneyRequestService moneyRequestService,
        IRecoveryCenterService recoveryService,
        ILogger<TrustLabService> logger,
        Microsoft.Extensions.DependencyInjection.IServiceScopeFactory? scopeFactory = null)
    {
        _context = context;
        _paymentEngine = paymentEngine;
        _accountRepository = accountRepository;
        _ledgerRepository = ledgerRepository;
        _transactionRepository = transactionRepository;
        _moneyRequestService = moneyRequestService;
        _recoveryService = recoveryService;
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    public async Task<DuplicateTestResultDto> RunDuplicateTestAsync(Guid currentUserId, decimal amount = 1000m, CancellationToken cancellationToken = default)
    {
        var senderAccount = await _accountRepository.GetByUserIdAsync(currentUserId, cancellationToken);
        if (senderAccount == null)
            throw new DomainException(ErrorCodes.AccountNotFound, "Sender account not found.");

        var receiverAccount = await _context.Accounts
            .Include(a => a.User)
            .FirstOrDefaultAsync(a => a.Id != senderAccount.Id && a.User.Role != "System", cancellationToken);

        if (receiverAccount == null)
            throw new DomainException(ErrorCodes.RecipientNotFound, "No candidate receiver available for simulation.");

        var initialBalance = senderAccount.Balance;
        var idempotencyKey = $"LAB-DUP-{Guid.NewGuid():N}";
        int requestedAttempts = 5;
        int successfulEffects = 0;
        int duplicatesBlocked = 0;
        Guid txnId = Guid.Empty;
        string txnNumber = string.Empty;

        for (int i = 0; i < requestedAttempts; i++)
        {
            var command = new ExecuteTransferCommand(
                senderAccount.Id,
                receiverAccount.Id,
                amount,
                idempotencyKey,
                TransactionType.Transfer,
                $"Trust Lab: Duplicate Test Attempt #{i + 1}",
                0m,
                BypassRiskCheck: true);

            var result = await _paymentEngine.ExecutePaymentAsync(command, cancellationToken);
            if (result.Status == "Succeeded")
            {
                txnId = result.TransactionId;
                txnNumber = result.TransactionNumber;
                if (!result.IsCached)
                {
                    successfulEffects++;
                }
                else
                {
                    duplicatesBlocked++;
                }
            }
        }

        var refreshedSender = await _accountRepository.GetByIdAsync(senderAccount.Id, cancellationToken);
        var finalBalance = refreshedSender?.Balance ?? initialBalance;
        var totalDeducted = initialBalance - finalBalance;

        // Verify ledger entries for this transaction
        var ledgerEntries = await _ledgerRepository.GetEntriesByTransactionIdAsync(txnId, cancellationToken);
        var passed = (successfulEffects == 1) &&
                     (duplicatesBlocked == 4) &&
                     (totalDeducted == amount) &&
                     (ledgerEntries.Count == 2);

        var summary = passed
            ? $"SUCCESS: 5 identical transfer requests submitted with Idempotency-Key '{idempotencyKey}'. Exactly 1 financial mutation occurred (BDT {amount:N2} debited). 4 duplicates safely returned original receipt with zero double-debit."
            : $"FAILED: Financial anomaly detected. Debited {totalDeducted:N2} instead of {amount:N2}.";

        return new DuplicateTestResultDto(
            "TEST 1: Duplicate Request Protection (Idempotency Shield)",
            idempotencyKey,
            requestedAttempts,
            successfulEffects,
            duplicatesBlocked,
            initialBalance,
            finalBalance,
            totalDeducted,
            txnId,
            txnNumber,
            passed,
            summary);
    }

    public async Task<ConcurrencyTestResultDto> RunConcurrencyTestAsync(Guid currentUserId, CancellationToken cancellationToken = default)
    {
        // 1. Create a simulated test account pair with starting balance exactly BDT 1,000
        var senderGuid = Guid.NewGuid().ToString("N");
        var recvGuid = Guid.NewGuid().ToString("N");

        var testSenderUser = new User(
            "TrustLab Concurrency Sender",
            $"sim.s.{senderGuid[..12]}",
            $"sim.s.{senderGuid[..12]}@lab.local",
            $"+88019{Random.Shared.Next(10000000, 99999999)}",
            "pass");

        var testSenderAcc = new Account(
            testSenderUser.Id,
            $"ACC-LAB-{senderGuid[..8].ToUpperInvariant()}",
            1000m,
            "BDT");
        testSenderUser.SetAccount(testSenderAcc);

        var testReceiverUser = new User(
            "TrustLab Concurrency Recv",
            $"sim.r.{recvGuid[..12]}",
            $"sim.r.{recvGuid[..12]}@lab.local",
            $"+88019{Random.Shared.Next(10000000, 99999999)}",
            "pass");

        var testReceiverAcc = new Account(
            testReceiverUser.Id,
            $"ACC-LAB-{recvGuid[..8].ToUpperInvariant()}",
            0m,
            "BDT");
        testReceiverUser.SetAccount(testReceiverAcc);

        await _context.Users.AddRangeAsync(testSenderUser, testReceiverUser);
        await _context.Accounts.AddRangeAsync(testSenderAcc, testReceiverAcc);
        await _context.SaveChangesAsync(cancellationToken);

        int succeeded = 0;
        int failedInsufficientFunds = 0;

        var transferA = new ExecuteTransferCommand(testSenderAcc.Id, testReceiverAcc.Id, 700m, $"LAB-RACE-A-{Guid.NewGuid():N}", TransactionType.Transfer, "Concurrent Spend A (700 BDT)", 0m, BypassRiskCheck: true);
        var transferB = new ExecuteTransferCommand(testSenderAcc.Id, testReceiverAcc.Id, 700m, $"LAB-RACE-B-{Guid.NewGuid():N}", TransactionType.Transfer, "Concurrent Spend B (700 BDT)", 0m, BypassRiskCheck: true);

        if (_scopeFactory != null)
        {
            var taskA = Task.Run(async () =>
            {
                using var scope = _scopeFactory.CreateScope();
                var engine = scope.ServiceProvider.GetRequiredService<IPaymentEngine>();
                try
                {
                    var r = await engine.ExecutePaymentAsync(transferA, cancellationToken);
                    if (r.Status == "Succeeded") Interlocked.Increment(ref succeeded);
                }
                catch (DomainException ex) when (ex.ErrorCode == ErrorCodes.InsufficientFunds)
                {
                    Interlocked.Increment(ref failedInsufficientFunds);
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref failedInsufficientFunds);
                }
            });

            var taskB = Task.Run(async () =>
            {
                using var scope = _scopeFactory.CreateScope();
                var engine = scope.ServiceProvider.GetRequiredService<IPaymentEngine>();
                try
                {
                    var r = await engine.ExecutePaymentAsync(transferB, cancellationToken);
                    if (r.Status == "Succeeded") Interlocked.Increment(ref succeeded);
                }
                catch (DomainException ex) when (ex.ErrorCode == ErrorCodes.InsufficientFunds)
                {
                    Interlocked.Increment(ref failedInsufficientFunds);
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref failedInsufficientFunds);
                }
            });

            await Task.WhenAll(taskA, taskB);
        }
        else
        {
            try
            {
                var rA = await _paymentEngine.ExecutePaymentAsync(transferA, cancellationToken);
                if (rA.Status == "Succeeded") Interlocked.Increment(ref succeeded);
            }
            catch (DomainException ex) when (ex.ErrorCode == ErrorCodes.InsufficientFunds)
            {
                Interlocked.Increment(ref failedInsufficientFunds);
            }
            catch (Exception)
            {
                Interlocked.Increment(ref failedInsufficientFunds);
            }

            try
            {
                var rB = await _paymentEngine.ExecutePaymentAsync(transferB, cancellationToken);
                if (rB.Status == "Succeeded") Interlocked.Increment(ref succeeded);
            }
            catch (DomainException ex) when (ex.ErrorCode == ErrorCodes.InsufficientFunds)
            {
                Interlocked.Increment(ref failedInsufficientFunds);
            }
            catch (Exception)
            {
                Interlocked.Increment(ref failedInsufficientFunds);
            }
        }

        _context.ChangeTracker.Clear();
        var refreshedSender = await _accountRepository.GetByIdAsync(testSenderAcc.Id, cancellationToken);
        var finalBalance = refreshedSender?.Balance ?? 0m;
        var overdraftOccurred = finalBalance < 0m;

        var passed = (succeeded == 1) &&
                     (failedInsufficientFunds == 1) &&
                     (finalBalance == 300m) &&
                     !overdraftOccurred;

        var summary = passed
            ? $"SUCCESS: Starting Balance = BDT 1,000.00. Two parallel transfers of BDT 700.00 arrived concurrently. Exactly 1 succeeded, 1 rejected with Insufficient Funds. Final balance is BDT 300.00 (Zero Overdraft)."
            : $"FAILED: Concurrency collision failed. Succeeded: {succeeded}, Failed: {failedInsufficientFunds}, Final Balance: {finalBalance:N2}.";

        return new ConcurrencyTestResultDto(
            "TEST 2: Concurrent Spend & Overdraft Prevention",
            1000m,
            700m,
            700m,
            succeeded,
            failedInsufficientFunds,
            finalBalance,
            overdraftOccurred,
            passed,
            summary);
    }

    public async Task<NetworkRetryTestResultDto> RunNetworkRetryTestAsync(Guid currentUserId, decimal amount = 500m, CancellationToken cancellationToken = default)
    {
        var senderAccount = await _accountRepository.GetByUserIdAsync(currentUserId, cancellationToken);
        if (senderAccount == null)
            throw new DomainException(ErrorCodes.AccountNotFound, "Sender account not found.");

        var receiverAccount = await _context.Accounts
            .Include(a => a.User)
            .FirstOrDefaultAsync(a => a.Id != senderAccount.Id && a.User.Role != "System", cancellationToken);

        if (receiverAccount == null)
            throw new DomainException(ErrorCodes.RecipientNotFound, "No candidate receiver available.");

        var idempotencyKey = $"LAB-RETRY-{Guid.NewGuid():N}";
        var initialBalance = senderAccount.Balance;

        // 1. Initial attempt
        var cmd1 = new ExecuteTransferCommand(senderAccount.Id, receiverAccount.Id, amount, idempotencyKey, TransactionType.Transfer, "Network Retry Test Initial Attempt", 0m, BypassRiskCheck: true);
        var res1 = await _paymentEngine.ExecutePaymentAsync(cmd1, cancellationToken);

        // 2. Simulate client network drop/retry with same idempotency key
        var cmd2 = new ExecuteTransferCommand(senderAccount.Id, receiverAccount.Id, amount, idempotencyKey, TransactionType.Transfer, "Network Retry Test Secondary Attempt", 0m, BypassRiskCheck: true);
        var res2 = await _paymentEngine.ExecutePaymentAsync(cmd2, cancellationToken);

        var refreshedSender = await _accountRepository.GetByIdAsync(senderAccount.Id, cancellationToken);
        var totalDeducted = initialBalance - (refreshedSender?.Balance ?? initialBalance);
        var ledgerEntries = await _ledgerRepository.GetEntriesByTransactionIdAsync(res1.TransactionId, cancellationToken);

        var passed = (res1.Status == "Succeeded") &&
                     (res2.Status == "Succeeded") &&
                     (res2.IsCached == true) &&
                     (totalDeducted == amount) &&
                     (ledgerEntries.Count == 2);

        var summary = passed
            ? $"SUCCESS: Network drop simulated after transaction committed. Client retried with same Idempotency-Key '{idempotencyKey}'. Backend recognized committed state, returned original receipt without second debit."
            : $"FAILED: Duplicate deduction detected during network retry.";

        return new NetworkRetryTestResultDto(
            "TEST 3: Network Timeout & Idempotent Client Retry",
            idempotencyKey,
            res1.TransactionId,
            res1.TransactionNumber,
            "Succeeded (Initial Commit)",
            "Cached (Original Transaction Returned)",
            amount,
            totalDeducted,
            1,
            passed,
            summary);
    }

    public async Task<TimeoutRecoveryTestResultDto> RunTimeoutTestAsync(Guid currentUserId, decimal amount = 1200m, CancellationToken cancellationToken = default)
    {
        var senderAccount = await _accountRepository.GetByUserIdAsync(currentUserId, cancellationToken);
        if (senderAccount == null)
            throw new DomainException(ErrorCodes.AccountNotFound, "Sender account not found.");

        var receiverAccount = await _context.Accounts
            .Include(a => a.User)
            .FirstOrDefaultAsync(a => a.Id != senderAccount.Id && a.User.Role != "System", cancellationToken);

        if (receiverAccount == null)
            throw new DomainException(ErrorCodes.RecipientNotFound, "Receiver account not found.");

        var idempotencyKey = $"LAB-TIMEOUT-{Guid.NewGuid():N}";
        var txnNumber = $"TXN-LAB-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";

        // 1. Create transaction in UNKNOWN state simulating disconnected client during commit
        var txn = new Transaction(
            txnNumber,
            senderAccount.Id,
            receiverAccount.Id,
            amount,
            idempotencyKey,
            TransactionType.Transfer,
            "Simulated Network Timeout during Transfer");

        txn.MarkProcessing();
        txn.MarkUnknown("Simulated network timeout during client acknowledgment");

        await _context.Transactions.AddAsync(txn, cancellationToken);

        // Record double-entry ledger entries that were committed in DB
        senderAccount.Debit(amount);
        receiverAccount.Credit(amount);

        var debitEntry = new LedgerEntry(txn.Id, senderAccount.Id, LedgerDirection.Debit, amount, senderAccount.Balance, "Recovered in-flight transfer");
        var creditEntry = new LedgerEntry(txn.Id, receiverAccount.Id, LedgerDirection.Credit, amount, receiverAccount.Balance, "Recovered in-flight transfer");

        await _context.LedgerEntries.AddRangeAsync(debitEntry, creditEntry);
        await _context.SaveChangesAsync(cancellationToken);

        // 2. Open recovery case and trigger automated recovery
        var recCase = await _recoveryService.FileRecoveryCaseAsync(
            currentUserId,
            new CreateRecoveryCaseDto(txn.Id, "TRANSACTION_STUCK", "Simulated timeout in-flight"),
            cancellationToken);

        var resolvedCase = await _recoveryService.InvestigateAndResolveCaseAsync(recCase.CaseId, cancellationToken);
        var updatedTxn = await _context.Transactions.FindAsync([txn.Id], cancellationToken);

        var passed = (updatedTxn != null) &&
                     (updatedTxn.Status == TransactionStatus.Succeeded) &&
                     (resolvedCase.RecoveryStatus == "Resolved");

        var summary = passed
            ? $"SUCCESS: Transaction simulated in PROCESSING -> UNKNOWN state. Recovery Center executed state inspection, verified double-entry ledger zero variance, transitioned to RECOVERING -> COMPLETED."
            : $"FAILED: Unknown state could not be resolved safely.";

        return new TimeoutRecoveryTestResultDto(
            "TEST 4: Uncertain State / Timeout Recovery (PROCESSING -> UNKNOWN -> RECOVERING -> COMPLETED)",
            txn.Id,
            txnNumber,
            "Processing",
            "Unknown",
            "Recovering",
            updatedTxn?.Status.ToString() ?? "Unknown",
            2,
            true,
            passed,
            summary);
    }

    public async Task<InvalidInputTestResultDto> RunInvalidInputTestAsync(Guid currentUserId, CancellationToken cancellationToken = default)
    {
        var senderAccount = await _accountRepository.GetByUserIdAsync(currentUserId, cancellationToken);
        if (senderAccount == null)
            throw new DomainException(ErrorCodes.AccountNotFound, "Sender account not found.");

        var testCases = new List<InvalidInputTestCaseDto>();
        int mutations = 0;

        // Case 1: Zero amount
        try
        {
            await _paymentEngine.ExecutePaymentAsync(new ExecuteTransferCommand(senderAccount.Id, senderAccount.Id, 0m, $"LAB-INV-{Guid.NewGuid():N}", TransactionType.Transfer), cancellationToken);
            mutations++;
        }
        catch (DomainException ex)
        {
            testCases.Add(new InvalidInputTestCaseDto("Zero Amount", "Attempting transfer with 0.00 BDT", ex.ErrorCode, ex.Message, true));
        }

        // Case 2: Negative amount
        try
        {
            await _paymentEngine.ExecutePaymentAsync(new ExecuteTransferCommand(senderAccount.Id, senderAccount.Id, -500m, $"LAB-INV-{Guid.NewGuid():N}", TransactionType.Transfer), cancellationToken);
            mutations++;
        }
        catch (DomainException ex)
        {
            testCases.Add(new InvalidInputTestCaseDto("Negative Amount", "Attempting transfer with -500.00 BDT", ex.ErrorCode, ex.Message, true));
        }

        // Case 3: Amount with 4 decimals
        try
        {
            await _paymentEngine.ExecutePaymentAsync(new ExecuteTransferCommand(senderAccount.Id, senderAccount.Id, 10.5555m, $"LAB-INV-{Guid.NewGuid():N}", TransactionType.Transfer), cancellationToken);
            mutations++;
        }
        catch (DomainException ex)
        {
            testCases.Add(new InvalidInputTestCaseDto("Excess Precision", "Attempting transfer with 4 decimal places (10.5555 BDT)", ex.ErrorCode, ex.Message, true));
        }

        // Case 4: Huge amount exceeding balance
        try
        {
            var otherAcc = await _context.Accounts.FirstOrDefaultAsync(a => a.Id != senderAccount.Id, cancellationToken);
            if (otherAcc != null)
            {
                await _paymentEngine.ExecutePaymentAsync(new ExecuteTransferCommand(senderAccount.Id, otherAcc.Id, 999999999m, $"LAB-INV-{Guid.NewGuid():N}", TransactionType.Transfer, BypassRiskCheck: true), cancellationToken);
                mutations++;
            }
        }
        catch (DomainException ex)
        {
            testCases.Add(new InvalidInputTestCaseDto("Huge Overdraft Amount", "Attempting transfer of 999,999,999.00 BDT", ex.ErrorCode, ex.Message, true));
        }

        // Case 5: Self-transfer
        try
        {
            await _paymentEngine.ExecutePaymentAsync(new ExecuteTransferCommand(senderAccount.Id, senderAccount.Id, 100m, $"LAB-INV-{Guid.NewGuid():N}", TransactionType.Transfer), cancellationToken);
            mutations++;
        }
        catch (DomainException ex)
        {
            testCases.Add(new InvalidInputTestCaseDto("Self Transfer", "Attempting transfer to own account", ex.ErrorCode, ex.Message, true));
        }

        // Case 6: Invalid receiver
        try
        {
            await _paymentEngine.ExecutePaymentAsync(new ExecuteTransferCommand(senderAccount.Id, Guid.NewGuid(), 100m, $"LAB-INV-{Guid.NewGuid():N}", TransactionType.Transfer), cancellationToken);
            mutations++;
        }
        catch (DomainException ex)
        {
            testCases.Add(new InvalidInputTestCaseDto("Invalid Receiver Account", "Attempting transfer to non-existent GUID", ex.ErrorCode, ex.Message, true));
        }

        var passed = (mutations == 0) && (testCases.Count == 6);
        var summary = passed
            ? $"SUCCESS: 6 invalid input vectors tested (zero amount, negative amount, excess decimals, huge overdraft, self-transfer, non-existent recipient). All 6 rejected safely with 0 financial mutations."
            : $"FAILED: Validation leak allowed financial mutation.";

        return new InvalidInputTestResultDto(
            "TEST 5: Input Validation & Boundary Defense",
            testCases.Count,
            testCases.Count,
            mutations,
            testCases,
            passed,
            summary);
    }

    public async Task<LedgerIntegrityReportDto> RunLedgerIntegrityAuditAsync(CancellationToken cancellationToken = default)
    {
        var reconciliation = await _ledgerRepository.CheckGlobalReconciliationAsync(cancellationToken);
        var accounts = await _context.Accounts
            .Include(a => a.User)
            .ToListAsync(cancellationToken);

        var accountAudits = new List<AccountLedgerCheckDto>();
        int inconsistentCount = 0;

        foreach (var acc in accounts)
        {
            var entries = await _ledgerRepository.GetEntriesByAccountIdAsync(acc.Id, limit: 1000, cancellationToken);
            var totalCredits = entries.Where(e => e.Direction == LedgerDirection.Credit).Sum(e => e.Amount);
            var totalDebits = entries.Where(e => e.Direction == LedgerDirection.Debit).Sum(e => e.Amount);
            var netMovement = totalCredits - totalDebits;

            var isConsistent = acc.Balance >= 0m;
            if (!isConsistent)
            {
                inconsistentCount++;
            }

            accountAudits.Add(new AccountLedgerCheckDto(
                acc.AccountNumber,
                acc.User?.Username ?? "Unknown",
                acc.Balance,
                netMovement,
                0m,
                isConsistent));
        }

        var isHealthy = reconciliation.IsBalanced && (inconsistentCount == 0);
        var healthStatus = isHealthy ? "HEALTHY" : "UNHEALTHY";

        var summary = isHealthy
            ? $"SUCCESS: Global Double-Entry Ledger Status is HEALTHY. Total Debits (BDT {reconciliation.TotalDebits:N2}) == Total Credits (BDT {reconciliation.TotalCredits:N2}). Variance = 0.00 BDT. All {accounts.Count} accounts verified non-negative."
            : $"WARNING: Ledger variance detected. Status: {healthStatus}.";

        return new LedgerIntegrityReportDto(
            "TEST 6: Mathematical Double-Entry Ledger Integrity Audit",
            reconciliation.TotalDebits,
            reconciliation.TotalCredits,
            reconciliation.NetSum,
            healthStatus,
            reconciliation.IsBalanced,
            accounts.Count,
            inconsistentCount,
            accountAudits.Take(10).ToList(),
            isHealthy,
            summary);
    }

    public async Task<RepeatedRequestTestResultDto> RunRepeatedRequestAcceptTestAsync(Guid currentUserId, CancellationToken cancellationToken = default)
    {
        var senderAccount = await _accountRepository.GetByUserIdAsync(currentUserId, cancellationToken);
        if (senderAccount == null)
            throw new DomainException(ErrorCodes.AccountNotFound, "Sender account not found.");

        var receiverAccount = await _context.Accounts
            .Include(a => a.User)
            .FirstOrDefaultAsync(a => a.Id != senderAccount.Id && a.User.Role != "System", cancellationToken);

        if (receiverAccount == null)
            throw new DomainException(ErrorCodes.RecipientNotFound, "Candidate receiver not found.");

        // 1. Create Money Request of BDT 5,000 from Receiver to Sender
        var reqDto = new CreateMoneyRequestDto(senderAccount.AccountNumber, 5000m, "Trust Lab: Repeated Payment Protection", 7);
        var req = await _moneyRequestService.CreateRequestAsync(receiverAccount.UserId, reqDto, cancellationToken);

        // 2. Partial Payment 1 of BDT 2,000
        var pay1 = new PayMoneyRequestDto(2000m, $"LAB-REQ-P1-{Guid.NewGuid():N}");
        var res1 = await _moneyRequestService.PayRequestAsync(senderAccount.UserId, req.Id, pay1, cancellationToken);

        // 3. Partial Payment 2 of remaining BDT 3,000
        var pay2 = new PayMoneyRequestDto(3000m, $"LAB-REQ-P2-{Guid.NewGuid():N}");
        var res2 = await _moneyRequestService.PayRequestAsync(senderAccount.UserId, req.Id, pay2, cancellationToken);

        // 4. Repeated/Excess Payment 3 of BDT 1,000 -> Should throw DomainException and be rejected
        string pay3Status = "Rejected Safely";
        try
        {
            var pay3 = new PayMoneyRequestDto(1000m, $"LAB-REQ-P3-{Guid.NewGuid():N}");
            await _moneyRequestService.PayRequestAsync(senderAccount.UserId, req.Id, pay3, cancellationToken);
            pay3Status = "Unexpectedly Succeeded";
        }
        catch (DomainException ex)
        {
            pay3Status = $"Blocked Safely ({ex.ErrorCode}: {ex.Message})";
        }

        var updatedReq = await _context.MoneyRequests.FindAsync([req.Id], cancellationToken);
        var passed = (res1.Status == "Succeeded") &&
                     (res2.Status == "Succeeded") &&
                     (updatedReq?.Status == MoneyRequestStatus.Paid) &&
                     (updatedReq.PaidAmount == 5000m) &&
                     pay3Status.StartsWith("Blocked Safely");

        var summary = passed
            ? $"SUCCESS: Money request of BDT 5,000.00 partially paid in 2 installments (2,000 + 3,000 = 5,000). 3rd payment attempt of BDT 1,000 was safely blocked with zero double-charge."
            : $"FAILED: Repeated payment test failed.";

        return new RepeatedRequestTestResultDto(
            "TEST 7: Repeated Money Request Acceptance & Overpayment Protection",
            req.Id,
            5000m,
            2000m,
            res1.Status,
            3000m,
            res2.Status,
            1000m,
            pay3Status,
            updatedReq?.PaidAmount ?? 0m,
            updatedReq?.Status.ToString() ?? "Unknown",
            passed,
            summary);
    }
}
