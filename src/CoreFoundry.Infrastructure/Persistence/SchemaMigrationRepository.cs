using CoreFoundry.Application.SchemaEngine;
using CoreFoundry.Domain.Schema;
using Microsoft.EntityFrameworkCore;

namespace CoreFoundry.Infrastructure.Persistence;

internal sealed class SchemaMigrationRepository(MetadataDbContext db) : ISchemaMigrationRepository
{
    public void Add(SchemaMigration migration) => db.SchemaMigrations.Add(migration);

    public Task<SchemaMigration?> FindAsync(long projectId, long migrationId, CancellationToken cancellationToken) =>
        db.SchemaMigrations.SingleOrDefaultAsync(
            migration => migration.Id == migrationId && migration.ProjectId == projectId, cancellationToken);

    public async Task<IReadOnlyList<SchemaMigration>> ListAsync(long projectId, int skip, int take, CancellationToken cancellationToken) =>
        await db.SchemaMigrations.AsNoTracking()
            .Where(migration => migration.ProjectId == projectId)
            .OrderByDescending(migration => migration.Version).ThenByDescending(migration => migration.Id)
            .Skip(skip).Take(take)
            .ToListAsync(cancellationToken);

    public Task<int> CountAsync(long projectId, CancellationToken cancellationToken) =>
        db.SchemaMigrations.CountAsync(migration => migration.ProjectId == projectId, cancellationToken);

    public Task<SchemaMigration?> LatestAppliedAsync(long projectId, CancellationToken cancellationToken) =>
        db.SchemaMigrations.AsNoTracking()
            .Where(migration => migration.ProjectId == projectId && migration.Status == MigrationStatus.Applied)
            .OrderByDescending(migration => migration.Version)
            .FirstOrDefaultAsync(cancellationToken);
}
