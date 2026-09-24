using CoreFoundry.Domain.Schema;
using CoreFoundry.Domain.SchemaEngine;

namespace CoreFoundry.Application.SchemaEngine;

/// <summary>Maps a project's draft tables (metadata) to the desired <see cref="SchemaModel"/>.</summary>
public static class DraftSchema
{
    /// <param name="tables">All tables of one project, with their columns, including pending drops.</param>
    public static SchemaModel From(IEnumerable<ProjectTable> tables)
    {
        ArgumentNullException.ThrowIfNull(tables);
        var list = tables.OrderBy(table => table.Name, StringComparer.Ordinal).ToList();
        var names = list.ToDictionary(table => table.Id, table => table.Name);

        return new SchemaModel([.. list.Select(table => new TableModel(
            table.Name,
            [.. table.Columns.Select(column => Column(column, names))],
            table.Id,
            table.AppliedName,
            table.PendingDrop))]);
    }

    private static ColumnModel Column(ProjectColumn column, IReadOnlyDictionary<long, string> tableNames) => new(
        column.Name,
        new ColumnType(column.DataType, column.Length, column.Precision, column.Scale),
        column.IsNullable,
        column.IsUnique,
        column.Default,
        column.ReferencesTableId is long target
            ? new ForeignKeyModel(tableNames.GetValueOrDefault(target, string.Empty), column.OnDelete ?? ReferenceAction.Restrict, target)
            : null,
        column.Id,
        column.AppliedName,
        column.PendingDrop);
}
