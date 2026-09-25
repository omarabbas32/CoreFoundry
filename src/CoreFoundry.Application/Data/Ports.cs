namespace CoreFoundry.Application.Data;

/// <summary>
/// A row ready for JSON: <c>id</c> first, then the table's columns. Values are <see cref="long"/>,
/// <see cref="int"/>, <see cref="bool"/>, <see cref="string"/> (decimals as exact text, dates and times
/// as ISO-8601 without offset, UUIDs), a <see cref="System.Text.Json.JsonElement"/> for Json columns, or null.
/// </summary>
public sealed class DataRow : Dictionary<string, object?>
{
    public DataRow() : base(StringComparer.Ordinal) { }

    public long Id => (long)this["id"]!;
}

/// <summary>Reads and writes rows of a project's applied tables (as <c>cf_engine</c>).</summary>
/// <remarks>
/// MySQL errors become application errors: duplicate unique value, row referenced by others and
/// table missing (drift) are <see cref="Common.ConflictException"/>; NULL in a NOT NULL column,
/// too-long value and a reference to a missing row are <see cref="Common.ValidationFailedException"/>
/// on that field. Anything else is left to become a 500.
/// </remarks>
public interface IDataRepository
{
    Task<(IReadOnlyList<DataRow> Rows, long Total)> ListAsync(
        string databaseName, DataTable table, SortOrder sort, int skip, int take, CancellationToken cancellationToken);

    Task<DataRow?> FindAsync(string databaseName, DataTable table, long id, CancellationToken cancellationToken);

    /// <returns>The new row's id.</returns>
    Task<long> InsertAsync(string databaseName, DataTable table, IReadOnlyList<ColumnValue> values, CancellationToken cancellationToken);

    /// <returns>False if no row has that id.</returns>
    Task<bool> UpdateAsync(string databaseName, DataTable table, long id, IReadOnlyList<ColumnValue> values, CancellationToken cancellationToken);

    /// <returns>False if no row has that id.</returns>
    Task<bool> DeleteAsync(string databaseName, DataTable table, long id, CancellationToken cancellationToken);
}
