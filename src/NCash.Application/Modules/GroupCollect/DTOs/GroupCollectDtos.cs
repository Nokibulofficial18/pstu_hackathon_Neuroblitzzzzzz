namespace NCash.Application.Modules.GroupCollect.DTOs;

public record MemberInvitationDto(
    string MemberAccountNumber,
    decimal RequiredAmount);

public record CreateGroupCollectionDto(
    string Title,
    string Description,
    decimal TargetAmount,
    int ExpiryDays = 14,
    List<MemberInvitationDto>? InitialMembers = null);

public record InviteMemberRequestDto(
    string MemberAccountNumber,
    decimal RequiredAmount);

public record PayContributionDto(
    decimal? Amount,
    string IdempotencyKey);

public record GroupMemberDto(
    Guid MemberId,
    Guid UserId,
    string Username,
    string FullName,
    string AccountNumber,
    decimal RequiredAmount,
    decimal PaidAmount,
    decimal RemainingAmount,
    string Status);

public record GroupCollectionDetailDto(
    Guid Id,
    Guid CreatorUserId,
    string CreatorUsername,
    string CreatorAccountNumber,
    string Title,
    string Description,
    decimal TargetAmount,
    decimal CollectedAmount,
    decimal RemainingAmount,
    string Status,
    DateTime? ExpiresAtUtc,
    DateTime CreatedAtUtc,
    List<GroupMemberDto> Members);
