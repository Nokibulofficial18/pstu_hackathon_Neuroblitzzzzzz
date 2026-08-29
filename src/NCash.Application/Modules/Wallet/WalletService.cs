using Microsoft.EntityFrameworkCore;
using NCash.Application.Contracts.Persistence;
using NCash.Domain.Common;
using NCash.Domain.Enums;

namespace NCash.Application.Modules.Wallet;

public record WalletSummaryDto(
    Guid AccountId,
    string AccountNumber,
    decimal AvailableBalance,
    string Currency,
    string Status,
    decimal TotalSent,
    decimal TotalReceived,
    int TotalTransactionsCount,
    int PendingRequestsCount,
    DateTime LastUpdatedUtc);

public interface IWalletService
{
    Task<WalletSummaryDto> GetWalletSummaryAsync(Guid userId, CancellationToken cancellationToken = default);
}

public class WalletService : IWalletService
{
    private readonly IApplicationDbContext _context;
    private readonly IAccountRepository _accountRepository;

    public WalletService(IApplicationDbContext context, IAccountRepository accountRepository)
    {
        _context = context;
        _accountRepository = accountRepository;
    }

    public async Task<WalletSummaryDto> GetWalletSummaryAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var account = await _accountRepository.GetByUserIdAsync(userId, cancellationToken);
        if (account == null)
            throw new DomainException(ErrorCodes.AccountNotFound, "Wallet account not found for user.");

        var totalSent = await _context.Transactions
            .Where(t => t.SenderAccountId == account.Id && t.Status == TransactionStatus.Succeeded)
            .SumAsync(t => (decimal?)t.Amount, cancellationToken) ?? 0m;

        var totalReceived = await _context.Transactions
            .Where(t => t.ReceiverAccountId == account.Id && t.Status == TransactionStatus.Succeeded)
            .SumAsync(t => (decimal?)t.Amount, cancellationToken) ?? 0m;

        var totalTransactionsCount = await _context.Transactions
            .CountAsync(t => t.SenderAccountId == account.Id || t.ReceiverAccountId == account.Id, cancellationToken);

        var pendingRequestsCount = await _context.MoneyRequests
            .CountAsync(m => m.PayerAccountId == account.Id && m.Status == Domain.Enums.MoneyRequestStatus.Pending, cancellationToken);

        return new WalletSummaryDto(
            account.Id,
            account.AccountNumber,
            account.Balance,
            account.Currency,
            account.Status.ToString(),
            totalSent,
            totalReceived,
            totalTransactionsCount,
            pendingRequestsCount,
            account.UpdatedAtUtc);
    }
}
