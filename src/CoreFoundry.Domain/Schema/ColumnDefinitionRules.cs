using CoreFoundry.Domain.Common;

namespace CoreFoundry.Domain.Schema;

/// <summary>Everything about a column except its name and position. Only <see cref="ColumnDefinitionRules.Create"/> builds valid ones.</summary>
public sealed record ColumnDefinition(
    DataType DataType,
    int? Length,
    byte? Precision,
    byte? Scale,
    bool IsNullable,
    bool IsUnique,
    ColumnDefault? Default);

/// <summary>Per-project and per-table caps (also stated in the README).</summary>
public static class SchemaLimits
{
    public const int MaxTablesPerProject = 50;
    public const int MaxColumnsPerTable = 100;
}

/// <summary>
/// Which length / precision / scale / default each <see cref="DataType"/> accepts, and MySQL's row-size
/// and index-size limits. Errors carry the field they are about in <see cref="DomainException.Field"/>.
/// </summary>
public static class ColumnDefinitionRules
{
    public const int MinVarcharLength = 1;
    public const int MaxVarcharLength = 4000;
    public const int MinPrecision = 1;
    public const int MaxPrecision = 65;
    public const int MaxScale = 30;

    /// <summary>MySQL's row-size limit, not counting TEXT/JSON contents.</summary>
    public const int MaxRowBytes = 65_535;

    /// <summary>Longest Varchar that can be UNIQUE: InnoDB keys are at most 3072 bytes, 4 bytes per utf8mb4 character.</summary>
    public const int MaxUniqueVarcharLength = 768;

    /// <summary>utf8mb4 needs up to 4 bytes per character.</summary>
    private const int BytesPerCharacter = 4;

    /// <summary>Bytes of the system <c>id BIGINT</c> primary key every table has.</summary>
    private const int SystemIdBytes = 8;

    /// <summary>Validates the parts of a column definition and parses its default.</summary>
    /// <exception cref="DomainException">A rule is broken; <see cref="DomainException.Field"/> names the input.</exception>
    public static ColumnDefinition Create(
        DataType dataType, int? length, int? precision, int? scale, bool isNullable, bool isUnique, string? defaultValue)
    {
        if (!Enum.IsDefined(dataType))
        {
            throw Invalid("dataType", "Unknown data type.");
        }

        ValidateLength(dataType, length);
        ValidatePrecisionAndScale(dataType, precision, scale);

        if (isUnique && dataType is DataType.Text or DataType.Json)
        {
            throw Invalid("isUnique", $"{dataType} columns can't be unique.");
        }

        if (isUnique && dataType == DataType.Varchar && length > MaxUniqueVarcharLength)
        {
            throw Invalid("isUnique", $"A unique Varchar column can be at most {MaxUniqueVarcharLength} characters long.");
        }

        var parsedDefault = ColumnDefault.Parse(dataType, defaultValue, length, precision, scale);
        return new ColumnDefinition(dataType, length, (byte?)precision, (byte?)scale, isNullable, isUnique, parsedDefault);
    }

    /// <summary>
    /// The row size MySQL computes for a table with these columns plus the system <c>id</c>, using the
    /// types the schema engine creates (VARCHAR(n) utf8mb4, DECIMAL(p,s), DATETIME(6), CHAR(36) …).
    /// Verified against MySQL at the 65,535-byte boundary for every type.
    /// </summary>
    public static int RowBytes(IEnumerable<ColumnDefinition> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);

        var bytes = SystemIdBytes;
        var nullable = 0;
        foreach (var column in columns)
        {
            bytes += ColumnBytes(column);
            if (column.IsNullable)
            {
                nullable++;
            }
        }

        // One NULL-flag bit per nullable column, rounded up to whole bytes.
        return bytes + ((nullable + 7) / 8);
    }

    /// <exception cref="DomainException">The row would be over <see cref="MaxRowBytes"/>.</exception>
    public static void EnsureRowFits(IEnumerable<ColumnDefinition> columns)
    {
        var bytes = RowBytes(columns);
        if (bytes > MaxRowBytes)
        {
            throw new DomainException(
                $"The table's columns need {bytes:N0} bytes per row, over MySQL's limit of {MaxRowBytes:N0}. " +
                "Shorten some Varchar columns or change them to Text.")
            {
                Field = "length",
            };
        }
    }

    private static void ValidateLength(DataType dataType, int? length)
    {
        if (dataType != DataType.Varchar)
        {
            if (length is not null)
            {
                throw Invalid("length", $"{dataType} columns don't have a length.");
            }

            return;
        }

        if (length is not (>= MinVarcharLength and <= MaxVarcharLength))
        {
            throw Invalid("length", $"Varchar length must be between {MinVarcharLength} and {MaxVarcharLength}.");
        }
    }

    private static void ValidatePrecisionAndScale(DataType dataType, int? precision, int? scale)
    {
        if (dataType != DataType.Decimal)
        {
            if (precision is not null)
            {
                throw Invalid("precision", $"{dataType} columns don't have a precision.");
            }

            if (scale is not null)
            {
                throw Invalid("scale", $"{dataType} columns don't have a scale.");
            }

            return;
        }

        if (precision is not (>= MinPrecision and <= MaxPrecision))
        {
            throw Invalid("precision", $"Decimal precision must be between {MinPrecision} and {MaxPrecision}.");
        }

        if (scale is not (>= 0 and <= MaxScale))
        {
            throw Invalid("scale", $"Decimal scale must be between 0 and {MaxScale}.");
        }

        if (scale > precision)
        {
            throw Invalid("scale", "Decimal scale can't be larger than its precision.");
        }
    }

    private static int ColumnBytes(ColumnDefinition column) => column.DataType switch
    {
        DataType.Int => 4,
        DataType.BigInt => 8,
        DataType.Decimal => DecimalBytes(column.Precision!.Value - column.Scale!.Value) + DecimalBytes(column.Scale!.Value),
        DataType.Bool => 1,
        DataType.Varchar => VarcharBytes(column.Length!.Value),
        DataType.Text => 10, // 2-byte length + 8-byte pointer; the contents are stored off-row.
        DataType.DateTime => 8, // DATETIME(6): 5 bytes + 3 for the microseconds.
        DataType.Date => 3,
        DataType.Json => 12, // Stored like LONGBLOB: 4-byte length + 8-byte pointer.
        DataType.Uuid => 36 * BytesPerCharacter, // CHAR(36) counts at its utf8mb4 maximum.
        _ => throw new ArgumentOutOfRangeException(nameof(column), column.DataType, "Unknown data type."),
    };

    private static int VarcharBytes(int length)
    {
        var maxBytes = length * BytesPerCharacter;
        return maxBytes + (maxBytes > 255 ? 2 : 1);
    }

    /// <summary>MySQL packs each 9 decimal digits into 4 bytes; leftover digits use 0–4 bytes.</summary>
    private static int DecimalBytes(int digits)
    {
        ReadOnlySpan<int> leftover = [0, 1, 1, 2, 2, 3, 3, 4, 4, 4];
        return (digits / 9 * 4) + leftover[digits % 9];
    }

    private static DomainException Invalid(string field, string message) => new(message) { Field = field };
}
