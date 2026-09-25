using System.Text.Json;
using CoreFoundry.Application.Common;
using CoreFoundry.Application.Data;
using CoreFoundry.Domain.Schema;
using CoreFoundry.Domain.SchemaEngine;
using Shouldly;

namespace CoreFoundry.UnitTests.Data;

public class RowCoercerTests
{
    private static readonly Dictionary<string, ColumnType> Types = new()
    {
        ["int"] = new(DataType.Int),
        ["bigint"] = new(DataType.BigInt),
        ["decimal"] = new(DataType.Decimal, null, 10, 2),
        ["bool"] = new(DataType.Bool),
        ["varchar"] = new(DataType.Varchar, 5),
        ["text"] = new(DataType.Text),
        ["date"] = new(DataType.Date),
        ["datetime"] = new(DataType.DateTime),
        ["uuid"] = new(DataType.Uuid),
        ["json"] = new(DataType.Json),
    };

    [Theory]
    [InlineData("int", "42", 42)]
    [InlineData("int", "2147483647", int.MaxValue)]
    [InlineData("int", "-2147483648", int.MinValue)]
    [InlineData("bigint", "9223372036854775807", long.MaxValue)]
    [InlineData("bigint", "-9223372036854775808", long.MinValue)]
    [InlineData("decimal", "19.99", "19.99")]
    [InlineData("decimal", "\"19.99\"", "19.99")]
    [InlineData("decimal", "12345678.99", "12345678.99")]
    [InlineData("decimal", "-0.5", "-0.5")]
    [InlineData("decimal", "7", "7")]
    [InlineData("decimal", "\"007.10\"", "7.1")]
    [InlineData("decimal", "19.990", "19.99")] // trailing zeros lose nothing
    [InlineData("decimal", "-0.00", "0")]
    [InlineData("bool", "true", true)]
    [InlineData("bool", "false", false)]
    [InlineData("varchar", "\"abcde\"", "abcde")]
    [InlineData("varchar", "\"\"", "")]
    [InlineData("varchar", "\"😀😀😀😀😀\"", "😀😀😀😀😀")] // 5 characters, 10 UTF-16 units
    [InlineData("text", "\"O'Reilly\"", "O'Reilly")]
    [InlineData("uuid", "\"3F2504E0-4F89-11D3-9A0C-0305E82C3301\"", "3f2504e0-4f89-11d3-9a0c-0305e82c3301")]
    [InlineData("json", "{\"a\": [1, 2]}", "{\"a\": [1, 2]}")]
    [InlineData("json", "\"just text\"", "\"just text\"")]
    [InlineData("json", "3.5", "3.5")]
    public void Accepted_values(string type, string json, object expected) =>
        Convert(type, json).ShouldBe(expected);

    [Theory]
    [InlineData("date", "\"2024-02-29\"", 2024, 2, 29)] // leap day
    [InlineData("date", "\"1000-01-01\"", 1000, 1, 1)]
    [InlineData("date", "\"9999-12-31\"", 9999, 12, 31)]
    public void Dates(string type, string json, int year, int month, int day) =>
        Convert(type, json).ShouldBe(new DateOnly(year, month, day));

    [Theory]
    [InlineData("\"2024-02-29T13:45\"", "2024-02-29T13:45:00.0000000")]
    [InlineData("\"2024-02-29T13:45:07\"", "2024-02-29T13:45:07.0000000")]
    [InlineData("\"2024-02-29T13:45:07.123456\"", "2024-02-29T13:45:07.1234560")]
    [InlineData("\"2024-02-29T13:45:07Z\"", "2024-02-29T13:45:07.0000000")]
    [InlineData("\"2024-02-29T13:45:07+02:00\"", "2024-02-29T11:45:07.0000000")] // offset → UTC
    [InlineData("\"2024-03-01T01:00:00+03:00\"", "2024-02-29T22:00:00.0000000")]
    public void Date_times_have_no_offset_and_offsets_become_utc(string json, string expected)
    {
        var value = Convert("datetime", json).ShouldBeOfType<DateTime>();
        value.ToString("O", System.Globalization.CultureInfo.InvariantCulture).ShouldBe(expected);
        value.Kind.ShouldBe(DateTimeKind.Unspecified);
    }

    [Theory]
    [InlineData("int", "2147483648")]
    [InlineData("int", "-2147483649")]
    [InlineData("int", "1.5")]
    [InlineData("int", "1.0")]
    [InlineData("int", "1e3")]
    [InlineData("int", "\"42\"")]
    [InlineData("int", "true")]
    [InlineData("bigint", "9223372036854775808")]
    [InlineData("bigint", "\"1\"")]
    [InlineData("decimal", "19.999")] // no silent rounding
    [InlineData("decimal", "123456789.5")] // 9 digits before the point, 8 allowed
    [InlineData("decimal", "1e2")]
    [InlineData("decimal", "\"12,5\"")]
    [InlineData("decimal", "\" 1\"")]
    [InlineData("decimal", "\"٣\"")] // non-ASCII digit
    [InlineData("decimal", "\"\"")]
    [InlineData("decimal", "\".5\"")]
    [InlineData("decimal", "true")]
    [InlineData("bool", "0")]
    [InlineData("bool", "1")]
    [InlineData("bool", "\"true\"")]
    [InlineData("varchar", "\"abcdef\"")]
    [InlineData("varchar", "12")]
    [InlineData("text", "12")]
    [InlineData("text", "[\"a\"]")]
    [InlineData("date", "\"2023-02-29\"")] // not a leap year
    [InlineData("date", "\"2024-2-9\"")]
    [InlineData("date", "\"29/02/2024\"")]
    [InlineData("date", "\"2024-02-29T00:00:00\"")]
    [InlineData("date", "\"0999-12-31\"")]
    [InlineData("date", "20240229")]
    [InlineData("datetime", "\"2024-02-29\"")]
    [InlineData("datetime", "\"2024-02-29 13:45:00\"")]
    [InlineData("datetime", "\"2024-02-29T25:00:00\"")]
    [InlineData("datetime", "\"2024-02-29T13:45:00.1234567\"")] // more precision than the column keeps
    [InlineData("datetime", "\"2024-02-29T13:45:00+0200\"")]
    [InlineData("datetime", "1709214300")]
    [InlineData("uuid", "\"not-a-uuid\"")]
    [InlineData("uuid", "\"3f2504e04f8911d39a0c0305e82c3301\"")]
    [InlineData("uuid", "\"{3f2504e0-4f89-11d3-9a0c-0305e82c3301}\"")]
    [InlineData("uuid", "42")]
    public void Rejected_values(string type, string json)
    {
        var column = Column(type);
        RowCoercer.TryConvert(column, Parse(json), out _, out var error).ShouldBeFalse();
        error.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Text_is_limited_in_utf8_bytes()
    {
        var column = Column("text");
        RowCoercer.TryConvert(column, Parse(JsonSerializer.Serialize(new string('a', RowCoercer.TextMaxBytes))), out _, out _).ShouldBeTrue();
        RowCoercer.TryConvert(column, Parse(JsonSerializer.Serialize(new string('é', 32_768))), out _, out var error).ShouldBeFalse();
        error.ShouldContain("bytes");
    }

    [Fact]
    public void Null_is_accepted_only_for_nullable_columns()
    {
        RowCoercer.TryConvert(Column("int", nullable: true), Parse("null"), out var value, out _).ShouldBeTrue();
        value.ShouldBeNull();

        RowCoercer.TryConvert(Column("json"), Parse("null"), out _, out var error).ShouldBeFalse();
        error.ShouldBe("Can't be null.");
    }

    [Fact]
    public void A_reference_error_names_the_referenced_table()
    {
        var column = new DataColumn("author_id", new ColumnType(DataType.BigInt), "BigInt", false, false, null, "authors");
        RowCoercer.TryConvert(column, Parse("\"x\""), out _, out var error).ShouldBeFalse();
        error.ShouldBe("Must be the id of a row in authors.");
    }

    // ---- whole bodies ----

    private static readonly DataTable Books = new("books",
    [
        new DataColumn("title", new ColumnType(DataType.Varchar, 200), "Varchar(200)", false, true, null, null),
        new DataColumn("price", new ColumnType(DataType.Decimal, null, 10, 2), "Decimal(10,2)", false, false, "0.00", null),
        new DataColumn("notes", new ColumnType(DataType.Text), "Text", true, false, null, null),
        new DataColumn("legacy", null, "mediumint(9)", true, false, null, null),
    ]);

    [Fact]
    public void Insert_returns_only_the_given_columns_in_table_order()
    {
        var values = RowCoercer.Coerce(Books, Parse("""{ "price": 9.5, "title": "Dune" }"""), replace: false);

        values.Select(value => (value.Column.Name, value.Value, value.UseDefault))
            .ShouldBe([("title", (object?)"Dune", false), ("price", "9.5", false)]);
    }

    [Fact]
    public void Replace_sets_left_out_columns_to_their_default_or_null_and_never_touches_read_only_ones()
    {
        var values = RowCoercer.Coerce(Books, Parse("""{ "title": "Dune" }"""), replace: true);

        values.Select(value => (value.Column.Name, value.Value, value.UseDefault))
            .ShouldBe([("title", (object?)"Dune", false), ("price", null, true), ("notes", null, false)]);
    }

    [Fact]
    public void Every_error_is_reported_at_once_keyed_by_field()
    {
        var body = Parse("""{ "id": 5, "price": 1.999, "legacy": 3, "author": "x", "notes": 7 }""");

        var errors = Should.Throw<ValidationFailedException>(() => RowCoercer.Coerce(Books, body, replace: false)).Errors;

        errors.Keys.Order(StringComparer.Ordinal).ShouldBe(["author", "id", "legacy", "notes", "price", "title"]);
        errors["title"].ShouldBe(["Required."]);
        errors["price"].ShouldBe(["Must have at most 2 decimals."]);
        errors["author"].ShouldHaveSingleItem().ShouldContain("has no column author");
        errors["legacy"].ShouldHaveSingleItem().ShouldContain("read-only");
        errors["id"].ShouldHaveSingleItem().ShouldContain("leave it out");
    }

    [Fact]
    public void Required_applies_to_replace_too()
    {
        var errors = Should.Throw<ValidationFailedException>(() => RowCoercer.Coerce(Books, Parse("{}"), replace: true)).Errors;
        errors.Keys.ShouldBe(["title"]);
    }

    [Fact]
    public void A_field_given_twice_is_an_error()
    {
        var errors = Should.Throw<ValidationFailedException>(
            () => RowCoercer.Coerce(Books, Parse("""{ "title": "a", "title": "b" }"""), replace: false)).Errors;
        errors["title"].ShouldBe(["Given more than once."]);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"title\"")]
    [InlineData("null")]
    public void The_body_must_be_an_object(string json) =>
        Should.Throw<ValidationFailedException>(() => RowCoercer.Coerce(Books, Parse(json), replace: false))
            .Errors.Keys.ShouldBe([""]);

    [Theory]
    [InlineData("title`; DROP TABLE books; --")]
    [InlineData("TITLE")]
    [InlineData("title ")]
    public void Keys_are_only_ever_matched_exactly(string key)
    {
        var body = Parse(JsonSerializer.Serialize(new Dictionary<string, string> { ["title"] = "Dune", [key] = "x" }));
        Should.Throw<ValidationFailedException>(() => RowCoercer.Coerce(Books, body, replace: false)).Errors.Keys.ShouldBe([key]);
    }

    private static object? Convert(string type, string json)
    {
        RowCoercer.TryConvert(Column(type), Parse(json), out var value, out var error).ShouldBeTrue(error);
        return value;
    }

    private static DataColumn Column(string type, bool nullable = false) =>
        new(type, Types[type], Types[type].ToString(), nullable, false, null, null);

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
