using CoreFoundry.Application.Common;
using CoreFoundry.Application.Schema;

namespace CoreFoundry.Application.Templates;

/// <summary>A column to add to a table the project already has (by its draft name).</summary>
public sealed record ExistingTableColumn(string Table, TemplateColumn Column);

/// <summary>
/// Writes template-shaped tables into a project's draft schema: a schema template (M7) or a confirmed
/// AI proposal (M10). Everything goes through <see cref="TableService"/>, so it's checked exactly like a
/// designer edit. All or nothing: if one step fails, what was already written is undone and the error rethrown.
/// </summary>
public sealed class DraftSchemaWriter(ITableRepository tables, TableService tableService, IUnitOfWork unitOfWork)
{
    /// <param name="newTables">
    /// Created in order. A column may reference an existing table, a new one (earlier, later or itself) by name;
    /// references to tables not created yet are added once they exist.
    /// </param>
    /// <param name="newColumns">Added to existing tables after the new tables exist, so they may reference them.</param>
    public async Task WriteAsync(
        long projectId,
        IReadOnlyList<TemplateTable> newTables,
        IReadOnlyList<ExistingTableColumn> newColumns,
        CancellationToken cancellationToken)
    {
        // Draft names → ids: existing tables first, then each new table as it is created.
        var ids = (await tables.ListAsync(projectId, cancellationToken))
            .Where(table => !table.PendingDrop)
            .ToDictionary(table => table.Name, table => table.Id, StringComparer.OrdinalIgnoreCase);
        var created = new List<long>();
        var addedColumns = new List<(long TableId, long ColumnId)>();
        try
        {
            // Columns that reference a table not created yet (itself, or a later one) are added afterwards.
            var deferred = new List<(string Table, TemplateColumn Column)>();
            foreach (var table in newTables)
            {
                var now = table.Columns.Where(column => column.References is null || ids.ContainsKey(column.References)).ToList();
                deferred.AddRange(table.Columns.Except(now).Select(column => (table.Name, column)));
                var dto = await tableService.CreateAsync(projectId, table.Name, [.. now.Select(column => Input(column, ids))], cancellationToken);
                created.Add(dto.Id);
                ids[table.Name] = dto.Id;
                dto = await tableService.SetAccessAsync(projectId, dto.Id, dto.Version, table.Read, table.Write, cancellationToken);
                if (!table.Realtime)
                {
                    await tableService.SetRealtimeAsync(projectId, dto.Id, dto.Version, false, cancellationToken);
                }
            }

            foreach (var (table, column) in deferred)
            {
                await AddColumnAsync(ids[table], column, track: false);
            }

            foreach (var (table, column) in newColumns)
            {
                if (!ids.TryGetValue(table, out var tableId))
                {
                    throw new NotFoundException($"There is no table named {table} to add {column.Name} to.");
                }

                await AddColumnAsync(tableId, column, track: true);
            }
        }
        catch
        {
            await UndoAsync(projectId, created, addedColumns);
            throw;
        }

        async Task AddColumnAsync(long tableId, TemplateColumn column, bool track)
        {
            var current = await tableService.GetAsync(projectId, tableId, cancellationToken);
            var before = current.Columns.Select(existing => existing.Id).ToHashSet();
            var after = await tableService.AddColumnAsync(projectId, tableId, current.Version, Input(column, ids), cancellationToken);
            if (track)
            {
                addedColumns.Add((tableId, after.Columns.Single(added => !before.Contains(added.Id)).Id));
            }
        }
    }

    /// <summary>New tables are removed outright; columns added to existing tables are never applied, so deleting removes them.</summary>
    private async Task UndoAsync(long projectId, List<long> created, List<(long TableId, long ColumnId)> addedColumns)
    {
        foreach (var (tableId, columnId) in Enumerable.Reverse(addedColumns))
        {
            if (await tables.FindAsync(projectId, tableId, CancellationToken.None) is { } table)
            {
                table.DeleteColumn(columnId);
            }
        }

        foreach (var id in Enumerable.Reverse(created))
        {
            if (await tables.FindAsync(projectId, id, CancellationToken.None) is { } table)
            {
                tables.Remove(table);
            }
        }

        await unitOfWork.SaveChangesAsync(CancellationToken.None);
    }

    private static ColumnInput Input(TemplateColumn column, Dictionary<string, long> tableIds) => new(
        column.Name,
        column.Type,
        column.Length,
        column.Precision,
        column.Scale,
        column.Nullable,
        column.Unique,
        column.Default,
        column.References is { } target ? tableIds[target] : null,
        column.OnDelete);
}
