using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NCash.Application.Contracts.Persistence;
using NCash.Application.Modules.GroupCollect.DTOs;
using NCash.Application.Modules.PaymentEngine;
using NCash.Application.Modules.PaymentEngine.DTOs;
using NCash.Domain.Common;
using NCash.Domain.Entities;
using NCash.Domain.Enums;

namespace NCash.Application.Modules.GroupCollect;

public interface IGroupCollectService
{
    Task<GroupCollectionDetailDto> CreateCollectionAsync(Guid creatorUserId, CreateGroupCollectionDto dto, CancellationToken cancellationToken = default);
    Task<GroupCollectionDetailDto> InviteMemberAsync(Guid currentUserId, Guid collectionId, InviteMemberRequestDto dto, CancellationToken cancellationToken = default);
    Task<GroupCollectionDetailDto> GetCollectionByIdAsync(Guid currentUserId, Guid collectionId, CancellationToken cancellationToken = default);
    Task<List<GroupCollectionDetailDto>> GetUserCollectionsAsync(Guid currentUserId, CancellationToken cancellationToken = default);
    Task<TransferResultDto> PayContributionAsync(Guid currentUserId, Guid collectionId, PayContributionDto dto, CancellationToken cancellationToken = default);
    Task<GroupCollectionDetailDto> CancelCollectionAsync(Guid currentUserId, Guid collectionId, CancellationToken cancellationToken = default);
}

public class GroupCollectService : IGroupCollectService
{
    private readonly IApplicationDbContext _context;
    private readonly IAccountRepository _accountRepository;
    private readonly IPaymentEngine _paymentEngine;
    private readonly ILogger<GroupCollectService> _logger;

    public GroupCollectService(
        IApplicationDbContext context,
        IAccountRepository accountRepository,
        IPaymentEngine paymentEngine,
        ILogger<GroupCollectService> logger)
    {
        _context = context;
        _accountRepository = accountRepository;
        _paymentEngine = paymentEngine;
        _logger = logger;
    }

    public async Task<GroupCollectionDetailDto> CreateCollectionAsync(Guid creatorUserId, CreateGroupCollectionDto dto, CancellationToken cancellationToken = default)
    {
        if (dto.TargetAmount <= 0)
            throw new DomainException(ErrorCodes.InvalidAmount, "Target collection amount must be greater than zero.");

        var creatorAccount = await _accountRepository.GetByUserIdAsync(creatorUserId, cancellationToken);
        if (creatorAccount == null)
            throw new DomainException(ErrorCodes.AccountNotFound, "Creator account not found.");

        var expiresAt = DateTime.UtcNow.AddDays(dto.ExpiryDays > 0 ? dto.ExpiryDays : 14);

        var collection = new GroupCollection(
            creatorUserId,
            creatorAccount.Id,
            dto.Title.Trim(),
            dto.ResolvedDescription,
            dto.TargetAmount,
            expiresAt);

        await _context.GroupCollections.AddAsync(collection, cancellationToken);

        var members = dto.ResolvedMembers;
        if (members != null && members.Count > 0)
        {
            var defaultSplitAmount = Math.Round(dto.TargetAmount / Math.Max(1, members.Count + 1), 2);
            foreach (var memberDto in members)
            {
                var memberQuery = memberDto.ResolvedMember;
                if (string.IsNullOrWhiteSpace(memberQuery)) continue;

                var memberAcc = await _accountRepository.GetByIdentifierAsync(memberQuery, cancellationToken);
                if (memberAcc == null)
                    throw new DomainException(ErrorCodes.AccountNotFound, $"Member account '{memberQuery}' not found.");

                if (memberAcc.UserId == creatorUserId)
                    continue;

                var requiredAmt = memberDto.RequiredAmount > 0 ? memberDto.RequiredAmount : defaultSplitAmount;

                var member = new GroupCollectionMember(collection.Id, memberAcc.UserId, memberAcc.Id, requiredAmt);
                await _context.GroupCollectionMembers.AddAsync(member, cancellationToken);
            }
        }

        await _context.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Group Collection created: ID {Id}, Title {Title}, Target BDT {Amount}", collection.Id, collection.Title, collection.TargetAmount);

        return await GetCollectionByIdAsync(creatorUserId, collection.Id, cancellationToken);
    }

    public async Task<GroupCollectionDetailDto> InviteMemberAsync(Guid currentUserId, Guid collectionId, InviteMemberRequestDto dto, CancellationToken cancellationToken = default)
    {
        var collection = await _context.GroupCollections
            .Include(g => g.Members)
            .FirstOrDefaultAsync(g => g.Id == collectionId, cancellationToken);

        if (collection == null)
            throw new DomainException(ErrorCodes.InvalidTransactionState, "Group collection not found.", 404);

        if (collection.CreatorUserId != currentUserId)
            throw new DomainException(ErrorCodes.UnauthorizedAccess, "Only the collection creator can invite members.", 403);

        collection.CheckExpiration();
        if (collection.Status == GroupCollectionStatus.Cancelled || collection.Status == GroupCollectionStatus.Expired || collection.Status == GroupCollectionStatus.Paid)
            throw new DomainException(ErrorCodes.InvalidTransactionState, $"Cannot invite members to a collection in status {collection.Status}.");

        var memberQuery = dto.ResolvedMember;
        if (string.IsNullOrWhiteSpace(memberQuery))
            throw new DomainException(ErrorCodes.ValidationFailed, "Member identifier is required.");

        var memberAcc = await _accountRepository.GetByIdentifierAsync(memberQuery, cancellationToken);
        if (memberAcc == null)
            throw new DomainException(ErrorCodes.AccountNotFound, $"Member account '{memberQuery}' not found.");

        if (memberAcc.UserId == currentUserId)
            throw new DomainException(ErrorCodes.SelfTransferNotAllowed, "Cannot invite yourself as a member to your own collection.");

        var existingMember = collection.Members.FirstOrDefault(m => m.UserId == memberAcc.UserId);
        if (existingMember != null)
            throw new DomainException(ErrorCodes.DuplicateRequest, "This user is already a member of this collection.");

        var requiredAmount = dto.RequiredAmount > 0
            ? dto.RequiredAmount
            : Math.Max(100m, (collection.TargetAmount - collection.CollectedAmount) / Math.Max(1, collection.Members.Count + 1));

        var member = new GroupCollectionMember(collection.Id, memberAcc.UserId, memberAcc.Id, requiredAmount);
        await _context.GroupCollectionMembers.AddAsync(member, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("User {UserId} invited to Group Collection {CollectionId} with target BDT {Amount}", memberAcc.UserId, collection.Id, dto.RequiredAmount);

        return await GetCollectionByIdAsync(currentUserId, collection.Id, cancellationToken);
    }

    public async Task<GroupCollectionDetailDto> GetCollectionByIdAsync(Guid currentUserId, Guid collectionId, CancellationToken cancellationToken = default)
    {
        var collection = await _context.GroupCollections
            .Include(g => g.CreatorUser)
            .Include(g => g.CreatorAccount)
            .Include(g => g.Members).ThenInclude(m => m.User)
            .Include(g => g.Members).ThenInclude(m => m.Account)
            .FirstOrDefaultAsync(g => g.Id == collectionId, cancellationToken);

        if (collection == null)
            throw new DomainException(ErrorCodes.InvalidTransactionState, "Group collection not found.", 404);

        collection.CheckExpiration();

        var isCreator = collection.CreatorUserId == currentUserId;
        var isMember = collection.Members.Any(m => m.UserId == currentUserId);

        if (!isCreator && !isMember)
            throw new DomainException(ErrorCodes.UnauthorizedAccess, "You do not have permission to view this group collection.", 403);

        return MapToDetailDto(collection);
    }

    public async Task<List<GroupCollectionDetailDto>> GetUserCollectionsAsync(Guid currentUserId, CancellationToken cancellationToken = default)
    {
        var collections = await _context.GroupCollections
            .Include(g => g.CreatorUser)
            .Include(g => g.CreatorAccount)
            .Include(g => g.Members).ThenInclude(m => m.User)
            .Include(g => g.Members).ThenInclude(m => m.Account)
            .Where(g => g.CreatorUserId == currentUserId || g.Members.Any(m => m.UserId == currentUserId))
            .OrderByDescending(g => g.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        foreach (var col in collections)
        {
            col.CheckExpiration();
        }

        return collections.Select(MapToDetailDto).ToList();
    }

    public async Task<TransferResultDto> PayContributionAsync(Guid currentUserId, Guid collectionId, PayContributionDto dto, CancellationToken cancellationToken = default)
    {
        // ── P1 FIX: Single atomic database transaction for the entire contribution payment ─
        // BEFORE: PaymentEngine committed internally, then member/collection state update
        // happened in a SEPARATE SaveChangesAsync. A failure between them would leave
        // money moved but collection status unchanged.
        // NOW: Both transfer AND aggregate state update share ONE transaction.

        await using var dbTransaction = await _context.BeginTransactionAsync(cancellationToken);
        try
        {
            // Load collection — guarded by the outer DB transaction + PaymentEngine account-level locks.
            // Status checks below prevent duplicate contributions under the transaction.
            var collection = await _context.GroupCollections
                .Include(g => g.CreatorAccount)
                .Include(g => g.Members)
                .FirstOrDefaultAsync(g => g.Id == collectionId, cancellationToken);

            if (collection == null)
                throw new DomainException(ErrorCodes.InvalidTransactionState, "Group collection not found.", 404);

            collection.CheckExpiration();

            if (collection.Status == GroupCollectionStatus.Cancelled)
                throw new DomainException(ErrorCodes.InvalidTransactionState, "This group collection has been cancelled.");

            if (collection.Status == GroupCollectionStatus.Expired)
                throw new DomainException(ErrorCodes.InvalidTransactionState, "This group collection has expired.");

            if (collection.Status == GroupCollectionStatus.Paid)
                throw new DomainException(ErrorCodes.InvalidTransactionState, "This group collection is already fully collected/paid.");

            var member = collection.Members.FirstOrDefault(m => m.UserId == currentUserId);
            if (member == null)
                throw new DomainException(ErrorCodes.UnauthorizedAccess, "You are not an invited member of this group collection.", 403);

            if (member.Status == GroupMemberStatus.Paid)
                throw new DomainException(ErrorCodes.InvalidTransactionState, "You have already fully paid your assigned contribution.");

            // Re-read under lock
            var remainingUnderLock = member.RemainingAmount;
            var payAmount = dto.Amount.HasValue && dto.Amount.Value > 0
                ? dto.Amount.Value
                : remainingUnderLock;

            if (payAmount <= 0)
                throw new DomainException(ErrorCodes.InvalidAmount, "Payment amount must be greater than zero.");

            if (payAmount > remainingUnderLock)
                throw new DomainException(ErrorCodes.InvalidAmount, $"Contribution amount BDT {payAmount:N2} exceeds your remaining assigned share of BDT {remainingUnderLock:N2}.");

            // Execute payment via PaymentEngine — sharing OUR transaction
            var idempotencyKey = string.IsNullOrWhiteSpace(dto.IdempotencyKey)
                ? $"GROUP-CONTRIB-{collection.Id:N}-{member.Id:N}-{member.PaidAmount:F0}-{payAmount:F0}"
                : dto.IdempotencyKey.Trim();

            var transferCommand = new ExecuteTransferCommand(
                SenderAccountId: member.AccountId,
                ReceiverAccountId: collection.CreatorAccountId,
                Amount: payAmount,
                IdempotencyKey: idempotencyKey,
                Type: TransactionType.GroupCollectionPayment,
                Purpose: $"Contribution to '{collection.Title}'",
                Fee: 0m,
                BypassRiskCheck: false,
                ExternalTransaction: dbTransaction);  // Share our transaction

            var paymentResult = await _paymentEngine.ExecutePaymentAsync(transferCommand, cancellationToken);

            // Update domain collection tracking state — same transaction
            member.RecordPayment(payAmount);
            collection.RecordMemberPayment(payAmount);

            // Single commit: transfer + collection state
            await _context.SaveChangesAsync(cancellationToken);
            await dbTransaction.CommitAsync(cancellationToken);

            _logger.LogInformation("Member {UserId} contributed BDT {Amount} to Collection {CollectionId}. Status: {Status}",
                currentUserId, payAmount, collection.Id, collection.Status);

            return paymentResult;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Group contribution payment failed for Collection {CollectionId}. Rolling back.", collectionId);
            await dbTransaction.RollbackAsync(cancellationToken);
            _context.ChangeTracker.Clear();
            throw;
        }
    }


    public async Task<GroupCollectionDetailDto> CancelCollectionAsync(Guid currentUserId, Guid collectionId, CancellationToken cancellationToken = default)
    {
        var collection = await _context.GroupCollections
            .Include(g => g.CreatorUser)
            .Include(g => g.CreatorAccount)
            .Include(g => g.Members).ThenInclude(m => m.User)
            .Include(g => g.Members).ThenInclude(m => m.Account)
            .FirstOrDefaultAsync(g => g.Id == collectionId, cancellationToken);

        if (collection == null)
            throw new DomainException(ErrorCodes.InvalidTransactionState, "Group collection not found.", 404);

        if (collection.CreatorUserId != currentUserId)
            throw new DomainException(ErrorCodes.UnauthorizedAccess, "Only the creator can cancel this group collection.", 403);

        collection.Cancel();
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Group Collection {CollectionId} cancelled by owner {UserId}", collection.Id, currentUserId);

        return MapToDetailDto(collection);
    }

    private static GroupCollectionDetailDto MapToDetailDto(GroupCollection collection)
    {
        var members = collection.Members.Select(m => new GroupMemberDto(
            m.Id,
            m.UserId,
            m.User?.Username ?? "Member",
            m.User?.FullName ?? "Member",
            m.Account?.AccountNumber ?? "ACC-UNKNOWN",
            m.RequiredAmount,
            m.PaidAmount,
            m.RemainingAmount,
            m.Status.ToString())).ToList();

        return new GroupCollectionDetailDto(
            collection.Id,
            collection.CreatorUserId,
            collection.CreatorUser?.Username ?? "Owner",
            collection.CreatorAccount?.AccountNumber ?? "ACC-UNKNOWN",
            collection.Title,
            collection.Description,
            collection.TargetAmount,
            collection.CollectedAmount,
            collection.RemainingAmount,
            collection.Status.ToString(),
            collection.ExpiresAtUtc,
            collection.CreatedAtUtc,
            members);
    }
}
