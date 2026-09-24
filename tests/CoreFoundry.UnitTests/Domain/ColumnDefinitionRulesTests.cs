using CoreFoundry.Domain.Common;
using CoreFoundry.Domain.Schema;
using Shouldly;

namespace CoreFoundry.UnitTests.Domain;

public class ColumnDefinitionRulesTests
{
    // ---- Length / precision / scale ------------------------------------------------------------

    [Theory]
    [InlineData(DataType.Int)]
    [InlineData(DataType.BigInt)]
    [InlineData(DataType.Bool)]
    [InlineData(DataType.Text)]
    [InlineData(DataType.DateTime)]
    [InlineData(DataType.Date)]
    [InlineData(DataType.Json)]
    [InlineData(DataType.Uuid)]
    public void Types_without_parameters_accept_none(DataType type) =>
        Create(type).DataType.ShouldBe(type);

    [Theory]
    [InlineData(DataType.Int)]
    [InlineData(DataType.Decimal)]
    [InlineData(DataType.Text)]
    [InlineData(DataType.Uuid)]
    public void Length_is_only_for_varchar(DataType type) =>
        FieldOf(() => Create(type, length: 10, precision: Precision(type), scale: Scale(type))).ShouldBe("length");

    [Theory]
    [InlineData(DataType.Int)]
    [InlineData(DataType.Varchar)]
    [InlineData(DataType.DateTime)]
    public void Precision_and_scale_are_only_for_decimal(DataType type)
    {
        int? length = type == DataType.Varchar ? 10 : null;
        FieldOf(() => Create(type, length: length, precision: 10)).ShouldBe("precision");
        FieldOf(() => Create(type, length: length, scale: 2)).ShouldBe("scale");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(255)]
    [InlineData(4000)]
    public void Varchar_length_between_1_and_4000_is_valid(int length) =>
        Create(DataType.Varchar, length: length).Length.ShouldBe(length);

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(4001)]
    public void Varchar_length_outside_1_to_4000_is_rejected(int? length) =>
        FieldOf(() => Create(DataType.Varchar, length: length)).ShouldBe("length");

    [Theory]
    [InlineData(1, 0)]
    [InlineData(10, 2)]
    [InlineData(65, 30)]
    [InlineData(30, 30)]
    public void Valid_decimal_precision_and_scale(int precision, int scale)
    {
        var definition = Create(DataType.Decimal, precision: precision, scale: scale);

        definition.Precision.ShouldBe((byte)precision);
        definition.Scale.ShouldBe((byte)scale);
    }

    [Theory]
    [InlineData(null, 0, "precision")]
    [InlineData(0, 0, "precision")]
    [InlineData(66, 0, "precision")]
    [InlineData(10, null, "scale")]
    [InlineData(10, -1, "scale")]
    [InlineData(65, 31, "scale")]
    [InlineData(5, 6, "scale")]
    public void Invalid_decimal_precision_or_scale_is_rejected(int? precision, int? scale, string field) =>
        FieldOf(() => Create(DataType.Decimal, precision: precision, scale: scale)).ShouldBe(field);

    [Fact]
    public void Unknown_data_type_is_rejected() =>
        FieldOf(() => Create((DataType)99)).ShouldBe("dataType");

    // ---- Unique ------------------------------------------------------------------------------------

    [Theory]
    [InlineData(DataType.Text)]
    [InlineData(DataType.Json)]
    public void Text_and_json_cannot_be_unique(DataType type) =>
        FieldOf(() => Create(type, isUnique: true)).ShouldBe("isUnique");

    [Fact]
    public void Unique_varchar_is_limited_by_the_index_key_size()
    {
        Create(DataType.Varchar, length: 768, isUnique: true).IsUnique.ShouldBeTrue();
        FieldOf(() => Create(DataType.Varchar, length: 769, isUnique: true)).ShouldBe("isUnique");
    }

    // ---- Defaults ----------------------------------------------------------------------------------

    [Theory]
    [InlineData(DataType.Int, "42", "42")]
    [InlineData(DataType.Int, " -7 ", "-7")]
    [InlineData(DataType.Int, "+007", "7")]
    [InlineData(DataType.Int, "2147483647", "2147483647")]
    [InlineData(DataType.Int, "-2147483648", "-2147483648")]
    [InlineData(DataType.BigInt, "9223372036854775807", "9223372036854775807")]
    [InlineData(DataType.BigInt, "-9223372036854775808", "-9223372036854775808")]
    [InlineData(DataType.Bool, "true", "true")]
    [InlineData(DataType.Bool, "FALSE", "false")]
    [InlineData(DataType.DateTime, "current_timestamp", "CURRENT_TIMESTAMP")]
    [InlineData(DataType.DateTime, "2026-01-31T09:30:00", "2026-01-31T09:30:00")]
    [InlineData(DataType.DateTime, "2026-01-31T09:30", "2026-01-31T09:30:00")]
    [InlineData(DataType.DateTime, "2026-01-31T09:30:00.123400", "2026-01-31T09:30:00.1234")]
    [InlineData(DataType.DateTime, "1000-01-01T00:00:00", "1000-01-01T00:00:00")]
    [InlineData(DataType.Date, "2026-02-28", "2026-02-28")]
    [InlineData(DataType.Uuid, "uuid()", "UUID()")]
    [InlineData(DataType.Uuid, "3F2504E0-4F89-11D3-9A0C-0305E82C3301", "3f2504e0-4f89-11d3-9a0c-0305e82c3301")]
    public void Valid_defaults_are_parsed_to_their_canonical_form(DataType type, string input, string canonical) =>
        Create(type, defaultValue: input).Default!.Canonical.ShouldBe(canonical);

    [Theory]
    [InlineData(DataType.Int, "2147483648")]
    [InlineData(DataType.Int, "-2147483649")]
    [InlineData(DataType.Int, "1.0")]
    [InlineData(DataType.Int, "1e3")]
    [InlineData(DataType.Int, "abc")]
    [InlineData(DataType.Int, "0x10")]
    [InlineData(DataType.BigInt, "9223372036854775808")]
    [InlineData(DataType.Bool, "yes")]
    [InlineData(DataType.Bool, "1")]
    [InlineData(DataType.DateTime, "now()")]
    [InlineData(DataType.DateTime, "2026-02-30T00:00:00")]
    [InlineData(DataType.DateTime, "2026-01-31 09:30:00")]
    [InlineData(DataType.DateTime, "2026-01-31T09:30:00Z")]
    [InlineData(DataType.DateTime, "2026-01-31T09:30:00.1234567")]
    [InlineData(DataType.DateTime, "0999-12-31T00:00:00")]
    [InlineData(DataType.Date, "2026-1-5")]
    [InlineData(DataType.Date, "31/01/2026")]
    [InlineData(DataType.Date, "0999-12-31")]
    [InlineData(DataType.Uuid, "not-a-uuid")]
    [InlineData(DataType.Uuid, "{3f2504e0-4f89-11d3-9a0c-0305e82c3301}")]
    [InlineData(DataType.Text, "hello")]
    [InlineData(DataType.Json, "{}")]
    public void Invalid_defaults_are_rejected(DataType type, string input) =>
        FieldOf(() => Create(type, defaultValue: input)).ShouldBe("defaultValue");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Missing_default_means_no_default(string? input) =>
        Create(DataType.Int, defaultValue: input).Default.ShouldBeNull();

    [Theory]
    [InlineData("0", "0")]
    [InlineData("12.5", "12.5")]
    [InlineData("-12.50", "-12.50")]
    [InlineData("00012.00", "12.00")]
    [InlineData("-0.00", "0.00")]
    [InlineData("99999999.99", "99999999.99")]
    public void Decimal_defaults_that_fit_10_2_are_accepted(string input, string canonical) =>
        Create(DataType.Decimal, precision: 10, scale: 2, defaultValue: input).Default!.Canonical.ShouldBe(canonical);

    [Theory]
    [InlineData("100000000")]
    [InlineData("1.234")]
    [InlineData(".5")]
    [InlineData("1.")]
    [InlineData("1,5")]
    [InlineData("1e2")]
    public void Decimal_defaults_that_do_not_fit_10_2_are_rejected(string input) =>
        FieldOf(() => Create(DataType.Decimal, precision: 10, scale: 2, defaultValue: input)).ShouldBe("defaultValue");

    [Fact]
    public void Decimal_default_can_use_the_full_65_30_range()
    {
        var value = new string('9', 35) + "." + new string('9', 30);

        Create(DataType.Decimal, precision: 65, scale: 30, defaultValue: value).Default!.Canonical.ShouldBe(value);
    }

    [Fact]
    public void Varchar_default_keeps_spaces_and_counts_characters_not_utf16_units()
    {
        Create(DataType.Varchar, length: 5, defaultValue: " a b ").Default!.Canonical.ShouldBe(" a b ");
        Create(DataType.Varchar, length: 2, defaultValue: "😀😀").Default.ShouldBeOfType<ColumnDefault.StringValue>();
        FieldOf(() => Create(DataType.Varchar, length: 2, defaultValue: "abc")).ShouldBe("defaultValue");
    }

    [Fact]
    public void Defaults_longer_than_the_stored_maximum_are_rejected() =>
        FieldOf(() => Create(DataType.Varchar, length: 4000, defaultValue: new string('a', 256))).ShouldBe("defaultValue");

    [Fact]
    public void Canonical_default_parses_back_to_the_same_value()
    {
        var first = Create(DataType.DateTime, defaultValue: "2026-01-31T09:30:00.5").Default!;

        Create(DataType.DateTime, defaultValue: first.Canonical).Default.ShouldBe(first);
    }

    // ---- Row size (limits verified against MySQL 9.2) -----------------------------------------------

    [Fact]
    public void Row_size_counts_the_system_id_and_null_bitmap()
    {
        // 8 (id) + 16381 * 4 + 2 (length prefix) + 1 (null bitmap) = MySQL's exact limit. Built directly
        // because Create() caps Varchar at 4000; only the byte arithmetic is under test.
        var wide = new ColumnDefinition(DataType.Varchar, 16381, null, null, IsNullable: true, IsUnique: false, Default: null);
        ColumnDefinitionRules.RowBytes([wide]).ShouldBe(65_535);
        ColumnDefinitionRules.RowBytes([Create(DataType.Int, isNullable: false)]).ShouldBe(12);
    }

    [Fact]
    public void Row_size_of_each_type_matches_mysql()
    {
        ColumnDefinition[] columns =
        [
            Create(DataType.Int), Create(DataType.BigInt), Create(DataType.Bool), Create(DataType.Date),
            Create(DataType.DateTime), Create(DataType.Text), Create(DataType.Json), Create(DataType.Uuid),
            Create(DataType.Decimal, precision: 10, scale: 2), Create(DataType.Varchar, length: 10),
        ];

        // id 8 + 4 + 8 + 1 + 3 + 8 + 10 + 12 + 144 + 5 + (40 + 1) + null bitmap 2 (10 nullable columns).
        ColumnDefinitionRules.RowBytes(columns).ShouldBe(246);
    }

    [Fact]
    public void Row_at_the_limit_fits_and_one_byte_more_is_rejected()
    {
        var columns = new List<ColumnDefinition>();
        for (var i = 0; i < 4; i++)
        {
            columns.Add(Create(DataType.Varchar, length: 4000));
        }

        // 8 + 4 * 16002 + 1 = 64,017. A Varchar(379) adds 1518 → 65,535 (bitmap still 1 byte).
        columns.Add(Create(DataType.Varchar, length: 379));
        ColumnDefinitionRules.RowBytes(columns).ShouldBe(65_535);
        Should.NotThrow(() => ColumnDefinitionRules.EnsureRowFits(columns));

        columns.Add(Create(DataType.Bool, isNullable: false));
        FieldOf(() => ColumnDefinitionRules.EnsureRowFits(columns)).ShouldBe("length");
    }

    private static ColumnDefinition Create(
        DataType type, int? length = null, int? precision = null, int? scale = null,
        bool isNullable = true, bool isUnique = false, string? defaultValue = null) =>
        ColumnDefinitionRules.Create(type, length, precision, scale, isNullable, isUnique, defaultValue);

    private static int? Precision(DataType type) => type == DataType.Decimal ? 10 : null;

    private static int? Scale(DataType type) => type == DataType.Decimal ? 2 : null;

    private static string? FieldOf(Action action) => Should.Throw<DomainException>(action).Field;
}
