using System.Globalization;
using System.Text.RegularExpressions;
using CoreFoundry.Application.SchemaEngine;
using CoreFoundry.Domain.Schema;
using CoreFoundry.Domain.SchemaEngine;
using MySqlConnector;

namespace CoreFoundry.Infrastructure.Engine;

/// <summary>
/// Reads a project database's real schema from <c>INFORMATION_SCHEMA</c> as <c>cf_engine</c> and maps
/// it back to the model the differ compares with. Types CoreFoundry doesn't create are kept as
/// <see cref="ColumnModel.RawType"/> (with a null <see cref="ColumnModel.Type"/>) so they show up as changes.
/// </summary>
public sealed partial class MySqlSchemaIntrospector(string engineConnectionString) : ISchemaIntrospector
{
    public async Task<SchemaModel> ReadAsync(string databaseName, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(engineConnectionString);
        await connection.OpenAsync(cancellationToken);
        return await ReadAsync(connection, databaseName, cancellationToken);
    }

    public async Task<long> CountNullsAsync(string databaseName, string table, string column, CancellationToken cancellationToken)
    {
        var sql = $"SELECT COUNT(*) FROM {Name(databaseName)}.{Name(table)} WHERE {Name(column)} IS NULL";
        return Convert.ToInt64(await ScalarAsync(sql, cancellationToken), CultureInfo.InvariantCulture);
    }

    public async Task<bool> HasRowsAsync(string databaseName, string table, CancellationToken cancellationToken)
    {
        var sql = $"SELECT EXISTS (SELECT 1 FROM {Name(databaseName)}.{Name(table)})";
        return Convert.ToInt64(await ScalarAsync(sql, cancellationToken), CultureInfo.InvariantCulture) == 1;
    }

    /// <summary>Reads on an already open connection (the apply re-reads under its lock on its own session).</summary>
    public static async Task<SchemaModel> ReadAsync(MySqlConnection connection, string databaseName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var tables = new SortedDictionary<string, List<RawColumn>>(StringComparer.Ordinal);

        await using (var command = Command(connection, databaseName,
            "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = @db AND TABLE_TYPE = 'BASE TABLE'"))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                tables[reader.GetString(0)] = [];
            }
        }

        await using (var command = Command(connection, databaseName,
            """
            SELECT TABLE_NAME, COLUMN_NAME, COLUMN_TYPE, IS_NULLABLE, COLUMN_DEFAULT, EXTRA
              FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = @db ORDER BY TABLE_NAME, ORDINAL_POSITION
            """))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (tables.TryGetValue(reader.GetString(0), out var columns))
                {
                    columns.Add(new RawColumn(
                        reader.GetString(1),
                        reader.GetString(2).ToLowerInvariant(),
                        reader.GetString(3) == "YES",
                        reader.IsDBNull(4) ? null : reader.GetString(4),
                        reader.GetString(5).ToLowerInvariant()));
                }
            }
        }

        var uniques = await ReadUniqueKeysAsync(connection, databaseName, cancellationToken);
        var references = await ReadForeignKeysAsync(connection, databaseName, cancellationToken);

        return new SchemaModel([.. tables.Select(table => new TableModel(
            table.Key,
            [.. table.Value
                .Where(column => !IsSystemId(column))
                .Select(column => ToModel(column, uniques.GetValueOrDefault((table.Key, column.Name)), references.GetValueOrDefault((table.Key, column.Name))))]))]);
    }

    /// <summary>Single-column unique indexes (other than the primary key), by (table, column).</summary>
    private static async Task<Dictionary<(string, string), string>> ReadUniqueKeysAsync(
        MySqlConnection connection, string databaseName, CancellationToken cancellationToken)
    {
        var columnsByIndex = new Dictionary<(string Table, string Index), List<string>>();
        await using (var command = Command(connection, databaseName,
            """
            SELECT TABLE_NAME, INDEX_NAME, COLUMN_NAME FROM INFORMATION_SCHEMA.STATISTICS
             WHERE TABLE_SCHEMA = @db AND NON_UNIQUE = 0 AND INDEX_NAME <> 'PRIMARY'
             ORDER BY TABLE_NAME, INDEX_NAME, SEQ_IN_INDEX
            """))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var key = (reader.GetString(0), reader.GetString(1));
                if (!columnsByIndex.TryGetValue(key, out var columns))
                {
                    columnsByIndex[key] = columns = [];
                }

                columns.Add(reader.GetString(2));
            }
        }

        var result = new Dictionary<(string, string), string>();
        foreach (var ((table, index), columns) in columnsByIndex.OrderBy(pair => pair.Key.Index, StringComparer.Ordinal))
        {
            if (columns.Count == 1)
            {
                result.TryAdd((table, columns[0]), index);
            }
        }

        return result;
    }

    /// <summary>Single-column foreign keys to another table's <c>id</c>, by (table, column).</summary>
    private static async Task<Dictionary<(string, string), ForeignKeyModel>> ReadForeignKeysAsync(
        MySqlConnection connection, string databaseName, CancellationToken cancellationToken)
    {
        var result = new Dictionary<(string, string), ForeignKeyModel>();
        await using var command = Command(connection, databaseName,
            """
            SELECT k.TABLE_NAME, k.COLUMN_NAME, k.CONSTRAINT_NAME, k.REFERENCED_TABLE_NAME, k.REFERENCED_COLUMN_NAME, r.DELETE_RULE
              FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE k
              JOIN INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS r
                ON r.CONSTRAINT_SCHEMA = k.CONSTRAINT_SCHEMA AND r.CONSTRAINT_NAME = k.CONSTRAINT_NAME AND r.TABLE_NAME = k.TABLE_NAME
             WHERE k.TABLE_SCHEMA = @db AND k.REFERENCED_TABLE_NAME IS NOT NULL
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var action = reader.GetString(5) switch
            {
                "CASCADE" => ReferenceAction.Cascade,
                "SET NULL" => ReferenceAction.SetNull,
                "RESTRICT" or "NO ACTION" => (ReferenceAction?)ReferenceAction.Restrict,
                _ => null,
            };
            if (reader.GetString(4) == "id" && action is { } onDelete)
            {
                result[(reader.GetString(0), reader.GetString(1))] =
                    new ForeignKeyModel(reader.GetString(3), onDelete, ConstraintName: reader.GetString(2));
            }
        }

        return result;
    }

    private static bool IsSystemId(RawColumn column) =>
        column is { Name: "id", ColumnType: "bigint", IsNullable: false } && column.Extra.Contains("auto_increment", StringComparison.Ordinal);

    private static ColumnModel ToModel(RawColumn column, string? uniqueIndex, ForeignKeyModel? reference)
    {
        var type = TypeOf(column.ColumnType);
        return new ColumnModel(
            column.Name,
            type,
            column.IsNullable,
            uniqueIndex is not null,
            type is null ? null : DefaultOf(type, column.Default, column.Extra),
            reference,
            RawType: column.ColumnType,
            UniqueIndexName: uniqueIndex);
    }

    /// <summary>Maps <c>COLUMN_TYPE</c> back to the types the renderer creates, or null for anything else.</summary>
    internal static ColumnType? TypeOf(string columnType)
    {
        switch (columnType)
        {
            case "int": return new ColumnType(DataType.Int);
            case "bigint": return new ColumnType(DataType.BigInt);
            case "tinyint(1)": return new ColumnType(DataType.Bool);
            case "text": return new ColumnType(DataType.Text);
            case "datetime(6)": return new ColumnType(DataType.DateTime);
            case "date": return new ColumnType(DataType.Date);
            case "json": return new ColumnType(DataType.Json);
            case "char(36)": return new ColumnType(DataType.Uuid);
        }

        if (VarcharPattern().Match(columnType) is { Success: true } varchar)
        {
            return new ColumnType(DataType.Varchar, int.Parse(varchar.Groups[1].Value, CultureInfo.InvariantCulture));
        }

        return DecimalPattern().Match(columnType) is { Success: true } number
            ? new ColumnType(DataType.Decimal, null,
                byte.Parse(number.Groups[1].Value, CultureInfo.InvariantCulture),
                byte.Parse(number.Groups[2].Value, CultureInfo.InvariantCulture))
            : null;
    }

    /// <summary>
    /// Maps <c>COLUMN_DEFAULT</c> (+ <c>EXTRA</c>) back to a typed default. MySQL 8 reports literals
    /// unquoted and function defaults as the expression with <c>DEFAULT_GENERATED</c>. Anything that
    /// doesn't parse is kept as text, so it differs from the draft and shows up in the plan.
    /// </summary>
    internal static ColumnDefault? DefaultOf(ColumnType type, string? raw, string extra)
    {
        if (raw is null)
        {
            return null;
        }

        var generated = extra.Contains("default_generated", StringComparison.Ordinal);
        return type.DataType switch
        {
            DataType.Int or DataType.BigInt when long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number) =>
                new ColumnDefault.IntegerValue(number),
            DataType.Decimal => new ColumnDefault.DecimalValue(raw),
            DataType.Bool when raw is "0" or "1" => new ColumnDefault.BooleanValue(raw == "1"),
            DataType.Varchar => new ColumnDefault.StringValue(raw),
            DataType.DateTime when generated && raw.StartsWith("CURRENT_TIMESTAMP", StringComparison.OrdinalIgnoreCase) =>
                new ColumnDefault.CurrentTimestamp(),
            DataType.DateTime when DateTime.TryParseExact(raw, ["yyyy-MM-dd HH:mm:ss.FFFFFF", "yyyy-MM-dd HH:mm:ss"],
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var dateTime) =>
                new ColumnDefault.DateTimeValue(dateTime),
            DataType.Date when DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) =>
                new ColumnDefault.DateValue(date),
            DataType.Uuid when generated && raw.Equals("uuid()", StringComparison.OrdinalIgnoreCase) => new ColumnDefault.GeneratedUuid(),
            DataType.Uuid when Guid.TryParseExact(raw, "D", out var uuid) => new ColumnDefault.UuidValue(uuid),
            _ => new ColumnDefault.StringValue(raw),
        };
    }

    private async Task<object?> ScalarAsync(string sql, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(engineConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection);
        return await command.ExecuteScalarAsync(cancellationToken);
    }

    private static MySqlCommand Command(MySqlConnection connection, string databaseName, string sql)
    {
        var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@db", databaseName);
        return command;
    }

    private static string Name(string name) => MySqlSqlRenderer.Quote(Identifier.Of(name));

    private sealed record RawColumn(string Name, string ColumnType, bool IsNullable, string? Default, string Extra);

    [GeneratedRegex(@"^varchar\((\d+)\)$", RegexOptions.CultureInvariant)]
    private static partial Regex VarcharPattern();

    [GeneratedRegex(@"^decimal\((\d+),(\d+)\)$", RegexOptions.CultureInvariant)]
    private static partial Regex DecimalPattern();
}
