using CoreFoundry.Application.SchemaEngine;
using CoreFoundry.Domain.Schema;
using CoreFoundry.Domain.SchemaEngine;

namespace CoreFoundry.Application.Schema;

/// <summary>Where a table or column stands relative to the real database.</summary>
public enum SchemaObjectState
{
    /// <summary>Never applied.</summary>
    New,

    /// <summary>Applied and unchanged since the last apply.</summary>
    Applied,

    /// <summary>Applied, but the draft differs from what the last apply created (rename, type, …).</summary>
    Changed,

    /// <summary>Applied, and will be dropped at the next apply unless restored.</summary>
    PendingDrop,
}

/// <summary>A column as the user typed it; validated by the Domain rules.</summary>
/// <param name="ReferencesTableId">A table of the same project whose <c>id</c> this column references, or null.</param>
public sealed record ColumnInput(
    string Name,
    DataType DataType,
    int? Length,
    int? Precision,
    int? Scale,
    bool IsNullable,
    bool IsUnique,
    string? DefaultValue,
    long? ReferencesTableId = null,
    ReferenceAction? OnDelete = null);

public sealed record ColumnDto(
    long Id,
    string Name,
    DataType DataType,
    int? Length,
    int? Precision,
    int? Scale,
    bool IsNullable,
    bool IsUnique,
    string? DefaultValue,
    long? ReferencesTableId,
    string? ReferencesTableName,
    ReferenceAction? OnDelete,
    int OrdinalPosition,
    SchemaObjectState State);

public sealed record TableSummaryDto(
    long Id,
    string Name,
    SchemaObjectState State,
    int ColumnCount,
    int Version,
    DateTime UpdatedAt);

/// <param name="Version">Send this back with the next change; a stale value gets 409.</param>
/// <param name="Columns">Ordered by position, including columns pending drop.</param>
public sealed record TableDto(
    long Id,
    string Name,
    SchemaObjectState State,
    int Version,
    IReadOnlyList<ColumnDto> Columns,
    DateTime CreatedAt,
    DateTime UpdatedAt)
{
    public static TableDto From(ProjectTable table, SchemaContext context)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(context);
        return new(
            table.Id,
            table.Name,
            context.StateOf(table),
            table.Version,
            [.. table.Columns.Select(column => ColumnFrom(table, column, context))],
            table.CreatedAt,
            table.UpdatedAt);
    }

    public static TableSummaryDto SummaryFrom(ProjectTable table, SchemaContext context)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(context);
        return new(table.Id, table.Name, context.StateOf(table), table.Columns.Count, table.Version, table.UpdatedAt);
    }

    private static ColumnDto ColumnFrom(ProjectTable table, ProjectColumn column, SchemaContext context) => new(
        column.Id,
        column.Name,
        column.DataType,
        column.Length,
        column.Precision,
        column.Scale,
        column.IsNullable,
        column.IsUnique,
        column.DefaultValue,
        column.ReferencesTableId,
        column.ReferencesTableId is long target ? context.Tables.GetValueOrDefault(target)?.Name : null,
        column.OnDelete,
        column.OrdinalPosition,
        context.StateOf(table, column));
}

/// <summary>A project table's current and applied names.</summary>
public sealed record TableName(string Name, string? AppliedName);

/// <summary>
/// What's needed to tell New / Applied / Changed / PendingDrop apart: the project's table names (for
/// references) and the snapshot the last apply took of the real schema.
/// </summary>
public sealed record SchemaContext(IReadOnlyDictionary<long, TableName> Tables, SchemaSnapshot Snapshot)
{
    public SchemaObjectState StateOf(ProjectTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (table.PendingDrop)
        {
            return SchemaObjectState.PendingDrop;
        }

        if (!table.IsApplied)
        {
            return SchemaObjectState.New;
        }

        return table.Name != table.AppliedName || table.Columns.Any(column => StateOf(table, column) != SchemaObjectState.Applied)
            ? SchemaObjectState.Changed
            : SchemaObjectState.Applied;
    }

    public SchemaObjectState StateOf(ProjectTable table, ProjectColumn column)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(column);
        if (table.IsColumnPendingDrop(column))
        {
            return SchemaObjectState.PendingDrop;
        }

        if (!column.IsApplied)
        {
            return SchemaObjectState.New;
        }

        // Without a snapshot of this table (e.g. applied before snapshots existed) there's nothing to compare with.
        if (Snapshot.FindTable(table.AppliedName!)?.FindColumn(column.AppliedName!) is not { } applied)
        {
            return SchemaObjectState.Applied;
        }

        return column.Name != column.AppliedName || applied != Expected(column)
            ? SchemaObjectState.Changed
            : SchemaObjectState.Applied;
    }

    /// <summary>What the snapshot would say about this column if its draft were applied as it is now.</summary>
    private ColumnSnapshot Expected(ProjectColumn column)
    {
        var type = new ColumnType(column.DataType, column.Length, column.Precision, column.Scale);
        var target = column.ReferencesTableId is long id ? Tables.GetValueOrDefault(id) : null;
        return new ColumnSnapshot(
            column.AppliedName!,
            type.ToString(),
            column.IsNullable,
            column.IsUnique,
            type.Normalize(column.Default)?.Canonical,
            target is null ? null : target.AppliedName ?? target.Name,
            column.OnDelete?.ToString());
    }
}
