using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CoreFoundry.Application.Common;
using CoreFoundry.Application.Data;
using CoreFoundry.Domain.Schema;
using CoreFoundry.Domain.SchemaEngine;
using Dapper;
using MySqlConnector;

namespace CoreFoundry.Infrastructure.Data;

/// <summary>Runs <see cref="DataSql"/> commands with Dapper on a <c>cf_engine</c> connection.</summary>
public sealed partial class MySqlDataRepository(string engineConnectionString) : IDataRepository
{
    public async Task<(IReadOnlyList<DataRow> Rows, long Total)> ListAsync(
        string databaseName, DataTable table, SortOrder sort, int skip, int take, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await Guard(table, [], async () =>
        {
            var total = await connection.ExecuteScalarAsync<long>(Command(DataSql.Count(databaseName, table), cancellationToken));
            var rows = await ReadAsync(connection, table, DataSql.Select(databaseName, table, sort, skip, take), cancellationToken);
            return ((IReadOnlyList<DataRow>)rows, total);
        });
    }

    public async Task<DataRow?> FindAsync(string databaseName, DataTable table, long id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await Guard(table, [], async () =>
            (await ReadAsync(connection, table, DataSql.SelectById(databaseName, table, id), cancellationToken)).SingleOrDefault());
    }

    public async Task<long> InsertAsync(
        string databaseName, DataTable table, IReadOnlyList<ColumnValue> values, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await Guard(table, values, async () =>
        {
            await connection.ExecuteAsync(Command(DataSql.Insert(databaseName, table, values), cancellationToken));
            // Same connection, so it is this insert's id.
            return await connection.ExecuteScalarAsync<long>(new CommandDefinition("SELECT LAST_INSERT_ID()", cancellationToken: cancellationToken));
        });
    }

    public async Task<bool> UpdateAsync(
        string databaseName, DataTable table, long id, IReadOnlyList<ColumnValue> values, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        // MySqlConnector reports rows matched (not changed), so an unchanged row still counts as found.
        return await Guard(table, values, async () =>
            await connection.ExecuteAsync(Command(DataSql.Update(databaseName, table, id, values), cancellationToken)) > 0);
    }

    public async Task<bool> DeleteAsync(string databaseName, DataTable table, long id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await Guard(table, [], async () =>
            await connection.ExecuteAsync(Command(DataSql.Delete(databaseName, table, id), cancellationToken)) > 0);
    }

    private async Task<MySqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new MySqlConnection(engineConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static CommandDefinition Command(DataCommand command, CancellationToken cancellationToken) =>
        new(command.Sql, new DynamicParameters(command.Parameters), cancellationToken: cancellationToken);

    private static async Task<List<DataRow>> ReadAsync(
        MySqlConnection connection, DataTable table, DataCommand command, CancellationToken cancellationToken)
    {
        await using var reader = await connection.ExecuteReaderAsync(Command(command, cancellationToken));
        var rows = new List<DataRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new DataRow { ["id"] = reader.GetInt64(0) };
            for (var i = 0; i < table.Columns.Count; i++)
            {
                row[table.Columns[i].Name] = ReadValue(reader, i + 1, table.Columns[i].Type);
            }

            rows.Add(row);
        }

        return rows;
    }

    /// <summary>MySQL value → JSON-ready value. Unknown (read-only) types become text.</summary>
    private static object? ReadValue(DbDataReader reader, int ordinal, ColumnType? type)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        return type?.DataType switch
        {
            DataType.Int => reader.GetInt32(ordinal),
            DataType.BigInt => reader.GetInt64(ordinal),
            DataType.Decimal => reader.GetFieldValue<MySqlDecimal>(ordinal).ToString(),
            DataType.Bool => reader.GetBoolean(ordinal),
            DataType.Varchar or DataType.Text => reader.GetString(ordinal),
            DataType.Date => reader.GetDateTime(ordinal).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            DataType.DateTime => reader.GetDateTime(ordinal).ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFF", CultureInfo.InvariantCulture),
            DataType.Uuid => reader.GetValue(ordinal) is Guid guid ? guid.ToString("D") : reader.GetString(ordinal),
            DataType.Json => ParseJson(reader.GetString(ordinal)),
            _ => reader.GetValue(ordinal) switch
            {
                byte[] bytes => Convert.ToBase64String(bytes),
                MySqlDecimal exact => exact.ToString(),
                DateTime dateTime => dateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFF", CultureInfo.InvariantCulture),
                var other => Convert.ToString(other, CultureInfo.InvariantCulture),
            },
        };
    }

    private static JsonElement ParseJson(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    /// <summary>Turns the MySQL errors a user can cause into 400/409s with a useful message.</summary>
    private static async Task<T> Guard<T>(DataTable table, IReadOnlyList<ColumnValue> values, Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (MySqlException ex) when (Translate(ex, table, values) is { } translated)
        {
            throw translated;
        }
    }

    internal static Exception? Translate(MySqlException ex, DataTable table, IReadOnlyList<ColumnValue> values)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return ex.Number switch
        {
            // Duplicate entry '978-0' for key 'books.uq_books_isbn'
            1062 => new ConflictException(DuplicateMessage(ex.Message, table)),
            // Column 'title' cannot be null
            1048 when QuotedName().Match(ex.Message) is { Success: true } match =>
                new ValidationFailedException(match.Groups[1].Value, "Can't be null."),
            // Data too long for column 'title' at row 1
            1406 when QuotedName().Match(ex.Message) is { Success: true } match =>
                new ValidationFailedException(match.Groups[1].Value, "Too long for the column."),
            // Cannot add or update a child row: a foreign key constraint fails (… FOREIGN KEY (`author_id`) REFERENCES `authors` (`id`) …)
            1452 when ForeignKey().Match(ex.Message) is { Success: true } match => new ValidationFailedException(
                match.Groups["column"].Value,
                $"No {match.Groups["target"].Value} row with id {values.FirstOrDefault(value => value.Column.Name == match.Groups["column"].Value)?.Value}."),
            // Cannot delete or update a parent row: a foreign key constraint fails (`cf_p_7`.`books`, CONSTRAINT … FOREIGN KEY (`author_id`) …)
            1451 when ReferencedBy().Match(ex.Message) is { Success: true } match => new ConflictException(
                $"Other rows reference this one: {match.Groups["table"].Value}.{match.Groups["column"].Value}. Delete or change those first."),
            // Table doesn't exist / unknown column: the database changed outside CoreFoundry.
            1146 or 1054 => new ConflictException(
                $"Table {table.Name} no longer matches the last apply (schema drift detected). Review a new plan."),
            _ => null,
        };
    }

    private static string DuplicateMessage(string message, DataTable table)
    {
        var match = Duplicate().Match(message);
        var key = match.Success ? match.Groups["key"].Value : "";
        var column = table.Columns.FirstOrDefault(column => column.IsUnique
            && (key == ConstraintNames.UniqueKey(table.Name, column.Name) || key == $"{table.Name}.{ConstraintNames.UniqueKey(table.Name, column.Name)}"));
        return column is null
            ? "A unique value already exists."
            : $"{column.Name} must be unique; {match.Groups["value"].Value} already exists.";
    }

    [GeneratedRegex("'([^']+)'", RegexOptions.CultureInvariant)]
    private static partial Regex QuotedName();

    [GeneratedRegex("^Duplicate entry '(?<value>.*)' for key '(?<key>[^']+)'$", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex Duplicate();

    [GeneratedRegex(@"FOREIGN KEY \(`(?<column>[^`]+)`\) REFERENCES `(?<target>[^`]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex ForeignKey();

    [GeneratedRegex(@"fails \(`[^`]+`\.`(?<table>[^`]+)`, CONSTRAINT `[^`]+` FOREIGN KEY \(`(?<column>[^`]+)`\)", RegexOptions.CultureInvariant)]
    private static partial Regex ReferencedBy();
}
