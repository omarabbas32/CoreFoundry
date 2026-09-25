using CoreFoundry.Application.SchemaEngine;
using CoreFoundry.Domain.SchemaEngine;

namespace CoreFoundry.Application.Data;

/// <summary>
/// The tables the Data API serves: the schema as it was right after the last successful apply
/// (<see cref="SchemaSnapshot"/>), never the draft. A table or column that exists only in the
/// draft doesn't exist here.
/// </summary>
/// <param name="SchemaVersion">The project's schema version this was read for.</param>
public sealed record DataSchema(int SchemaVersion, IReadOnlyList<DataTable> Tables)
{
    public static DataSchema Empty(int schemaVersion) => new(schemaVersion, []);

    public static DataSchema From(SchemaSnapshot snapshot, int schemaVersion)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new(schemaVersion, [.. snapshot.Tables.Select(table => new DataTable(
            table.Name,
            [.. table.Columns.Select(column => new DataColumn(
                column.Name,
                ColumnType.TryParse(column.Type, out var type) ? type : null,
                column.Type,
                column.IsNullable,
                column.IsUnique,
                column.Default,
                column.References))]))]);
    }

    /// <summary>Exact, case-sensitive match on the physical name (names are stored lower-case).</summary>
    public DataTable? FindTable(string name) => Tables.FirstOrDefault(table => table.Name == name);
}

/// <param name="Columns">The user columns in database order; the system <c>id</c> isn't listed (every table has it).</param>
public sealed record DataTable(string Name, IReadOnlyList<DataColumn> Columns)
{
    public DataColumn? FindColumn(string name) => Columns.FirstOrDefault(column => column.Name == name);
}

/// <param name="Type">Null when the column has a type CoreFoundry doesn't create (changed outside it): such a column is read-only.</param>
/// <param name="RawType">The type as the snapshot stores it (for display).</param>
/// <param name="Default">The default's canonical text, or null.</param>
/// <param name="References">The referenced table's physical name, or null.</param>
public sealed record DataColumn(
    string Name, ColumnType? Type, string RawType, bool IsNullable, bool IsUnique, string? Default, string? References)
{
    public bool IsWritable => Type is not null;

    /// <summary>An insert may leave it out: MySQL fills in NULL or the default.</summary>
    public bool IsOptionalOnInsert => IsNullable || Default is not null;
}

/// <summary>Serves the <see cref="DataSchema"/> of a project's schema version.</summary>
public interface ISnapshotProvider
{
    /// <summary>
    /// The schema after the last successful apply. <paramref name="schemaVersion"/> is the project's
    /// current version: every apply bumps it, so a cached schema is never served after a new apply.
    /// </summary>
    Task<DataSchema> GetAsync(long projectId, int schemaVersion, CancellationToken cancellationToken);
}
