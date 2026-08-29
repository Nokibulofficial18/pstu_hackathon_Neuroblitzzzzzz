using NCash.Domain.Common;

namespace NCash.Domain.Entities;

public class TransactionEvent : BaseEntity
{
    public Guid TransactionId { get; private set; }
    public string EventType { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public string? MetadataJson { get; private set; }

    // Navigation property
    public virtual Transaction Transaction { get; private set; } = null!;

    private TransactionEvent() { } // EF Core

    public TransactionEvent(Guid transactionId, string eventType, string description, string? metadataJson = null)
    {
        TransactionId = transactionId;
        EventType = eventType;
        Description = description;
        MetadataJson = metadataJson;
    }
}
