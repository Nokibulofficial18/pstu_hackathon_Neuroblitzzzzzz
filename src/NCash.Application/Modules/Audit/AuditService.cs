using Microsoft.EntityFrameworkCore;
using NCash.Application.Contracts.Persistence;
using NCash.Domain.Entities;

namespace NCash.Application.Modules.Audit;

public record AuditLogDto(
    Guid Id,
    Guid? ActorId,
    string Action,
    string EntityName,
    string EntityId,
    string? OldValueJson,
    string? NewValueJson,
    string? IpAddress,
    string? UserAgent,
    DateTime CreatedAtUtc);

public interface IAuditService
{
    Task LogSecurityEventAsync(Guid? actorId, string action, string entityName, string entityId, string? oldValue = null, string? newValue = null, string? ip = null, string? userAgent = null, CancellationToken cancellationToken = default);
    Task<List<AuditLogDto>> GetRecentAuditLogsAsync(int limit = 50, CancellationToken cancellationToken = default);
    Task<List<AuditLogDto>> GetRecentLogsAsync(int page = 1, int pageSize = 50, CancellationToken cancellationToken = default);
    Task<List<AuditLogDto>> GetUserAuditLogsAsync(Guid userId, CancellationToken cancellationToken = default);
}

public class AuditService : IAuditService
{
    private readonly IApplicationDbContext _context;

    public AuditService(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task LogSecurityEventAsync(Guid? actorId, string action, string entityName, string entityId, string? oldValue = null, string? newValue = null, string? ip = null, string? userAgent = null, CancellationToken cancellationToken = default)
    {
        var log = new SystemAuditLog(actorId, action, entityName, entityId, oldValue, newValue, ip, userAgent);
        await _context.SystemAuditLogs.AddAsync(log, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<List<AuditLogDto>> GetRecentAuditLogsAsync(int limit = 50, CancellationToken cancellationToken = default)
    {
        var logs = await _context.SystemAuditLogs
            .OrderByDescending(l => l.CreatedAtUtc)
            .Take(limit)
            .ToListAsync(cancellationToken);

        return logs.Select(MapFromEntity).ToList();
    }

    public async Task<List<AuditLogDto>> GetRecentLogsAsync(int page = 1, int pageSize = 50, CancellationToken cancellationToken = default)
    {
        var logs = await _context.SystemAuditLogs
            .OrderByDescending(l => l.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return logs.Select(MapFromEntity).ToList();
    }

    public async Task<List<AuditLogDto>> GetUserAuditLogsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var logs = await _context.SystemAuditLogs
            .Where(l => l.ActorId == userId || l.EntityId == userId.ToString())
            .OrderByDescending(l => l.CreatedAtUtc)
            .Take(100)
            .ToListAsync(cancellationToken);

        return logs.Select(MapFromEntity).ToList();
    }

    private static AuditLogDto MapFromEntity(SystemAuditLog l) => new(
        l.Id,
        l.ActorId,
        l.Action,
        l.EntityName,
        l.EntityId,
        l.OldValueJson,
        l.NewValueJson,
        l.IpAddress,
        l.UserAgent,
        l.CreatedAtUtc);
}
