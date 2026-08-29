using NCash.Domain.Common;

namespace NCash.Domain.Entities;

public class SystemAuditLog : BaseEntity
{
    public Guid? ActorId { get; private set; }
    public string Action { get; private set; } = string.Empty;
    public string EntityName { get; private set; } = string.Empty;
    public string EntityId { get; private set; } = string.Empty;
    public string? OldValueJson { get; private set; }
    public string? NewValueJson { get; private set; }
    public string? MetadataJson { get; private set; }
    public string? IpAddress { get; private set; }
    public string? UserAgent { get; private set; }

    private SystemAuditLog() { } // EF Core

    public SystemAuditLog(
        Guid? actorId,
        string action,
        string entityName,
        string entityId,
        string? oldValueJson = null,
        string? newValueJson = null,
        string? metadataJson = null,
        string? ipAddress = null,
        string? userAgent = null)
    {
        ActorId = actorId;
        Action = action;
        EntityName = entityName;
        EntityId = entityId;
        OldValueJson = oldValueJson;
        NewValueJson = newValueJson;
        MetadataJson = metadataJson;
        IpAddress = ipAddress;
        UserAgent = userAgent;
    }
}
