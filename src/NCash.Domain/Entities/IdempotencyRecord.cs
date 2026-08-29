using NCash.Domain.Common;
using NCash.Domain.Enums;

namespace NCash.Domain.Entities;

public class IdempotencyRecord : BaseEntity
{
    public string Key { get; private set; } = string.Empty;
    public Guid UserId { get; private set; }
    public string RequestPath { get; private set; } = string.Empty;
    public string RequestPayloadHash { get; private set; } = string.Empty;
    public int? ResponseStatusCode { get; private set; }
    public string? ResponseBodyJson { get; private set; }
    public IdempotencyStatus Status { get; private set; } = IdempotencyStatus.Processing;
    public DateTime ExpiresAtUtc { get; private set; }

    private IdempotencyRecord() { } // EF Core

    public IdempotencyRecord(string key, Guid userId, string requestPath, string requestPayloadHash, TimeSpan? ttl = null)
    {
        Key = key;
        UserId = userId;
        RequestPath = requestPath;
        RequestPayloadHash = requestPayloadHash;
        Status = IdempotencyStatus.Processing;
        ExpiresAtUtc = DateTime.UtcNow.Add(ttl ?? TimeSpan.FromHours(24));
    }

    public void Complete(int statusCode, string responseBodyJson)
    {
        Status = IdempotencyStatus.Completed;
        ResponseStatusCode = statusCode;
        ResponseBodyJson = responseBodyJson;
        Touch();
    }

    public void Fail(int statusCode, string responseBodyJson)
    {
        Status = IdempotencyStatus.Failed;
        ResponseStatusCode = statusCode;
        ResponseBodyJson = responseBodyJson;
        Touch();
    }
}
