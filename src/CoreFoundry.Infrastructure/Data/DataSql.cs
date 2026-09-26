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
    /// <param name="search">Optional: see <see cref="SearchFilter"/>.</param>
    public static DataCommand Select(string database, DataTable table, SortOrder sort, int skip, int take, string? search = null)
    {
        ArgumentNullException.ThrowIfNull(sort);
        var direction = sort.Descending ? " DESC" : "";
        var order = sort.Column is null
            ? $"`id`{direction}"
            : $"{Name(sort.Column.Name)}{direction}, `id`"; // id breaks ties, so pages are stable
        var parameters = new Dictionary<string, object?> { ["take"] = take, ["skip"] = skip };
        var where = SearchFilter(table, search, parameters);
        return new($"SELECT {Columns(table)} FROM {Table(database, table)}{where} ORDER BY {order} LIMIT @take OFFSET @skip", parameters);
    }

    /// <param name="search">Optional: counts only the rows <see cref="SearchFilter"/> matches, for the same page count.</param>
    public static DataCommand Count(string database, DataTable table, string? search = null)
    {
        var parameters = new Dictionary<string, object?>();
        return new($"SELECT COUNT(*) FROM {Table(database, table)}{SearchFilter(table, search, parameters)}", parameters);
    }

    /// <summary>
    /// A <c>WHERE</c> for a data-browser search: any text column (Varchar, Text, Uuid) containing the text
    /// (<c>LIKE</c>, with the user's <c>%</c>, <c>_</c> and <c>\</c> escaped), or an exact id. Empty when there's no search;
    /// <c>WHERE FALSE</c> when nothing can match (no text columns and not a number).
    /// </summary>
    public static string SearchFilter(DataTable table, string? search, Dictionary<string, object?> parameters)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(parameters);
        if (string.IsNullOrEmpty(search))
        {
            return "";
        }

        var conditions = new List<string>();
        var text = table.Columns
            .Where(column => column.Type?.DataType is Domain.Schema.DataType.Varchar or Domain.Schema.DataType.Text or Domain.Schema.DataType.Uuid)
            .ToList();
        if (text.Count > 0)
        {
            parameters["search"] = Contains(search);
            conditions.AddRange(text.Select(column => $"{Name(column.Name)} LIKE @search"));
        }

        if (long.TryParse(search, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id))
        {
            parameters["searchId"] = id;
            conditions.Add("`id` = @searchId");
        }

        return conditions.Count == 0 ? " WHERE FALSE" : $" WHERE ({string.Join(" OR ", conditions)})";
    }

    /// <summary>A <c>LIKE</c> pattern for "contains", with the user's wildcards taken literally.</summary>
    private static string Contains(string search) =>
        "%" + search.Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal).Replace("_", @"\_", StringComparison.Ordinal) + "%";

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

    /// <summary>
    /// Rows for a reference picker: <c>id</c> and the label column (or NULL), optionally filtered by a
    /// search on the label (<c>LIKE</c>, with the user's <c>%</c>, <c>_</c> and <c>\</c> escaped) or an exact id.
    /// </summary>
    public static DataCommand Lookup(string database, DataTable table, DataColumn? label, string? search, int take)
    {
        var parameters = new Dictionary<string, object?> { ["take"] = take };
        var labelSql = label is null ? "NULL" : Name(label.Name);
        var where = "";
        if (!string.IsNullOrEmpty(search))
        {
            var conditions = new List<string>();
            if (label is not null)
            {
                parameters["search"] = Contains(search);
                conditions.Add($"{labelSql} LIKE @search");
            }

            if (long.TryParse(search, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id))
            {
                parameters["id"] = id;
                conditions.Add("`id` = @id");
            }

            where = conditions.Count == 0 ? " WHERE FALSE" : $" WHERE {string.Join(" OR ", conditions)}";
        }

        var order = label is null ? "`id`" : $"{labelSql}, `id`";
        return new($"SELECT `id`, {labelSql} FROM {Table(database, table)}{where} ORDER BY {order} LIMIT @take", parameters);
    }

    /// <summary>
    /// The labels of exactly these rows (for showing references by name in the data browser): <c>id</c> and the label
    /// column, one parameter per id. Ids that don't exist are simply absent.
    /// </summary>
    public static DataCommand Labels(string database, DataTable table, DataColumn? label, IReadOnlyList<long> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var parameters = new Dictionary<string, object?>();
        var placeholders = ids.Select((id, index) =>
        {
            parameters[$"id_{index}"] = id;
            return $"@id_{index}";
        }).ToList();
        var labelSql = label is null ? "NULL" : Name(label.Name);
        var where = placeholders.Count == 0 ? "FALSE" : $"`id` IN ({string.Join(", ", placeholders)})";
        return new($"SELECT `id`, {labelSql} FROM {Table(database, table)} WHERE {where}", parameters);
    }

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
