using NCash.Domain.Common;
using NCash.Domain.Enums;

namespace NCash.Domain.Entities;

public class DisputeCase : BaseEntity
{
    public Guid TransactionId { get; private set; }
    public Guid ReportedByUserId { get; private set; }
    public string Category { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public DisputeStatus Status { get; private set; } = DisputeStatus.Open;
    public string? ResolutionNote { get; private set; }
    public DateTime? ResolvedAtUtc { get; private set; }

    // Navigation property
    public virtual Transaction Transaction { get; private set; } = null!;
    public virtual User ReportedByUser { get; private set; } = null!;

    private DisputeCase() { } // EF Core

    public DisputeCase(Guid transactionId, Guid reportedByUserId, string category, string description)
    {
        TransactionId = transactionId;
        ReportedByUserId = reportedByUserId;
        Category = category;
        Description = description;
        Status = DisputeStatus.Open;
    }

    public void MarkUnderReview()
    {
        Status = DisputeStatus.UnderReview;
        Touch();
    }

    public void Resolve(string resolutionNote)
    {
        Status = DisputeStatus.Resolved;
        ResolutionNote = resolutionNote;
        ResolvedAtUtc = DateTime.UtcNow;
        Touch();
    }

    public void Reject(string reason)
    {
        Status = DisputeStatus.Rejected;
        ResolutionNote = reason;
        ResolvedAtUtc = DateTime.UtcNow;
        Touch();
    }
}
