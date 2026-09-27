using CoreFoundry.Application.Assistant;
using CoreFoundry.Domain.Assistant;
using Microsoft.EntityFrameworkCore;

namespace CoreFoundry.Infrastructure.Persistence;

internal sealed class AssistantSessionRepository(MetadataDbContext db) : IAssistantSessionRepository
{
    public Task<AssistantSession?> FindAsync(long projectId, long sessionId, CancellationToken cancellationToken) =>
        db.AssistantSessions
            .Include(session => session.Messages)
            .SingleOrDefaultAsync(session => session.ProjectId == projectId && session.Id == sessionId, cancellationToken);

    public Task<AssistantSession?> FindOpenAsync(long projectId, CancellationToken cancellationToken) =>
        db.AssistantSessions
            .Include(session => session.Messages)
            .Where(session => session.ProjectId == projectId
                && (session.Status == AssistantSessionStatus.Asking || session.Status == AssistantSessionStatus.Proposed))
            .OrderByDescending(session => session.Id)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<AssistantSession>> ListAsync(long projectId, int take, CancellationToken cancellationToken) =>
        await db.AssistantSessions
            .Where(session => session.ProjectId == projectId)
            .OrderByDescending(session => session.Id)
            .Take(take)
            .ToListAsync(cancellationToken);

    public void Add(AssistantSession session) => db.AssistantSessions.Add(session);
}

internal sealed class AssistantUsageRepository(MetadataDbContext db) : IAssistantUsageRepository
{
    public async Task<bool> TryConsumeAsync(long userId, DateTime dayUtc, int limit, CancellationToken cancellationToken)
    {
        var day = dayUtc.Date;
        // The row may not exist yet; INSERT IGNORE makes a racing second insert a no-op. The guarded UPDATE is the atomic
        // check-and-count: it matches (and so counts) only while the day is under the limit.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT IGNORE INTO AssistantUsage (UserId, Day, Calls) VALUES ({userId}, {day}, 0)", cancellationToken);
        var counted = await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE AssistantUsage SET Calls = Calls + 1 WHERE UserId = {userId} AND Day = {day} AND Calls < {limit}",
            cancellationToken);
        return counted == 1;
    }
}
