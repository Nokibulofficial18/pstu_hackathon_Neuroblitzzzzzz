using Microsoft.EntityFrameworkCore;
using NCash.Application.Contracts.Persistence;
using NCash.Domain.Common;

namespace NCash.Application.Modules.Users;

public record UserProfileDto(
    Guid Id,
    string FullName,
    string Username,
    string Email,
    string PhoneNumber,
    string Role,
    string AccountNumber,
    decimal Balance,
    string Currency,
    DateTime CreatedAtUtc);

public record ReceiverSearchResultDto(
    Guid AccountId,
    string AccountNumber,
    string FullName,
    string Username,
    string MaskedPhone,
    bool IsRegisteredUser);

public interface IUserService
{
    Task<UserProfileDto> GetUserProfileAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<ReceiverSearchResultDto?> LookupReceiverAsync(string query, CancellationToken cancellationToken = default);
    Task<List<ReceiverSearchResultDto>> GetSuggestedRecipientsAsync(Guid currentUserId, CancellationToken cancellationToken = default);
}

public class UserService : IUserService
{
    private readonly IApplicationDbContext _context;

    public UserService(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<UserProfileDto> GetUserProfileAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await _context.Users
            .Include(u => u.Account)
            .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);

        if (user == null)
            throw new DomainException(ErrorCodes.UserNotFound, "User not found.", 404);

        return new UserProfileDto(
            user.Id,
            user.FullName,
            user.Username,
            user.Email,
            user.PhoneNumber,
            user.Role,
            user.Account?.AccountNumber ?? "ACC-NONE",
            user.Account?.Balance ?? 0m,
            user.Account?.Currency ?? "BDT",
            user.CreatedAtUtc);
    }

    public async Task<ReceiverSearchResultDto?> LookupReceiverAsync(string query, CancellationToken cancellationToken = default)
    {
        var cleanQuery = query.Trim().ToLowerInvariant();

        var account = await _context.Accounts
            .Include(a => a.User)
            .Where(a => a.User.Role != "System")
            .FirstOrDefaultAsync(a =>
                a.AccountNumber.ToLower() == cleanQuery ||
                a.User.Username.ToLower() == cleanQuery ||
                a.User.PhoneNumber == query.Trim(), cancellationToken);

        if (account == null)
            return null;

        var phone = account.User.PhoneNumber;
        var maskedPhone = phone.Length > 6
            ? $"{phone[..3]}****{phone[^3..]}"
            : phone;

        return new ReceiverSearchResultDto(
            account.Id,
            account.AccountNumber,
            account.User.FullName,
            account.User.Username,
            maskedPhone,
            true);
    }

    public async Task<List<ReceiverSearchResultDto>> GetSuggestedRecipientsAsync(Guid currentUserId, CancellationToken cancellationToken = default)
    {
        var accounts = await _context.Accounts
            .Include(a => a.User)
            .Where(a => a.UserId != currentUserId && a.User.Role != "System" && a.User.IsActive)
            .OrderBy(a => a.User.FullName)
            .Take(10)
            .ToListAsync(cancellationToken);

        return accounts.Select(a =>
        {
            var phone = a.User.PhoneNumber;
            var maskedPhone = phone.Length > 6 ? $"{phone[..3]}****{phone[^3..]}" : phone;
            return new ReceiverSearchResultDto(
                a.Id,
                a.AccountNumber,
                a.User.FullName,
                a.User.Username,
                maskedPhone,
                true);
        }).ToList();
    }
}
