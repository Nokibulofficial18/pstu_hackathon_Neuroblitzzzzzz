using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NCash.Application.Contracts.Persistence;
using NCash.Application.Modules.PaymentEngine;
using NCash.Application.Modules.PaymentEngine.DTOs;
using NCash.Domain.Common;
using NCash.Domain.Entities;
using NCash.Domain.Enums;

namespace NCash.Application.Modules.MoneyRequests;

public record CreateMoneyRequestDto(
    string? PayerAccountNumber = null,
    decimal Amount = 0m,
    string? Note = null,
    int ExpiryDays = 7,
    string? PayerId = null)
{
    public string ResolvedPayer =>
        !string.IsNullOrWhiteSpace(PayerAccountNumber)
            ? PayerAccountNumber.Trim()
            : (PayerId?.Trim() ?? string.Empty);
}

public record MoneyRequestResponseDto(
    Guid Id,
    Guid RequesterAccountId,
    string RequesterName,
    string RequesterAccountNumber,
    Guid PayerAccountId,
    string PayerName,
    string PayerAccountNumber,
    decimal Amount,
    decimal PaidAmount,
    decimal RemainingAmount,
    string Status,
    string? Note,
    DateTime? ExpiresAtUtc,
    DateTime? CompletedAtUtc,
    DateTime CreatedAtUtc)
{
    public string RequesterUsername => RequesterName;
    public string PayerUsername => PayerName;
    public DateTime CreatedAt => CreatedAtUtc;
}

public record PayMoneyRequestDto(
    decimal? PaymentAmount = null,
    string? IdempotencyKey = null,
    decimal? Amount = null)
{
    public decimal? ResolvedAmount => PaymentAmount ?? Amount;
}

public interface IMoneyRequestService
{
    Task<MoneyRequestResponseDto> CreateRequestAsync(Guid requesterUserId, CreateMoneyRequestDto dto, CancellationToken cancellationToken = default);
    Task<MoneyRequestResponseDto> GetRequestByIdAsync(Guid userId, Guid requestId, CancellationToken cancellationToken = default);
    Task<List<MoneyRequestResponseDto>> GetUserRequestsAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<List<MoneyRequestResponseDto>> GetIncomingRequestsAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<List<MoneyRequestResponseDto>> GetOutgoingRequestsAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<TransferResultDto> PayRequestAsync(Guid payerUserId, Guid requestId, PayMoneyRequestDto dto, CancellationToken cancellationToken = default);
    Task<MoneyRequestResponseDto> RejectRequestAsync(Guid payerUserId, Guid requestId, CancellationToken cancellationToken = default);
    Task<MoneyRequestResponseDto> CancelRequestAsync(Guid requesterUserId, Guid requestId, CancellationToken cancellationToken = default);
}

public class MoneyRequestService : IMoneyRequestService
{
    private readonly IApplicationDbContext _context;
    private readonly IAccountRepository _accountRepository;
    private readonly IPaymentEngine _paymentEngine;
    private readonly ILogger<MoneyRequestService> _logger;

    public MoneyRequestService(
        IApplicationDbContext context,
        IAccountRepository accountRepository,
        IPaymentEngine paymentEngine,
        ILogger<MoneyRequestService> logger)
    {
        _context = context;
        _accountRepository = accountRepository;
        _paymentEngine = paymentEngine;
        _logger = logger;
    }

    public async Task<MoneyRequestResponseDto> CreateRequestAsync(Guid requesterUserId, CreateMoneyRequestDto dto, CancellationToken cancellationToken = default)
    {
        var requesterAccount = await _accountRepository.GetByUserIdAsync(requesterUserId, cancellationToken);
        if (requesterAccount == null)
            throw new DomainException(ErrorCodes.AccountNotFound, "Requester account not found.");

        var payerQuery = dto.ResolvedPayer;
        if (string.IsNullOrWhiteSpace(payerQuery))
            throw new DomainException(ErrorCodes.ValidationFailed, "Payer account number or username is required.");

        var payerAccount = await _accountRepository.GetByIdentifierAsync(payerQuery, cancellationToken);
        if (payerAccount == null)
            throw new DomainException(ErrorCodes.RecipientNotFound, $"Payer account '{payerQuery}' not found.");

        if (requesterAccount.Id == payerAccount.Id)
            throw new DomainException(ErrorCodes.SelfTransferNotAllowed, "Cannot request money from your own account.");

        var expiresAt = DateTime.UtcNow.AddDays(dto.ExpiryDays > 0 ? dto.ExpiryDays : 7);
        var request = new MoneyRequest(requesterAccount.Id, payerAccount.Id, dto.Amount, dto.Note, expiresAt);

        await _context.MoneyRequests.AddAsync(request, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);

        return await MapToDtoAsync(request.Id, cancellationToken);
    }

    public async Task<MoneyRequestResponseDto> GetRequestByIdAsync(Guid userId, Guid requestId, CancellationToken cancellationToken = default)
    {
        var account = await _accountRepository.GetByUserIdAsync(userId, cancellationToken);
        if (account == null)
            throw new DomainException(ErrorCodes.AccountNotFound, "User account not found.");

        var request = await _context.MoneyRequests
            .Include(r => r.RequesterAccount).ThenInclude(a => a.User)
            .Include(r => r.PayerAccount).ThenInclude(a => a.User)
            .FirstOrDefaultAsync(r => r.Id == requestId, cancellationToken);

        if (request == null)
            throw new DomainException(ErrorCodes.MoneyRequestNotFound, "Money request not found.", 404);

        if (request.RequesterAccountId != account.Id && request.PayerAccountId != account.Id)
            throw new DomainException(ErrorCodes.UnauthorizedAccess, "You are not authorized to view this money request.", 403);

        request.CheckExpiration();
        return MapFromEntity(request);
    }

    public async Task<List<MoneyRequestResponseDto>> GetUserRequestsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var account = await _accountRepository.GetByUserIdAsync(userId, cancellationToken);
        if (account == null)
            return [];

        var requests = await _context.MoneyRequests
            .Include(r => r.RequesterAccount).ThenInclude(a => a.User)
            .Include(r => r.PayerAccount).ThenInclude(a => a.User)
            .Where(r => r.RequesterAccountId == account.Id || r.PayerAccountId == account.Id)
            .OrderByDescending(r => r.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        foreach (var req in requests)
        {
            req.CheckExpiration();
        }

        return requests.Select(MapFromEntity).ToList();
    }

    public async Task<List<MoneyRequestResponseDto>> GetIncomingRequestsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var account = await _accountRepository.GetByUserIdAsync(userId, cancellationToken);
        if (account == null)
            throw new DomainException(ErrorCodes.AccountNotFound, "User account not found.");

        var requests = await _context.MoneyRequests
            .Include(m => m.RequesterAccount).ThenInclude(a => a.User)
            .Include(m => m.PayerAccount).ThenInclude(a => a.User)
            .Where(m => m.PayerAccountId == account.Id)
            .OrderByDescending(m => m.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        return requests.Select(MapFromEntity).ToList();
    }

    public async Task<List<MoneyRequestResponseDto>> GetOutgoingRequestsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var account = await _accountRepository.GetByUserIdAsync(userId, cancellationToken);
        if (account == null)
            throw new DomainException(ErrorCodes.AccountNotFound, "User account not found.");

        var requests = await _context.MoneyRequests
            .Include(m => m.RequesterAccount).ThenInclude(a => a.User)
            .Include(m => m.PayerAccount).ThenInclude(a => a.User)
            .Where(m => m.RequesterAccountId == account.Id)
            .OrderByDescending(m => m.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        return requests.Select(MapFromEntity).ToList();
    }

    public async Task<TransferResultDto> PayRequestAsync(Guid payerUserId, Guid requestId, PayMoneyRequestDto dto, CancellationToken cancellationToken = default)
    {
        var payerAccount = await _accountRepository.GetByUserIdAsync(payerUserId, cancellationToken);
        if (payerAccount == null)
            throw new DomainException(ErrorCodes.AccountNotFound, "Payer account not found.");

        var request = await _context.MoneyRequests
            .Include(m => m.RequesterAccount)
            .FirstOrDefaultAsync(m => m.Id == requestId, cancellationToken);

        if (request == null)
            throw new DomainException(ErrorCodes.MoneyRequestNotFound, "Money request not found.", 404);

        if (request.PayerAccountId != payerAccount.Id)
            throw new DomainException(ErrorCodes.UnauthorizedAccess, "You are not authorized to pay this request.", 403);

        if (request.ExpiresAtUtc.HasValue && request.ExpiresAtUtc.Value < DateTime.UtcNow)
        {
            throw new DomainException(ErrorCodes.MoneyRequestExpired, "This money request has expired.");
        }

        var amountToPay = dto.ResolvedAmount ?? request.RemainingAmount;
        if (amountToPay <= 0 || amountToPay > request.RemainingAmount)
        {
            throw new DomainException(ErrorCodes.MoneyRequestInvalidAmount,
                $"Invalid payment amount {amountToPay:N2}. Remaining owed: {request.RemainingAmount:N2}.");
        }

        // Execute payment via the isolated PaymentEngine
        var command = new ExecuteTransferCommand(
            payerAccount.Id,
            request.RequesterAccountId,
            amountToPay,
            !string.IsNullOrWhiteSpace(dto.IdempotencyKey) ? dto.IdempotencyKey : $"REQ-PAY-{requestId:N}-{Guid.NewGuid():N}",
            TransactionType.MoneyRequestPayment,
            $"Payment for request #{request.Id.ToString()[..8]}. Note: {request.Note ?? "None"}",
            0m);

        var transferResult = await _paymentEngine.ExecutePaymentAsync(command, cancellationToken);

        // Apply to request lifecycle
        request.ApplyPayment(amountToPay);
        _context.MoneyRequests.Update(request);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Money request {RequestId} paid amount {Amount}. New status: {Status}",
            requestId, amountToPay, request.Status);

        return transferResult;
    }

    public async Task<MoneyRequestResponseDto> RejectRequestAsync(Guid payerUserId, Guid requestId, CancellationToken cancellationToken = default)
    {
        var payerAccount = await _accountRepository.GetByUserIdAsync(payerUserId, cancellationToken);
        if (payerAccount == null)
            throw new DomainException(ErrorCodes.AccountNotFound, "Payer account not found.");

        var request = await _context.MoneyRequests
            .FirstOrDefaultAsync(m => m.Id == requestId, cancellationToken);

        if (request == null)
            throw new DomainException(ErrorCodes.MoneyRequestNotFound, "Money request not found.", 404);

        if (request.PayerAccountId != payerAccount.Id)
            throw new DomainException(ErrorCodes.UnauthorizedAccess, "Unauthorized.", 403);

        request.Reject();
        _context.MoneyRequests.Update(request);
        await _context.SaveChangesAsync(cancellationToken);

        return await MapToDtoAsync(requestId, cancellationToken);
    }

    public async Task<MoneyRequestResponseDto> CancelRequestAsync(Guid requesterUserId, Guid requestId, CancellationToken cancellationToken = default)
    {
        var requesterAccount = await _accountRepository.GetByUserIdAsync(requesterUserId, cancellationToken);
        if (requesterAccount == null)
            throw new DomainException(ErrorCodes.AccountNotFound, "Requester account not found.");

        var request = await _context.MoneyRequests
            .FirstOrDefaultAsync(m => m.Id == requestId, cancellationToken);

        if (request == null)
            throw new DomainException(ErrorCodes.MoneyRequestNotFound, "Money request not found.", 404);

        if (request.RequesterAccountId != requesterAccount.Id)
            throw new DomainException(ErrorCodes.UnauthorizedAccess, "Unauthorized.", 403);

        request.Cancel();
        _context.MoneyRequests.Update(request);
        await _context.SaveChangesAsync(cancellationToken);

        return await MapToDtoAsync(requestId, cancellationToken);
    }

    private async Task<MoneyRequestResponseDto> MapToDtoAsync(Guid requestId, CancellationToken cancellationToken)
    {
        var m = await _context.MoneyRequests
            .Include(r => r.RequesterAccount).ThenInclude(a => a.User)
            .Include(r => r.PayerAccount).ThenInclude(a => a.User)
            .FirstAsync(r => r.Id == requestId, cancellationToken);

        return MapFromEntity(m);
    }

    private static MoneyRequestResponseDto MapFromEntity(MoneyRequest m)
    {
        return new MoneyRequestResponseDto(
            m.Id,
            m.RequesterAccountId,
            m.RequesterAccount.User.FullName,
            m.RequesterAccount.AccountNumber,
            m.PayerAccountId,
            m.PayerAccount.User.FullName,
            m.PayerAccount.AccountNumber,
            m.Amount,
            m.PaidAmount,
            m.RemainingAmount,
            m.Status.ToString(),
            m.Note,
            m.ExpiresAtUtc,
            m.CompletedAtUtc,
            m.CreatedAtUtc);
    }
}
