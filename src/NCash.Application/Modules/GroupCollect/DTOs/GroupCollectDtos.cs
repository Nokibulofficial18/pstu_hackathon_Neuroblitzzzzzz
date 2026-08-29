namespace NCash.Application.Modules.GroupCollect.DTOs;

public record MemberInvitationDto(
    string? MemberAccountNumber = null,
    decimal RequiredAmount = 0m,
    string? MemberId = null,
    string? UserId = null)
{
    public string ResolvedMember =>
        !string.IsNullOrWhiteSpace(MemberAccountNumber)
            ? MemberAccountNumber.Trim()
            : (MemberId?.Trim() ?? (UserId?.Trim() ?? string.Empty));
}

public record CreateGroupCollectionDto(
    string Title,
    string? Description = null,
    decimal TargetAmount = 0m,
    int ExpiryDays = 14,
    List<MemberInvitationDto>? InitialMembers = null,
    List<MemberInvitationDto>? Members = null)
{
    public string ResolvedDescription =>
        !string.IsNullOrWhiteSpace(Description) ? Description.Trim() : Title.Trim();

    public List<MemberInvitationDto> ResolvedMembers =>
        InitialMembers ?? Members ?? new List<MemberInvitationDto>();
}

public record InviteMemberRequestDto(
    string? MemberAccountNumber = null,
    decimal RequiredAmount = 0m,
    string? MemberId = null,
    string? UserId = null)
{
    public string ResolvedMember =>
        !string.IsNullOrWhiteSpace(MemberAccountNumber)
            ? MemberAccountNumber.Trim()
            : (MemberId?.Trim() ?? (UserId?.Trim() ?? string.Empty));
}

public record PayContributionDto(
    decimal? Amount = null,
    string? IdempotencyKey = null);

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
