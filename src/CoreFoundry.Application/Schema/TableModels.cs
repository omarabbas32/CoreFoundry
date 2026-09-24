using CoreFoundry.Domain.Schema;

namespace CoreFoundry.Application.Schema;

/// <summary>Where a table or column stands relative to the real database.</summary>
public enum SchemaObjectState
{
    /// <summary>Never applied.</summary>
    New,

    /// <summary>Applied and unchanged. (<c>Changed</c> arrives with the M3 snapshot comparison.)</summary>
    Applied,

    /// <summary>Applied, and will be dropped at the next apply unless restored.</summary>
    PendingDrop,
}

/// <summary>A column as the user typed it; validated by the Domain rules.</summary>
public sealed record ColumnInput(
    string Name,
    DataType DataType,
    int? Length,
    int? Precision,
    int? Scale,
    bool IsNullable,
    bool IsUnique,
    string? DefaultValue);

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
    public static TableDto From(ProjectTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        return new(
            table.Id,
            table.Name,
            StateOf(table),
            table.Version,
            [.. table.Columns.Select(column => ColumnFrom(table, column))],
            table.CreatedAt,
            table.UpdatedAt);
    }

    public static TableSummaryDto SummaryFrom(ProjectTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        return new(table.Id, table.Name, StateOf(table), table.Columns.Count, table.Version, table.UpdatedAt);
    }

    private static ColumnDto ColumnFrom(ProjectTable table, ProjectColumn column) => new(
        column.Id,
        column.Name,
        column.DataType,
        column.Length,
        column.Precision,
        column.Scale,
        column.IsNullable,
        column.IsUnique,
        column.DefaultValue,
        column.OrdinalPosition,
        table.IsColumnPendingDrop(column) ? SchemaObjectState.PendingDrop
            : column.IsApplied ? SchemaObjectState.Applied
            : SchemaObjectState.New);

    private static SchemaObjectState StateOf(ProjectTable table) =>
        table.PendingDrop ? SchemaObjectState.PendingDrop
            : table.IsApplied ? SchemaObjectState.Applied
            : SchemaObjectState.New;
}
