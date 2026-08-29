using Microsoft.EntityFrameworkCore;
using NCash.Application.Contracts.Persistence;
using NCash.Domain.Entities;
using NCash.Infrastructure.Persistence;

namespace NCash.Infrastructure.Repositories;

public class IdempotencyRepository : IIdempotencyRepository
{
    private readonly NCashDbContext _context;

    public IdempotencyRepository(NCashDbContext context)
    {
        _context = context;
    }

    public async Task<IdempotencyRecord?> GetByKeyAsync(string key, CancellationToken cancellationToken = default)
    {
        return await _context.IdempotencyRecords
            .FirstOrDefaultAsync(r => r.Key == key && r.ExpiresAtUtc > DateTime.UtcNow, cancellationToken);
    }

    public async Task AddAsync(IdempotencyRecord record, CancellationToken cancellationToken = default)
    {
        await _context.IdempotencyRecords.AddAsync(record, cancellationToken);
    }

    public Task UpdateAsync(IdempotencyRecord record, CancellationToken cancellationToken = default)
    {
        var entry = _context.Entry(record);
        if (entry.State == EntityState.Detached)
        {
            _context.IdempotencyRecords.Update(record);
        }
        return Task.CompletedTask;
    }
}
