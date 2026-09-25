using CoreFoundry.Application.Common;
using CoreFoundry.Application.Data;
using CoreFoundry.Domain.Schema;
using CoreFoundry.Domain.SchemaEngine;
using CoreFoundry.Infrastructure.Data;
using Shouldly;

namespace CoreFoundry.UnitTests.Data;

public class DataSqlTests
{
    private const string Db = "cf_p_7";

    private static readonly DataTable Books = new("books",
    [
        new DataColumn("title", new ColumnType(DataType.Varchar, 200), "Varchar(200)", false, true, null, null),
        new DataColumn("price_usd", new ColumnType(DataType.Decimal, null, 10, 2), "Decimal(10,2)", false, false, "0.00", null),
        new DataColumn("published_on", new ColumnType(DataType.Date), "Date", true, false, null, null),
    ]);

    [Fact]
    public void Select_lists_id_first_orders_by_the_column_then_id_and_pages_with_parameters()
    {
        var command = DataSql.Select(Db, Books, SortOrder.Parse(Books, "-price_usd"), skip: 25, take: 25);

        command.Sql.ShouldBe(
            "SELECT `id`, `title`, `price_usd`, `published_on` FROM `cf_p_7`.`books` ORDER BY `price_usd` DESC, `id` LIMIT @take OFFSET @skip");
        command.Parameters.ShouldBe(new Dictionary<string, object?> { ["take"] = 25, ["skip"] = 25 });
    }

    [Theory]
    [InlineData(null, "ORDER BY `id` LIMIT")]
    [InlineData("", "ORDER BY `id` LIMIT")]
    [InlineData("id", "ORDER BY `id` LIMIT")]
    [InlineData("-id", "ORDER BY `id` DESC LIMIT")]
    [InlineData("title", "ORDER BY `title`, `id` LIMIT")]
    public void Sort_orders(string? sort, string expected) =>
        DataSql.Select(Db, Books, SortOrder.Parse(Books, sort), 0, 10).Sql.ShouldContain(expected);

    [Theory]
    [InlineData("author")]
    [InlineData("Title")]
    [InlineData("--title")]
    [InlineData("-")]
    [InlineData("title; DROP TABLE books")]
    [InlineData("`title`")]
    [InlineData("1")]
    public void Unknown_sort_is_a_validation_error(string sort) =>
        Should.Throw<ValidationFailedException>(() => SortOrder.Parse(Books, sort)).Errors.Keys.ShouldBe(["sort"]);

    [Fact]
    public void Count_and_select_by_id() =>
        (DataSql.Count(Db, Books).Sql, DataSql.SelectById(Db, Books, 5).Sql, DataSql.SelectById(Db, Books, 5).Parameters["id"]).ShouldBe((
            "SELECT COUNT(*) FROM `cf_p_7`.`books`",
            "SELECT `id`, `title`, `price_usd`, `published_on` FROM `cf_p_7`.`books` WHERE `id` = @id",
            (object?)5L));

    [Fact]
    public void Insert_uses_ordinal_parameters_and_dates_become_date_times()
    {
        var command = DataSql.Insert(Db, Books,
        [
            new ColumnValue(Books.Columns[0], "Dune"),
            new ColumnValue(Books.Columns[1], "19.99"),
            new ColumnValue(Books.Columns[2], new DateOnly(1965, 8, 1)),
        ]);

        command.Sql.ShouldBe("INSERT INTO `cf_p_7`.`books` (`title`, `price_usd`, `published_on`) VALUES (@p_0, @p_1, @p_2)");
        command.Parameters.ShouldBe(new Dictionary<string, object?>
        {
            ["p_0"] = "Dune", ["p_1"] = "19.99", ["p_2"] = new DateTime(1965, 8, 1),
        });
    }

    [Fact]
    public void Insert_with_nothing_given_uses_every_default() =>
        DataSql.Insert(Db, Books, []).Sql.ShouldBe("INSERT INTO `cf_p_7`.`books` () VALUES ()");

    [Fact]
    public void Update_is_a_full_replace_with_default_and_null()
    {
        var command = DataSql.Update(Db, Books, 42,
        [
            new ColumnValue(Books.Columns[0], "Dune"),
            new ColumnValue(Books.Columns[1], null, UseDefault: true),
            new ColumnValue(Books.Columns[2], null),
        ]);

        command.Sql.ShouldBe("UPDATE `cf_p_7`.`books` SET `title` = @p_0, `price_usd` = DEFAULT, `published_on` = @p_2 WHERE `id` = @id");
        command.Parameters.ShouldBe(new Dictionary<string, object?> { ["id"] = 42L, ["p_0"] = "Dune", ["p_2"] = null });
    }

    [Fact]
    public void Update_with_nothing_writable_still_matches_the_row() =>
        DataSql.Update(Db, Books, 1, []).Sql.ShouldBe("UPDATE `cf_p_7`.`books` SET `id` = `id` WHERE `id` = @id");

    [Fact]
    public void Delete() =>
        DataSql.Delete(Db, Books, 3).Sql.ShouldBe("DELETE FROM `cf_p_7`.`books` WHERE `id` = @id");

    [Fact]
    public void Lookup_searches_the_label_with_escaped_wildcards_or_the_exact_id()
    {
        var command = DataSql.Lookup(Db, Books, Books.LabelColumn, @"12%_\", 20);

        command.Sql.ShouldBe("SELECT `id`, `title` FROM `cf_p_7`.`books` WHERE `title` LIKE @search ORDER BY `title`, `id` LIMIT @take");
        command.Parameters["search"].ShouldBe(@"%12\%\_\\%");

        var byId = DataSql.Lookup(Db, Books, Books.LabelColumn, "42", 20);
        byId.Sql.ShouldContain("WHERE `title` LIKE @search OR `id` = @id");
        byId.Parameters["id"].ShouldBe(42L);
    }

    [Fact]
    public void Lookup_without_a_label_column_lists_ids()
    {
        var numbers = new DataTable("numbers", [new DataColumn("n", new ColumnType(DataType.Int), "Int", true, false, null, null)]);

        numbers.LabelColumn.ShouldBeNull();
        DataSql.Lookup(Db, numbers, null, null, 5).Sql.ShouldBe("SELECT `id`, NULL FROM `cf_p_7`.`numbers` ORDER BY `id` LIMIT @take");
        DataSql.Lookup(Db, numbers, null, "abc", 5).Sql.ShouldContain("WHERE FALSE");
    }

    [Fact]
    public void A_name_that_somehow_is_unsafe_never_reaches_the_sql()
    {
        var crafted = new DataTable("books`; DROP DATABASE x; --", []);
        Should.Throw<ArgumentException>(() => DataSql.Count(Db, crafted));

        var badColumn = new DataTable("books", [Books.Columns[0] with { Name = "title` = 1; --" }]);
        Should.Throw<ArgumentException>(() => DataSql.SelectById(Db, badColumn, 1));
    }
}
