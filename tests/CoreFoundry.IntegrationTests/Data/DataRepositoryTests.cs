using System.Text.Json;
using CoreFoundry.Application.Common;
using CoreFoundry.Application.Data;
using CoreFoundry.Application.SchemaEngine;
using CoreFoundry.Domain.Schema;
using CoreFoundry.Domain.SchemaEngine;
using CoreFoundry.Infrastructure.Data;
using CoreFoundry.Infrastructure.Engine;
using CoreFoundry.IntegrationTests.Infrastructure;
using MySqlConnector;
using Shouldly;

namespace CoreFoundry.IntegrationTests.Data;

/// <summary>
/// <see cref="MySqlDataRepository"/> against real MySQL: tables are created by the schema engine,
/// the <see cref="DataTable"/>s come from a snapshot of them, like after an apply.
/// </summary>
[Collection(TestDatabaseGroup.Name)]
public sealed class DataRepositoryTests : IAsyncLifetime
{
    private readonly TestDatabaseApi _api;
    private readonly string _database = $"cf_p_{1_900_000_000L + Random.Shared.NextInt64(99_999_999)}";
    private DataSchema _schema = DataSchema.Empty(0);

    public DataRepositoryTests(TestDatabaseApi api)
    {
        api.SkipIfUnavailable();
        _api = api;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private MySqlDataRepository Repository => new(_api.EngineConnectionString);

    private DataTable Authors => _schema.FindTable("authors")!;

    private DataTable Books => _schema.FindTable("books")!;

    public async ValueTask InitializeAsync()
    {
        await ExecuteAsync($"CREATE DATABASE `{_database}`");
        var desired = new SchemaModel(
        [
            new TableModel("authors", [Col("name", Varchar(120), nullable: false)], 1),
            new TableModel("books",
            [
                Col("isbn", Varchar(13), unique: true),
                Col("title", Varchar(200), nullable: false),
                Col("price", new ColumnType(DataType.Decimal, null, 10, 2), nullable: false, value: new ColumnDefault.DecimalValue("0")),
                Col("big", new ColumnType(DataType.Decimal, null, 65, 30)),
                Col("pages", new ColumnType(DataType.Int)),
                Col("copies", new ColumnType(DataType.BigInt)),
                Col("in_stock", new ColumnType(DataType.Bool)),
                Col("notes", new ColumnType(DataType.Text)),
                Col("published_on", new ColumnType(DataType.Date)),
                Col("added_at", new ColumnType(DataType.DateTime)),
                Col("public_id", new ColumnType(DataType.Uuid)),
                Col("metadata", new ColumnType(DataType.Json)),
                Col("author_id", new ColumnType(DataType.BigInt)) with { Reference = new ForeignKeyModel("authors", ReferenceAction.Restrict, 1) },
            ], 2),
        ]);
        var introspector = new MySqlSchemaIntrospector(_api.EngineConnectionString);
        var diff = SchemaDiffer.Diff(desired, await introspector.ReadAsync(_database, Ct));
        foreach (var statement in new MySqlSqlRenderer().Render(_database, diff.Operations))
        {
            await ExecuteAsync(statement);
        }

        var actual = await introspector.ReadAsync(_database, Ct);
        var managed = actual.Tables.ToDictionary(
            table => table.Name, table => (IReadOnlySet<string>)table.Columns.Select(column => column.Name).ToHashSet(StringComparer.Ordinal));
        _schema = DataSchema.From(SchemaSnapshot.From(actual), 1, managed);
    }

    public async ValueTask DisposeAsync() => await ExecuteAsync($"DROP DATABASE IF EXISTS `{_database}`");

    [Fact]
    public async Task Every_type_round_trips()
    {
        var author = await InsertAsync(Authors, """{ "name": "Frank Herbert" }""");
        var id = await InsertAsync(Books, $$"""
            {
              "isbn": "9780441013593", "title": "Dune", "price": "19.9",
              "big": "12345678901234567890123456789012345.123456789012345678901234567891",
              "pages": -2147483648, "copies": 9223372036854775807, "in_stock": true, "notes": "O'Reilly \\ \"quoted\" 😀",
              "published_on": "2024-02-29", "added_at": "2024-02-29T13:45:07.123456+02:00",
              "public_id": "3F2504E0-4F89-11D3-9A0C-0305E82C3301", "metadata": { "tags": ["sf", 1] },
              "author_id": {{author}}
            }
            """);

        var row = (await Repository.FindAsync(_database, Books, id, Ct)).ShouldNotBeNull();

        row.Keys.ShouldBe(["id", "isbn", "title", "price", "big", "pages", "copies", "in_stock", "notes", "published_on", "added_at", "public_id", "metadata", "author_id"]);
        row["id"].ShouldBe(id);
        row["price"].ShouldBe("19.90");
        row["big"].ShouldBe("12345678901234567890123456789012345.123456789012345678901234567891");
        row["pages"].ShouldBe(int.MinValue);
        row["copies"].ShouldBe(long.MaxValue);
        row["in_stock"].ShouldBe(true);
        row["notes"].ShouldBe("O'Reilly \\ \"quoted\" 😀");
        row["published_on"].ShouldBe("2024-02-29");
        row["added_at"].ShouldBe("2024-02-29T11:45:07.123456");
        row["public_id"].ShouldBe("3f2504e0-4f89-11d3-9a0c-0305e82c3301");
        JsonSerializer.Serialize(row["metadata"]).ShouldBe("""{"tags":["sf",1]}""");
        row["author_id"].ShouldBe(author);
    }

    [Fact]
    public async Task Left_out_columns_get_null_or_their_default_and_replace_resets_them()
    {
        var id = await InsertAsync(Books, """{ "title": "Dune", "price": "5", "pages": 10 }""");
        var row = (await Repository.FindAsync(_database, Books, id, Ct))!;
        (row["price"], row["pages"], row["isbn"]).ShouldBe(("5.00", (object?)10, null));

        (await Repository.UpdateAsync(_database, Books, id, Coerce(Books, """{ "title": "Dune II" }""", replace: true), Ct)).ShouldBeTrue();

        row = (await Repository.FindAsync(_database, Books, id, Ct))!;
        (row["title"], row["price"], row["pages"]).ShouldBe(("Dune II", "0.00", (object?)null));
    }

    [Fact]
    public async Task Pages_are_sorted_with_id_breaking_ties_and_total_counts_all_rows()
    {
        foreach (var price in new[] { "3", "1", "2", "2", "2" })
        {
            await InsertAsync(Books, $$"""{ "title": "t", "price": "{{price}}" }""");
        }

        var (first, total) = await Repository.ListAsync(_database, Books, SortOrder.Parse(Books, "-price"), 0, 3, Ct);
        var (second, _) = await Repository.ListAsync(_database, Books, SortOrder.Parse(Books, "-price"), 3, 3, Ct);

        total.ShouldBe(5);
        first.Select(row => (string)row["price"]!).ShouldBe(["3.00", "2.00", "2.00"]);
        second.Select(row => (string)row["price"]!).ShouldBe(["2.00", "1.00"]);
        var twos = first.Concat(second).Where(row => (string)row["price"]! == "2.00").Select(row => row.Id).ToList();
        twos.ShouldBe([.. twos.Order()]);
    }

    [Fact]
    public async Task A_duplicate_unique_value_is_a_conflict_naming_the_column()
    {
        await InsertAsync(Books, """{ "title": "a", "isbn": "978" }""");

        var ex = await Should.ThrowAsync<ConflictException>(() => InsertAsync(Books, """{ "title": "b", "isbn": "978" }"""));
        ex.Message.ShouldBe("isbn must be unique; 978 already exists.");
    }

    [Fact]
    public async Task A_reference_to_a_missing_row_is_a_field_error()
    {
        var ex = await Should.ThrowAsync<ValidationFailedException>(() => InsertAsync(Books, """{ "title": "a", "author_id": 424242 }"""));
        ex.Errors["author_id"].ShouldBe(["No authors row with id 424242."]);
    }

    [Fact]
    public async Task Deleting_a_referenced_row_is_a_conflict_and_nothing_is_deleted()
    {
        var author = await InsertAsync(Authors, """{ "name": "Ann" }""");
        await InsertAsync(Books, $$"""{ "title": "a", "author_id": {{author}} }""");

        var ex = await Should.ThrowAsync<ConflictException>(() => Repository.DeleteAsync(_database, Authors, author, Ct));

        ex.Message.ShouldStartWith("Other rows reference this one: books.author_id.");
        (await Repository.FindAsync(_database, Authors, author, Ct)).ShouldNotBeNull();
    }

    [Fact]
    public async Task Null_in_a_not_null_column_is_a_field_error_even_past_validation()
    {
        var title = Books.FindColumn("title")!;
        var ex = await Should.ThrowAsync<ValidationFailedException>(
            () => Repository.InsertAsync(_database, Books, [new ColumnValue(title, null)], Ct));
        ex.Errors.Keys.ShouldBe(["title"]);
    }

    [Fact]
    public async Task Missing_rows_are_reported_not_thrown()
    {
        (await Repository.FindAsync(_database, Books, 999, Ct)).ShouldBeNull();
        (await Repository.UpdateAsync(_database, Books, 999, Coerce(Books, """{ "title": "x" }""", true), Ct)).ShouldBeFalse();
        (await Repository.DeleteAsync(_database, Books, 999, Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task An_unchanged_update_still_finds_the_row()
    {
        var id = await InsertAsync(Books, """{ "title": "same" }""");
        (await Repository.UpdateAsync(_database, Books, id, Coerce(Books, """{ "title": "same" }""", true), Ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task A_table_dropped_outside_CoreFoundry_is_reported_as_drift()
    {
        await ExecuteAsync($"DROP TABLE `{_database}`.`books`");

        var ex = await Should.ThrowAsync<ConflictException>(() => Repository.ListAsync(_database, Books, SortOrder.ById, 0, 10, Ct));
        ex.Message.ShouldContain("schema drift");
    }

    private async Task<long> InsertAsync(DataTable table, string json) =>
        await Repository.InsertAsync(_database, table, Coerce(table, json, replace: false), Ct);

    private static IReadOnlyList<ColumnValue> Coerce(DataTable table, string json, bool replace)
    {
        using var document = JsonDocument.Parse(json);
        return RowCoercer.Coerce(table, document.RootElement, replace);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new MySqlConnection(_api.EngineConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new MySqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static ColumnModel Col(string name, ColumnType type, bool nullable = true, bool unique = false, ColumnDefault? value = null) =>
        new(name, type, nullable, unique, value);

    private static ColumnType Varchar(int length) => new(DataType.Varchar, length);
}
