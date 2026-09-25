using CoreFoundry.Domain.Schema;
using CoreFoundry.Domain.SchemaEngine;
using Shouldly;

namespace CoreFoundry.UnitTests.SchemaEngine;

public class ColumnTypeTests
{
    public static TheoryData<ColumnType> EveryType =>
    [
        new ColumnType(DataType.Int),
        new ColumnType(DataType.BigInt),
        new ColumnType(DataType.Decimal, null, 10, 2),
        new ColumnType(DataType.Decimal, null, 65, 30),
        new ColumnType(DataType.Decimal, null, 5, 0),
        new ColumnType(DataType.Bool),
        new ColumnType(DataType.Varchar, 200),
        new ColumnType(DataType.Varchar, 1),
        new ColumnType(DataType.Text),
        new ColumnType(DataType.DateTime),
        new ColumnType(DataType.Date),
        new ColumnType(DataType.Json),
        new ColumnType(DataType.Uuid),
    ];

    [Theory]
    [MemberData(nameof(EveryType))]
    public void Parsing_what_ToString_wrote_gives_the_same_type(ColumnType type)
    {
        ColumnType.TryParse(type.ToString(), out var parsed).ShouldBeTrue();
        parsed.ShouldBe(type);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("tinyint(4)")] // raw MySQL types kept for columns CoreFoundry didn't create
    [InlineData("mediumtext")]
    [InlineData("text")]
    [InlineData("int")]
    [InlineData("varchar(200)")]
    [InlineData("Varchar")]
    [InlineData("Varchar()")]
    [InlineData("Varchar(0)")]
    [InlineData("Varchar(-5)")]
    [InlineData("Varchar(0200)")]
    [InlineData("Varchar( 200)")]
    [InlineData("Varchar(200")]
    [InlineData("Varchar(200,2)")]
    [InlineData("Decimal")]
    [InlineData("Decimal(10)")]
    [InlineData("Decimal(10,11)")]
    [InlineData("Decimal(0,0)")]
    [InlineData("Decimal(300,2)")]
    [InlineData("Int(11)")]
    [InlineData("Int()")]
    [InlineData("5")]
    [InlineData("Unknown")]
    [InlineData("unknown")]
    public void Anything_else_is_not_a_column_type(string? text)
    {
        ColumnType.TryParse(text, out var parsed).ShouldBeFalse();
        parsed.ShouldBeNull();
    }
}
