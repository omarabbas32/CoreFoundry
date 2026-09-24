using System.Globalization;
using System.Text;
using CoreFoundry.Application.SchemaEngine;
using CoreFoundry.Domain.Schema;
using CoreFoundry.Domain.SchemaEngine;

namespace CoreFoundry.Infrastructure.Engine;

/// <summary>
/// Turns schema operations into MySQL DDL. No I/O: the same operations always give the same SQL.
/// </summary>
/// <remarks>
/// Every name goes through <see cref="Quote"/>, which only accepts an <see cref="Identifier"/>, so a
/// name that isn't <c>[a-z][a-z0-9_]</c> can't reach the SQL even if it skipped validation.
/// Defaults can't be parameters in DDL, so they are written as literals from their typed value.
/// Statements have no trailing semicolon: each one is executed on its own.
/// </remarks>
public sealed class MySqlSqlRenderer : ISqlRenderer
{
    private const string TableOptions = "ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci";

    public IReadOnlyList<string> Render(string databaseName, IReadOnlyList<SchemaOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        var database = Identifier.Of(databaseName);
        var statements = new List<string>();

        foreach (var phase in operations.GroupBy(operation => operation.Phase).OrderBy(group => group.Key))
        {
            switch (phase.Key)
            {
                case OperationPhase.DropForeignKeys:
                    statements.AddRange(AlterEach(database, phase, operation =>
                        [$"DROP FOREIGN KEY {Name(((DropForeignKey)operation).Name)}"]));
                    break;
                case OperationPhase.DropTables:
                    statements.AddRange(phase.Select(operation => $"DROP TABLE {Table(database, operation.Table)}"));
                    break;
                case OperationPhase.RenameTables:
                    statements.AddRange(phase.Cast<RenameTable>().Select(rename =>
                        $"RENAME TABLE {Table(database, rename.Table)} TO {Table(database, rename.NewName)}"));
                    break;
                case OperationPhase.CreateTables:
                    statements.AddRange(phase.Cast<CreateTable>().Select(create => CreateTableSql(database, create.Definition)));
                    break;
                case OperationPhase.AlterTables:
                    statements.AddRange(phase.GroupBy(operation => operation.Table).Select(table => AlterTableSql(database, table.Key, [.. table])));
                    break;
                case OperationPhase.AddForeignKeys:
                    statements.AddRange(AlterEach(database, phase, operation =>
                    {
                        var add = (AddForeignKey)operation;
                        return [$"ADD CONSTRAINT {Name(add.Name)} FOREIGN KEY ({Name(add.Column)}) " +
                                $"REFERENCES {Table(database, add.TargetTable)} ({Name("id")}) ON DELETE {OnDeleteSql(add.OnDelete)}"];
                    }));
                    break;
                default:
                    throw new InvalidOperationException($"Unknown phase {phase.Key}.");
            }
        }

        return statements;
    }

    /// <summary>The only way a name gets into SQL: backtick-quoted, and only if it's a safe <see cref="Identifier"/>.</summary>
    public static string Quote(Identifier identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        return $"`{identifier.Value}`";
    }

    /// <summary>A column definition as used by CREATE TABLE, ADD, MODIFY and CHANGE.</summary>
    public static string ColumnSql(ColumnModel column)
    {
        ArgumentNullException.ThrowIfNull(column);
        var type = column.Type ?? throw new InvalidOperationException($"Column {column.Name} has no type to render.");
        var sql = new StringBuilder($"{Name(column.Name)} {TypeSql(type)} {(column.IsNullable ? "NULL" : "NOT NULL")}");
        if (column.Default is { } value)
        {
            sql.Append(" DEFAULT ").Append(DefaultSql(value));
        }

        return sql.ToString();
    }

    public static string TypeSql(ColumnType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type.DataType switch
        {
            DataType.Int => "INT",
            DataType.BigInt => "BIGINT",
            DataType.Decimal => string.Create(CultureInfo.InvariantCulture, $"DECIMAL({type.Precision},{type.Scale})"),
            DataType.Bool => "TINYINT(1)",
            DataType.Varchar => string.Create(CultureInfo.InvariantCulture, $"VARCHAR({type.Length})"),
            DataType.Text => "TEXT",
            DataType.DateTime => "DATETIME(6)",
            DataType.Date => "DATE",
            DataType.Json => "JSON",
            DataType.Uuid => "CHAR(36)",
            _ => throw new InvalidOperationException($"Unknown data type {type.DataType}."),
        };
    }

    /// <summary>A default as a SQL literal, written from the typed value (never from user text).</summary>
    public static string DefaultSql(ColumnDefault value) => value switch
    {
        ColumnDefault.IntegerValue integer => integer.Value.ToString(CultureInfo.InvariantCulture),
        ColumnDefault.DecimalValue number => number.Value, // validated: optional '-', digits, optional '.digits'
        ColumnDefault.BooleanValue boolean => boolean.Value ? "1" : "0",
        ColumnDefault.StringValue text => StringLiteral(text.Value),
        ColumnDefault.DateTimeValue dateTime => StringLiteral(dateTime.Value.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture)),
        ColumnDefault.DateValue date => StringLiteral(date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
        ColumnDefault.UuidValue uuid => StringLiteral(uuid.Canonical),
        ColumnDefault.CurrentTimestamp => "CURRENT_TIMESTAMP(6)",
        ColumnDefault.GeneratedUuid => "(UUID())",
        _ => throw new InvalidOperationException($"Unknown default {value?.GetType().Name}."),
    };

    /// <summary>A single-quoted MySQL string with <c>'</c>, <c>\</c> and NUL escaped.</summary>
    public static string StringLiteral(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var escaped = value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "''", StringComparison.Ordinal)
            .Replace("\0", "\\0", StringComparison.Ordinal);
        return $"'{escaped}'";
    }

    private static string CreateTableSql(Identifier database, TableModel table)
    {
        var lines = new List<string> { $"{Name("id")} BIGINT NOT NULL AUTO_INCREMENT" };
        lines.AddRange(table.Columns.Select(ColumnSql));
        lines.Add($"PRIMARY KEY ({Name("id")})");
        lines.AddRange(table.Columns.Where(column => column.IsUnique).Select(column =>
            $"UNIQUE KEY {Name(ConstraintNames.UniqueKey(table.Name, column.Name))} ({Name(column.Name)})"));

        return $"CREATE TABLE {Table(database, table.Name)} (\n  {string.Join(",\n  ", lines)}\n) {TableOptions}";
    }

    /// <summary>
    /// One ALTER TABLE for all of a table's column and index changes. A column that is renamed and
    /// modified in the same plan becomes one CHANGE COLUMN (MySQL can't MODIFY a name it's renaming).
    /// </summary>
    private static string AlterTableSql(Identifier database, string table, IReadOnlyList<SchemaOperation> operations)
    {
        var renames = operations.OfType<RenameColumn>().ToDictionary(rename => rename.Column, StringComparer.Ordinal);
        var modifies = operations.OfType<ModifyColumn>().ToDictionary(modify => modify.Column, StringComparer.Ordinal);
        var clauses = new List<string>();

        foreach (var rename in renames.Values)
        {
            clauses.Add(modifies.Remove(rename.Column, out var modify)
                ? $"CHANGE COLUMN {Name(rename.Column)} {ColumnSql(modify.Desired)}"
                : $"RENAME COLUMN {Name(rename.Column)} TO {Name(rename.NewName)}");
        }

        clauses.AddRange(operations.OfType<AddColumn>().Select(add => $"ADD COLUMN {ColumnSql(add.Column)}"));
        clauses.AddRange(modifies.Values.Select(modify => $"MODIFY COLUMN {ColumnSql(modify.Desired)}"));
        clauses.AddRange(operations.OfType<DropColumn>().Select(drop => $"DROP COLUMN {Name(drop.Column)}"));
        clauses.AddRange(operations.OfType<DropUniqueKey>().Select(drop => $"DROP INDEX {Name(drop.Name)}"));
        clauses.AddRange(operations.OfType<RenameUniqueKey>().Select(rename => $"RENAME INDEX {Name(rename.Name)} TO {Name(rename.NewName)}"));
        clauses.AddRange(operations.OfType<AddUniqueKey>().Select(add => $"ADD UNIQUE KEY {Name(add.Name)} ({Name(add.Column)})"));

        return $"ALTER TABLE {Table(database, table)}\n  {string.Join(",\n  ", clauses)}";
    }

    private static IEnumerable<string> AlterEach(
        Identifier database, IEnumerable<SchemaOperation> operations, Func<SchemaOperation, IEnumerable<string>> clauses) =>
        operations.GroupBy(operation => operation.Table)
            .Select(table => $"ALTER TABLE {Table(database, table.Key)}\n  {string.Join(",\n  ", table.SelectMany(clauses))}");

    private static string OnDeleteSql(ReferenceAction action) => action switch
    {
        ReferenceAction.Restrict => "RESTRICT",
        ReferenceAction.Cascade => "CASCADE",
        ReferenceAction.SetNull => "SET NULL",
        _ => throw new InvalidOperationException($"Unknown reference action {action}."),
    };

    private static string Table(Identifier database, string table) => $"{Quote(database)}.{Name(table)}";

    /// <summary>Validates a name as an <see cref="Identifier"/> (throws if unsafe) and quotes it.</summary>
    private static string Name(string name) => Quote(Identifier.Of(name));
}
