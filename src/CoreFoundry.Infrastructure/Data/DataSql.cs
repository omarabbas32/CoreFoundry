using CoreFoundry.Application.Data;
using CoreFoundry.Domain.SchemaEngine;
using CoreFoundry.Infrastructure.Engine;

namespace CoreFoundry.Infrastructure.Data;

/// <summary>A statement and its parameter values.</summary>
public sealed record DataCommand(string Sql, IReadOnlyDictionary<string, object?> Parameters);

/// <summary>
/// Builds the Data API's SQL. Pure. Two defenses for two problems: names come only from the
/// resolved <see cref="DataTable"/> (never from the request) and still go through
/// <see cref="MySqlSqlRenderer.Quote"/>; values are always parameters named <c>@p_&lt;ordinal&gt;</c>.
/// </summary>
public static class DataSql
{
    public static DataCommand Select(string database, DataTable table, SortOrder sort, int skip, int take)
    {
        ArgumentNullException.ThrowIfNull(sort);
        var direction = sort.Descending ? " DESC" : "";
        var order = sort.Column is null
            ? $"`id`{direction}"
            : $"{Name(sort.Column.Name)}{direction}, `id`"; // id breaks ties, so pages are stable
        return new(
            $"SELECT {Columns(table)} FROM {Table(database, table)} ORDER BY {order} LIMIT @take OFFSET @skip",
            new Dictionary<string, object?> { ["take"] = take, ["skip"] = skip });
    }

    public static DataCommand Count(string database, DataTable table) =>
        new($"SELECT COUNT(*) FROM {Table(database, table)}", new Dictionary<string, object?>());

    public static DataCommand SelectById(string database, DataTable table, long id) =>
        new($"SELECT {Columns(table)} FROM {Table(database, table)} WHERE `id` = @id", new Dictionary<string, object?> { ["id"] = id });

    /// <summary>Columns left out get NULL or their default. Followed by <c>LAST_INSERT_ID()</c> on the same connection.</summary>
    public static DataCommand Insert(string database, DataTable table, IReadOnlyList<ColumnValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var parameters = new Dictionary<string, object?>();
        var names = values.Select(value => Name(value.Column.Name));
        var placeholders = values.Select((value, index) => Placeholder(value, index, parameters));
        return new($"INSERT INTO {Table(database, table)} ({string.Join(", ", names)}) VALUES ({string.Join(", ", placeholders)})", parameters);
    }

    /// <summary>Full replace of the given columns. Matches (MySQL's "found rows") 0 when the id doesn't exist.</summary>
    public static DataCommand Update(string database, DataTable table, long id, IReadOnlyList<ColumnValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var parameters = new Dictionary<string, object?> { ["id"] = id };
        var assignments = values.Select((value, index) => $"{Name(value.Column.Name)} = {Placeholder(value, index, parameters)}").ToList();
        if (assignments.Count == 0)
        {
            assignments.Add("`id` = `id`"); // nothing writable: still report whether the row exists
        }

        return new($"UPDATE {Table(database, table)} SET {string.Join(", ", assignments)} WHERE `id` = @id", parameters);
    }

    public static DataCommand Delete(string database, DataTable table, long id) =>
        new($"DELETE FROM {Table(database, table)} WHERE `id` = @id", new Dictionary<string, object?> { ["id"] = id });

    private static string Placeholder(ColumnValue value, int index, Dictionary<string, object?> parameters)
    {
        if (value.UseDefault)
        {
            return "DEFAULT";
        }

        var name = $"p_{index}";
        parameters[name] = value.Value switch
        {
            DateOnly date => date.ToDateTime(TimeOnly.MinValue),
            var other => other,
        };
        return "@" + name;
    }

    private static string Columns(DataTable table) =>
        string.Join(", ", ["`id`", .. table.Columns.Select(column => Name(column.Name))]);

    private static string Table(string database, DataTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        return $"{Name(database)}.{Name(table.Name)}";
    }

    /// <exception cref="ArgumentException">Not a safe identifier (defense in depth).</exception>
    private static string Name(string name) => MySqlSqlRenderer.Quote(Identifier.Of(name));
}
