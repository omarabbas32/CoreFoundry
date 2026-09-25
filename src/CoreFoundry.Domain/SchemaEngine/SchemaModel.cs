using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using CoreFoundry.Domain.Schema;

namespace CoreFoundry.Domain.SchemaEngine;

/// <summary>
/// A database schema: either the <b>desired</b> one (read from the draft metadata) or the
/// <b>actual</b> one (introspected from <c>INFORMATION_SCHEMA</c>). The system <c>id</c> column is
/// never part of the model: every table has it.
/// </summary>
public sealed record SchemaModel(IReadOnlyList<TableModel> Tables)
{
    public static SchemaModel Empty { get; } = new([]);
}

/// <param name="Name">Desired name (draft) or physical name (actual).</param>
/// <param name="MetadataId">Draft only: the <c>ProjectTables</c> id.</param>
/// <param name="AppliedName">Draft only: the physical name last applied, or null if never applied.</param>
public sealed record TableModel(
    string Name,
    IReadOnlyList<ColumnModel> Columns,
    long? MetadataId = null,
    string? AppliedName = null,
    bool PendingDrop = false);

/// <param name="Type">Null when an introspected column has a type CoreFoundry doesn't create (see <paramref name="RawType"/>).</param>
/// <param name="UniqueIndexName">Actual only: the single-column unique index on this column, if any.</param>
public sealed record ColumnModel(
    string Name,
    ColumnType? Type,
    bool IsNullable,
    bool IsUnique,
    ColumnDefault? Default,
    ForeignKeyModel? Reference = null,
    long? MetadataId = null,
    string? AppliedName = null,
    bool PendingDrop = false,
    string? RawType = null,
    string? UniqueIndexName = null);

/// <summary>A reference to another table's <c>id</c>.</summary>
/// <param name="TargetTable">Draft: the target's desired name. Actual: the referenced table's physical name.</param>
/// <param name="TargetMetadataId">Draft only: the target's <c>ProjectTables</c> id.</param>
/// <param name="ConstraintName">Actual only: the constraint's name.</param>
public sealed record ForeignKeyModel(
    string TargetTable,
    ReferenceAction OnDelete,
    long? TargetMetadataId = null,
    string? ConstraintName = null);

/// <summary>A <see cref="DataType"/> with its parameters.</summary>
public sealed record ColumnType(DataType DataType, int? Length = null, byte? Precision = null, byte? Scale = null)
{
    public override string ToString() => DataType switch
    {
        DataType.Varchar => $"Varchar({Length})",
        DataType.Decimal => $"Decimal({Precision},{Scale})",
        _ => DataType.ToString(),
    };

    /// <summary>
    /// The inverse of <see cref="ToString"/> (<c>Varchar(200)</c>, <c>Decimal(10,2)</c>, <c>Date</c> …).
    /// Anything else, such as a raw MySQL type kept in a snapshot for a column CoreFoundry didn't
    /// create, isn't a <see cref="ColumnType"/>. Case-sensitive, like <see cref="ToString"/>.
    /// </summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out ColumnType? type)
    {
        type = null;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var open = text.IndexOf('(', StringComparison.Ordinal);
        var name = open < 0 ? text : text[..open];
        if (name.Length == 0 || !char.IsAsciiLetterUpper(name[0]) || !Enum.TryParse<DataType>(name, out var dataType)
            || !Enum.IsDefined(dataType) || dataType.ToString() != name)
        {
            return false;
        }

        string[] arguments = [];
        if (open >= 0)
        {
            if (!text.EndsWith(')'))
            {
                return false;
            }

            arguments = text[(open + 1)..^1].Split(',');
        }

        type = (dataType, arguments) switch
        {
            (DataType.Varchar, [var length]) when TryNumber(length, out var n) && n > 0 => new ColumnType(dataType, n),
            (DataType.Decimal, [var precision, var scale])
                when TryNumber(precision, out var p) && TryNumber(scale, out var s) && p is > 0 and <= byte.MaxValue && s <= p =>
                new ColumnType(dataType, null, (byte)p, (byte)s),
            (not DataType.Varchar and not DataType.Decimal, []) => new ColumnType(dataType),
            _ => null,
        };

        // Only the exact form ToString writes (no "Varchar(0200)").
        if (type?.ToString() != text)
        {
            type = null;
        }

        return type is not null;
    }

    private static bool TryNumber(string text, out int value) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    /// <summary>
    /// MySQL stores some defaults in a normalized form (a DECIMAL(10,2) default of 7.5 reads back as
    /// 7.50). Normalizing the desired default the same way keeps equal defaults equal.
    /// </summary>
    public ColumnDefault? Normalize(ColumnDefault? value) =>
        value is ColumnDefault.DecimalValue decimalValue && Scale is byte scale
            ? new ColumnDefault.DecimalValue(PadScale(decimalValue.Value, scale))
            : value;

    private static string PadScale(string value, int scale)
    {
        var point = value.IndexOf('.', StringComparison.Ordinal);
        var fraction = point < 0 ? string.Empty : value[(point + 1)..];
        var integer = point < 0 ? value : value[..point];
        return scale == 0
            ? integer
            : string.Create(CultureInfo.InvariantCulture, $"{integer}.{fraction.PadRight(scale, '0')}");
    }
}
