using System.Globalization;
using CoreFoundry.Domain.Schema;
using CoreFoundry.Domain.SchemaEngine;
using CoreFoundry.Infrastructure.Engine;
using CoreFoundry.IntegrationTests.Infrastructure;
using MySqlConnector;
using Shouldly;

namespace CoreFoundry.IntegrationTests.SchemaEngine;

/// <summary>
/// Differ + renderer + introspector against real MySQL: whatever the renderer creates, the
/// introspector must read back so that the next diff is empty. Each test uses its own cf_p_ database.
/// </summary>
[Collection(TestDatabaseGroup.Name)]
public sealed class EngineRoundTripTests : IAsyncLifetime
{
    private static readonly MySqlSqlRenderer Renderer = new();
    private readonly TestDatabaseApi _api;
    private readonly string _database = $"cf_p_{1_900_000_000L + Random.Shared.NextInt64(99_999_999)}";

    public EngineRoundTripTests(TestDatabaseApi api)
    {
        api.SkipIfUnavailable();
        _api = api;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private MySqlSchemaIntrospector Introspector => new(_api.EngineConnectionString);

    public async ValueTask InitializeAsync() => await ExecuteAsync($"CREATE DATABASE `{_database}`");

    public async ValueTask DisposeAsync() => await ExecuteAsync($"DROP DATABASE IF EXISTS `{_database}`");

    [Fact]
    public async Task Every_type_default_unique_key_and_reference_round_trips()
    {
        var authors = Table("authors", 1,
            Col("name", Varchar(120), 10, nullable: false, unique: true),
            Col("bio", new ColumnType(DataType.Text), 11),
            Col("born_on", new ColumnType(DataType.Date), 12, value: new ColumnDefault.DateValue(new DateOnly(1970, 1, 1))));
        var books = Table("books", 2,
            Col("title", Varchar(200), 20, nullable: false, value: new ColumnDefault.StringValue(@"O'Reilly \ guide")),
            Col("price", Dec(10, 2), 21, nullable: false, value: new ColumnDefault.DecimalValue("7.5")),
            Col("pages", new ColumnType(DataType.Int), 22, value: new ColumnDefault.IntegerValue(-1)),
            Col("copies", new ColumnType(DataType.BigInt), 23, value: new ColumnDefault.IntegerValue(long.MaxValue)),
            Col("in_stock", new ColumnType(DataType.Bool), 24, value: new ColumnDefault.BooleanValue(true)),
            Col("created_at", new ColumnType(DataType.DateTime), 25, nullable: false, value: new ColumnDefault.CurrentTimestamp()),
            Col("released_at", new ColumnType(DataType.DateTime), 26, value: new ColumnDefault.DateTimeValue(new DateTime(2026, 1, 31, 9, 30, 0, 500))),
            Col("public_id", new ColumnType(DataType.Uuid), 27, value: new ColumnDefault.GeneratedUuid()),
            Col("batch", new ColumnType(DataType.Uuid), 28, value: new ColumnDefault.UuidValue(Guid.Parse("3f2504e0-4f89-11d3-9a0c-0305e82c3301"))),
            Col("metadata", new ColumnType(DataType.Json), 29),
            Col("author_id", new ColumnType(DataType.BigInt), 30) with { Reference = new ForeignKeyModel("authors", ReferenceAction.Cascade, 1) });
        var employees = Table("employees", 3,
            Col("manager_id", new ColumnType(DataType.BigInt), 40) with { Reference = new ForeignKeyModel("employees", ReferenceAction.SetNull, 3) });

        var desired = new SchemaModel([books, employees, authors]);
        var statements = await ApplyAsync(desired);

        statements.Count(statement => statement.StartsWith("CREATE TABLE", StringComparison.Ordinal)).ShouldBe(3);
        await ShouldBeInSyncAsync(desired);
    }

    [Fact]
    public async Task Renames_keep_data_and_every_kind_of_change_round_trips()
    {
        var v1 = new SchemaModel(
        [
            Table("authors", 1, Col("name", Varchar(100), 10)),
            Table("books", 2,
                Col("price", Dec(10, 2), 20),
                Col("isbn", Varchar(13), 21, unique: true),
                Col("pages", new ColumnType(DataType.Int), 22),
                Col("notes", new ColumnType(DataType.Text), 23),
                Col("code", Varchar(10), 24, unique: true),
                Col("author_id", new ColumnType(DataType.BigInt), 25) with { Reference = new ForeignKeyModel("authors", ReferenceAction.Cascade, 1) }),
        ]);
        await ApplyAsync(v1);
        await ExecuteAsync($"INSERT INTO `{_database}`.`authors` (`name`) VALUES ('Ann')");
        await ExecuteAsync($"INSERT INTO `{_database}`.`books` (`price`, `isbn`, `pages`, `author_id`) VALUES (12.34, '978', 300, 1)");

        // Rename both tables and several columns, widen types, change nullability/unique/reference, drop and add.
        var v2 = new SchemaModel(
        [
            Table("writers", 1, "authors", Col("full_name", Varchar(150), 10, applied: "name")),
            Table("novels", 2, "books",
                Col("price_usd", Dec(12, 2), 20, applied: "price", nullable: false),
                Col("isbn13", Varchar(13), 21, applied: "isbn", unique: true),
                Col("pages", new ColumnType(DataType.BigInt), 22, applied: "pages"),
                Col("notes", new ColumnType(DataType.Text), 23, applied: "notes", pendingDrop: true),
                Col("code", Varchar(10), 24, applied: "code"),
                Col("author_id", new ColumnType(DataType.BigInt), 25, applied: "author_id") with { Reference = new ForeignKeyModel("writers", ReferenceAction.SetNull, 1) },
                Col("subtitle", Varchar(80), 26)),
        ]);
        var statements = await ApplyAsync(v2);

        statements.ShouldContain(statement => statement.Contains("CHANGE COLUMN `price` `price_usd` DECIMAL(12,2) NOT NULL", StringComparison.Ordinal));
        await ShouldBeInSyncAsync(v2);
        (await ScalarAsync($"SELECT CONCAT(`price_usd`, '|', `isbn13`, '|', `pages`) FROM `{_database}`.`novels`")).ShouldBe("12.34|978|300");
        (await ScalarAsync($"SELECT `full_name` FROM `{_database}`.`writers`")).ShouldBe("Ann");
    }

    [Fact]
    public async Task Dropping_two_tables_that_reference_each_other_works()
    {
        var v1 = new SchemaModel(
        [
            Table("authors", 1, Col("favourite_book_id", new ColumnType(DataType.BigInt), 10) with { Reference = new ForeignKeyModel("books", ReferenceAction.SetNull, 2) }),
            Table("books", 2, Col("author_id", new ColumnType(DataType.BigInt), 20) with { Reference = new ForeignKeyModel("authors", ReferenceAction.Cascade, 1) }),
        ]);
        await ApplyAsync(v1);

        var v2 = new SchemaModel([.. Applied(v1).Tables.Select(table => table with { PendingDrop = true })]);
        await ApplyAsync(v2);

        (await Introspector.ReadAsync(_database, Ct)).Tables.ShouldBeEmpty();
    }

    [Fact]
    public async Task Unmanaged_tables_and_columns_are_reported_and_left_alone()
    {
        var v1 = new SchemaModel([Table("books", 1, Col("title", Varchar(50), 10))]);
        await ApplyAsync(v1);
        await ExecuteAsync($"CREATE TABLE `{_database}`.`legacy` (x INT)");
        await ExecuteAsync($"ALTER TABLE `{_database}`.`books` ADD COLUMN `extra` MEDIUMINT");

        var diff = SchemaDiffer.Diff(Applied(v1), await Introspector.ReadAsync(_database, Ct));

        diff.IsEmpty.ShouldBeTrue();
        diff.UnmanagedTables.ShouldBe(["legacy"]);
        diff.UnmanagedColumns.ShouldBe(["books.extra"]);
    }

    [Fact]
    public async Task Row_and_null_counts_are_read()
    {
        await ApplyAsync(new SchemaModel([Table("books", 1, Col("title", Varchar(50), 10))]));
        (await Introspector.HasRowsAsync(_database, "books", Ct)).ShouldBeFalse();

        await ExecuteAsync($"INSERT INTO `{_database}`.`books` (`title`) VALUES (NULL), (NULL), ('x')");

        (await Introspector.HasRowsAsync(_database, "books", Ct)).ShouldBeTrue();
        (await Introspector.CountNullsAsync(_database, "books", "title", Ct)).ShouldBe(2);
    }

    /// <summary>Diffs, renders and runs the SQL, like an apply without the journal.</summary>
    private async Task<IReadOnlyList<string>> ApplyAsync(SchemaModel desired)
    {
        var diff = SchemaDiffer.Diff(desired, await Introspector.ReadAsync(_database, Ct));
        var statements = Renderer.Render(_database, diff.Operations);
        foreach (var statement in statements)
        {
            await ExecuteAsync(statement);
        }

        return statements;
    }

    /// <summary>After applying, the metadata says everything is applied, and the next plan must be empty.</summary>
    private async Task ShouldBeInSyncAsync(SchemaModel desired)
    {
        var diff = SchemaDiffer.Diff(Applied(desired), await Introspector.ReadAsync(_database, Ct));
        diff.Operations.Select(operation => operation.Describe()).ShouldBeEmpty();
        diff.UnmanagedColumns.ShouldBeEmpty();
        diff.UnmanagedTables.ShouldBeEmpty();
    }

    /// <summary>What the apply's metadata commit does: pending drops disappear, AppliedName = Name.</summary>
    private static SchemaModel Applied(SchemaModel model) => new([.. model.Tables
        .Where(table => !table.PendingDrop)
        .Select(table => table with
        {
            AppliedName = table.Name,
            Columns = [.. table.Columns.Where(column => !column.PendingDrop).Select(column => column with { AppliedName = column.Name })],
        })]);

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new MySqlConnection(_api.EngineConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new MySqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<string?> ScalarAsync(string sql)
    {
        await using var connection = new MySqlConnection(_api.EngineConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new MySqlCommand(sql, connection);
        return Convert.ToString(await command.ExecuteScalarAsync(Ct), CultureInfo.InvariantCulture);
    }

    private static TableModel Table(string name, long id, params ColumnModel[] columns) => new(name, columns, id);

    private static TableModel Table(string name, long id, string applied, params ColumnModel[] columns) => new(name, columns, id, applied);

    private static ColumnModel Col(
        string name, ColumnType type, long id, string? applied = null, bool nullable = true, bool unique = false,
        bool pendingDrop = false, ColumnDefault? value = null) =>
        new(name, type, nullable, unique, value, MetadataId: id, AppliedName: applied, PendingDrop: pendingDrop);

    private static ColumnType Varchar(int length) => new(DataType.Varchar, length);

    private static ColumnType Dec(int precision, int scale) => new(DataType.Decimal, null, (byte)precision, (byte)scale);
}
