using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NCash.Application.Contracts.Persistence;
using NCash.Application.Contracts.Security;
using NCash.Application.Modules.Auth.DTOs;
using NCash.Domain.Common;
using NCash.Domain.Entities;
using NCash.Domain.Enums;

namespace NCash.Application.Modules.Auth;

public interface IAuthService
{
    Task<AuthResponseDto> RegisterAsync(RegisterRequestDto request, CancellationToken cancellationToken = default);
    Task<AuthResponseDto> LoginAsync(LoginRequestDto request, CancellationToken cancellationToken = default);
    Task<CurrentUserResponseDto> GetCurrentUserAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<PinOperationResultDto> SetTransactionPinAsync(Guid userId, SetPinRequestDto request, CancellationToken cancellationToken = default);
    Task<PinOperationResultDto> VerifyTransactionPinAsync(Guid userId, VerifyPinRequestDto request, CancellationToken cancellationToken = default);
}

public class AuthService : IAuthService
{
    private readonly IApplicationDbContext _context;
    private readonly IAccountRepository _accountRepository;
    private readonly ILedgerRepository _ledgerRepository;
    private readonly ITransactionRepository _transactionRepository;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IJwtTokenGenerator _jwtTokenGenerator;
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        IApplicationDbContext context,
        IAccountRepository accountRepository,
        ILedgerRepository ledgerRepository,
        ITransactionRepository transactionRepository,
        IPasswordHasher passwordHasher,
        IJwtTokenGenerator jwtTokenGenerator,
        ILogger<AuthService> logger)
    {
        _context = context;
        _accountRepository = accountRepository;
        _ledgerRepository = ledgerRepository;
        _transactionRepository = transactionRepository;
        _passwordHasher = passwordHasher;
        _jwtTokenGenerator = jwtTokenGenerator;
        _logger = logger;
    }

    public async Task<AuthResponseDto> RegisterAsync(RegisterRequestDto request, CancellationToken cancellationToken = default)
    {
        var normalizedUsername = request.Username.Trim().ToLowerInvariant();
        var normalizedEmail = request.Email.Trim().ToLowerInvariant();
        var phone = request.PhoneNumber.Trim();

        // 1. Anti-collision check
        if (await _context.Users.AnyAsync(u => u.Username == normalizedUsername, cancellationToken))
            throw new DomainException(ErrorCodes.UserAlreadyExists, $"Username '{request.Username}' is already taken.");

        if (await _context.Users.AnyAsync(u => u.Email == normalizedEmail, cancellationToken))
            throw new DomainException(ErrorCodes.UserAlreadyExists, $"Email '{request.Email}' is already registered.");

        if (await _context.Users.AnyAsync(u => u.PhoneNumber == phone, cancellationToken))
            throw new DomainException(ErrorCodes.UserAlreadyExists, $"Phone number '{phone}' is already registered.");

        // 2. Hash Password & optional PIN
        var passwordHash = _passwordHasher.HashPassword(request.Password);
        string? pinHash = !string.IsNullOrWhiteSpace(request.InitialTransactionPin)
            ? _passwordHasher.HashPassword(request.InitialTransactionPin.Trim())
            : null;

        var user = new User(
            request.FullName,
            normalizedUsername,
            normalizedEmail,
            phone,
            passwordHash,
            pinHash,
            role: "User",
            status: UserStatus.Active);

        // 3. Generate unique account number
        var randomNum = new Random().Next(100000, 999999);
        var accountNumber = $"ACC-{randomNum}";
        while (await _context.Accounts.AnyAsync(a => a.AccountNumber == accountNumber, cancellationToken))
        {
            randomNum = new Random().Next(100000, 999999);
            accountNumber = $"ACC-{randomNum}";
        }

        var account = new Account(user.Id, accountNumber, 0m, SystemConstants.CurrencyBdt);
        user.SetAccount(account);

        // 4. Transactional creation and controlled initial issuance from Treasury
        await using var dbTransaction = await _context.BeginTransactionAsync(cancellationToken);
        try
        {
            await _context.Users.AddAsync(user, cancellationToken);
            await _context.Accounts.AddAsync(account, cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);

            // Fetch Treasury Account for issuance
            var treasuryAccount = await _accountRepository.GetAccountForUpdateAsync(SystemConstants.TreasuryAccountId, cancellationToken);
            if (treasuryAccount == null)
                throw new DomainException(ErrorCodes.AccountNotFound, "System Treasury account is uninitialized.");

            var initialAmount = SystemConstants.InitialUserBalance; // BDT 100,000
            var idempotencyKey = $"SIGNUP-ISSUANCE-{user.Id}-{Guid.NewGuid():N}";
            var txnNumber = $"TXN-SYS-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";

            var txn = new Transaction(
                txnNumber,
                SystemConstants.TreasuryAccountId,
                account.Id,
                initialAmount,
                idempotencyKey,
                TransactionType.SystemIssuance,
                "Welcome Bonus: Controlled Initial System Issuance",
                0m);

            txn.MarkProcessing();

            // Mutate balances
            treasuryAccount.Debit(initialAmount);
            account.Credit(initialAmount);

            txn.MarkSucceeded();

            await _transactionRepository.AddAsync(txn, cancellationToken);

            // Create Paired Double-Entry Ledger records
            var debitEntry = new LedgerEntry(
                txn.Id,
                SystemConstants.TreasuryAccountId,
                LedgerDirection.Debit,
                initialAmount,
                treasuryAccount.Balance,
                $"System Issuance to {user.Username} ({account.AccountNumber})");

            var creditEntry = new LedgerEntry(
                txn.Id,
                account.Id,
                LedgerDirection.Credit,
                initialAmount,
                account.Balance,
                "Welcome Bonus: Simulated Starting Funds");

            await _ledgerRepository.AddEntryAsync(debitEntry, cancellationToken);
            await _ledgerRepository.AddEntryAsync(creditEntry, cancellationToken);

            // Record events
            var evtCreated = new TransactionEvent(txn.Id, TransactionEventTypes.Created, "Transaction created for controlled registration bonus.");
            var evtCompleted = new TransactionEvent(txn.Id, TransactionEventTypes.Completed, $"Credited {initialAmount:N2} BDT to new account {account.AccountNumber}.");
            await _context.TransactionEvents.AddRangeAsync(evtCreated, evtCompleted);

            // Record system audit log
            var auditLog = new SystemAuditLog(
                user.Id,
                "USER_REGISTERED",
                "User",
                user.Id.ToString(),
                oldValueJson: null,
                newValueJson: $"{{\"username\":\"{user.Username}\",\"account\":\"{account.AccountNumber}\",\"fundedAmount\":{initialAmount}}}",
                metadataJson: "{\"source\":\"SelfRegistration\",\"issuance\":\"ControlledSystemTreasury\"}");

            await _context.SystemAuditLogs.AddAsync(auditLog, cancellationToken);

            // Commit entire registration batch atomically
            await _context.SaveChangesAsync(cancellationToken);
            await dbTransaction.CommitAsync(cancellationToken);

            _logger.LogInformation("Successfully registered user {Username} ({AccountNumber}) funded with BDT {Balance}",
                user.Username, account.AccountNumber, account.Balance);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Registration transaction failed for username {Username}. Rolling back.", request.Username);
            await dbTransaction.RollbackAsync(cancellationToken);
            throw;
        }

        var token = _jwtTokenGenerator.GenerateToken(user, account);

        return new AuthResponseDto(
            token,
            user.Id,
            user.FullName,
            user.Username,
            user.Email,
            user.Role,
            account.Id,
            account.AccountNumber,
            account.Balance,
            account.Currency,
            HasPinConfigured: !string.IsNullOrEmpty(user.TransactionPinHash));
    }

    public async Task<AuthResponseDto> LoginAsync(LoginRequestDto request, CancellationToken cancellationToken = default)
    {
        var identifier = request.UsernameOrEmail.Trim().ToLowerInvariant();

        var user = await _context.Users
            .Include(u => u.Account)
            .FirstOrDefaultAsync(u => u.Username == identifier || u.Email == identifier || u.PhoneNumber == identifier, cancellationToken);

        // Anti-enumeration: Generic error message for missing user or wrong password
        if (user == null || !_passwordHasher.VerifyPassword(request.Password, user.PasswordHash))
        {
            _logger.LogWarning("Failed login attempt for identifier: {Identifier}", identifier);

            // P1/P3 FIX: Record FAILED_LOGIN event so RiskShield can react to it.
            // Only record if user exists (don't create audit records for unknown identifiers to avoid leaking user existence).
            if (user != null)
            {
                var failedLoginAudit = new SystemAuditLog(
                    user.Id,
                    "FAILED_LOGIN",
                    "User",
                    user.Id.ToString(),
                    metadataJson: "{\"reason\":\"InvalidPassword\"}");
                await _context.SystemAuditLogs.AddAsync(failedLoginAudit, cancellationToken);
                await _context.SaveChangesAsync(cancellationToken);
            }

            throw new DomainException(ErrorCodes.InvalidCredentials, "Invalid credentials. Please check your username/email and password.", 401);
        }

        if (user.Status != UserStatus.Active)
        {
            throw new DomainException(ErrorCodes.UnauthorizedAccess, $"Account status is {user.Status}. Please contact support.", 403);
        }

        var token = _jwtTokenGenerator.GenerateToken(user, user.Account);

        var auditLog = new SystemAuditLog(
            user.Id,
            "USER_LOGIN",
            "User",
            user.Id.ToString(),
            metadataJson: "{\"status\":\"Success\"}");

        await _context.SystemAuditLogs.AddAsync(auditLog, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);

        return new AuthResponseDto(
            token,
            user.Id,
            user.FullName,
            user.Username,
            user.Email,
            user.Role,
            user.Account?.Id ?? Guid.Empty,
            user.Account?.AccountNumber ?? "ACC-NONE",
            user.Account?.Balance ?? 0m,
            user.Account?.Currency ?? "BDT",
            HasPinConfigured: !string.IsNullOrEmpty(user.TransactionPinHash));
    }

    public async Task<CurrentUserResponseDto> GetCurrentUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await _context.Users
            .Include(u => u.Account)
            .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);

        if (user == null)
            throw new DomainException(ErrorCodes.UserNotFound, "User not found.", 404);

        return new CurrentUserResponseDto(
            user.Id,
            user.FullName,
            user.Username,
            user.Email,
            user.PhoneNumber,
            user.Role,
            user.Status.ToString(),
            user.Account?.Id ?? Guid.Empty,
            user.Account?.AccountNumber ?? "ACC-NONE",
            user.Account?.Balance ?? 0m,
            user.Account?.Currency ?? "BDT",
            HasPinConfigured: !string.IsNullOrEmpty(user.TransactionPinHash),
            MemberSinceUtc: user.CreatedAtUtc);
    }

    public async Task<PinOperationResultDto> SetTransactionPinAsync(Guid userId, SetPinRequestDto request, CancellationToken cancellationToken = default)
    {
        if (request.Pin.Trim() != request.ConfirmPin.Trim())
            throw new DomainException(ErrorCodes.ValidationFailed, "PIN and Confirm PIN do not match.");

        var user = await _context.Users.FindAsync([userId], cancellationToken);
        if (user == null)
            throw new DomainException(ErrorCodes.UserNotFound, "User not found.", 404);

        var pinHash = _passwordHasher.HashPassword(request.Pin.Trim());
        user.SetTransactionPin(pinHash);

        var auditLog = new SystemAuditLog(
            user.Id,
            "TRANSACTION_PIN_CONFIGURED",
            "User",
            user.Id.ToString());

        await _context.SystemAuditLogs.AddAsync(auditLog, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);

        return new PinOperationResultDto(true, "Transaction PIN configured successfully.");
    }

    public async Task<PinOperationResultDto> VerifyTransactionPinAsync(Guid userId, VerifyPinRequestDto request, CancellationToken cancellationToken = default)
    {
        var user = await _context.Users.FindAsync([userId], cancellationToken);
        if (user == null)
            throw new DomainException(ErrorCodes.UserNotFound, "User not found.", 404);

        if (string.IsNullOrEmpty(user.TransactionPinHash))
            throw new DomainException(ErrorCodes.ValidationFailed, "No transaction PIN has been set for this account. Please set a PIN first.");

        // P1/P3 FIX: PIN brute-force lockout.
        // Count failed PIN attempts in the last 15 minutes.
        var fifteenMinutesAgo = DateTime.UtcNow.AddMinutes(-15);
        var recentFailedPinAttempts = await _context.SystemAuditLogs
            .CountAsync(l => l.ActorId == userId &&
                             l.Action == "FAILED_PIN" &&
                             l.CreatedAtUtc >= fifteenMinutesAgo, cancellationToken);

        // Lock out after 5 failed PIN attempts in 15 minutes.
        if (recentFailedPinAttempts >= 5)
        {
            _logger.LogWarning("PIN lockout triggered for UserId: {UserId}. {Count} failed attempts in 15 minutes.",
                userId, recentFailedPinAttempts);

            var lockoutAudit = new SystemAuditLog(
                userId,
                "PIN_LOCKOUT",
                "User",
                userId.ToString(),
                metadataJson: $"{{\"failedAttempts\":{recentFailedPinAttempts},\"windowMinutes\":15}}");
            await _context.SystemAuditLogs.AddAsync(lockoutAudit, cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);

            throw new DomainException(ErrorCodes.InvalidCredentials,
                "Too many failed PIN attempts. Your PIN verification is temporarily locked. Please try again in 15 minutes.", 429);
        }

        var isValid = _passwordHasher.VerifyPassword(request.Pin.Trim(), user.TransactionPinHash);
        if (!isValid)
        {
            _logger.LogWarning("Failed PIN verification for UserId: {UserId}. Attempt {Attempt}/5.",
                userId, recentFailedPinAttempts + 1);

            // Record FAILED_PIN event — this feeds RiskShield's risk scoring
            var failedPinAudit = new SystemAuditLog(
                userId,
                "FAILED_PIN",
                "User",
                userId.ToString(),
                metadataJson: $"{{\"attemptNumber\":{recentFailedPinAttempts + 1}}}");
            await _context.SystemAuditLogs.AddAsync(failedPinAudit, cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);

            // Generic error message — don't tell client how many attempts remain (avoids enumeration)
            throw new DomainException(ErrorCodes.InvalidCredentials, "Invalid transaction PIN.", 401);
        }

        // Successful PIN verification — record it
        var successAudit = new SystemAuditLog(
            userId,
            "PIN_VERIFIED",
            "User",
            userId.ToString());
        await _context.SystemAuditLogs.AddAsync(successAudit, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);

        return new PinOperationResultDto(true, "Transaction PIN verified successfully.");
    }
}
