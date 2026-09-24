using CoreFoundry.Application.Schema;
using CoreFoundry.Domain.Schema;
using Microsoft.EntityFrameworkCore;

namespace CoreFoundry.Infrastructure.Persistence;

internal sealed class TableRepository(MetadataDbContext db) : ITableRepository
{
    public async Task<IReadOnlyList<ProjectTable>> ListAsync(long projectId, CancellationToken cancellationToken) =>
        await db.ProjectTables.Include(table => table.Columns)
            .Where(table => table.ProjectId == projectId)
            .OrderBy(table => table.Name)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

    public Task<ProjectTable?> FindAsync(long projectId, long tableId, CancellationToken cancellationToken) =>
        db.ProjectTables.Include(table => table.Columns)
            .SingleOrDefaultAsync(table => table.Id == tableId && table.ProjectId == projectId, cancellationToken);

    public Task<int> CountAsync(long projectId, CancellationToken cancellationToken) =>
        db.ProjectTables.CountAsync(table => table.ProjectId == projectId, cancellationToken);

    public async Task<IReadOnlyDictionary<long, string>> ListNamesAsync(long projectId, CancellationToken cancellationToken) =>
        await db.ProjectTables.Where(table => table.ProjectId == projectId)
            .ToDictionaryAsync(table => table.Id, table => table.Name, cancellationToken);

    public Task<bool> NameExistsAsync(long projectId, string name, long? exceptTableId, CancellationToken cancellationToken) =>
        db.ProjectTables.AnyAsync(
            table => table.ProjectId == projectId && table.Name == name && table.Id != exceptTableId,
            cancellationToken);

    public void Add(ProjectTable table) => db.ProjectTables.Add(table);

    public void Remove(ProjectTable table) => db.ProjectTables.Remove(table);
}
