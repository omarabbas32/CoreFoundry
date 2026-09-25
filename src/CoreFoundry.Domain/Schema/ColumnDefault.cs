using System.Globalization;
using System.Text.RegularExpressions;
using CoreFoundry.Domain.Common;

namespace CoreFoundry.Domain.Schema;

/// <summary>
/// A column's default value, parsed and checked against its type. The schema engine renders SQL from
/// this typed value, never from the text the user typed. <see cref="Canonical"/> is the stored form.
/// </summary>
public abstract partial record ColumnDefault
{
    public const string CurrentTimestampText = "CURRENT_TIMESTAMP";
    public const string GeneratedUuidText = "UUID()";

    private const string Field = "defaultValue";

    private static readonly DateTime MinDateTime = new(1000, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
    private static readonly DateOnly MinDate = new(1000, 1, 1);

    private ColumnDefault() { }

    /// <summary>The normalized text stored in <c>ProjectColumns.DefaultValue</c>; parsing it again gives the same value.</summary>
    public abstract string Canonical { get; }

    public sealed override string ToString() => Canonical;

    /// <summary>Int or BigInt.</summary>
    public sealed record IntegerValue(long Value) : ColumnDefault
    {
        public override string Canonical => Value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// A Decimal as text (<c>-12.50</c>): DECIMAL(65,30) doesn't fit .NET's <see cref="decimal"/>.
    /// Always has a leading digit; the fraction keeps the digits the user typed.
    /// </summary>
    public sealed record DecimalValue(string Value) : ColumnDefault
    {
        public override string Canonical => Value;
    }

    public sealed record BooleanValue(bool Value) : ColumnDefault
    {
        public override string Canonical => Value ? "true" : "false";
    }

    /// <summary>A Varchar literal, exactly as typed (not trimmed).</summary>
    public sealed record StringValue(string Value) : ColumnDefault
    {
        public override string Canonical => Value;
    }

    /// <summary>A DATETIME(6) wall-clock value (no time zone, like the column itself).</summary>
    public sealed record DateTimeValue(DateTime Value) : ColumnDefault
    {
        public override string Canonical => Value.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFF", CultureInfo.InvariantCulture);
    }

    public sealed record DateValue(DateOnly Value) : ColumnDefault
    {
        public override string Canonical => Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    public sealed record UuidValue(Guid Value) : ColumnDefault
    {
        public override string Canonical => Value.ToString("D", CultureInfo.InvariantCulture);
    }

    /// <summary>DateTime default of the current time.</summary>
    public sealed record CurrentTimestamp : ColumnDefault
    {
        public override string Canonical => CurrentTimestampText;
    }

    /// <summary>Uuid default of a new random UUID per row.</summary>
    public sealed record GeneratedUuid : ColumnDefault
    {
        public override string Canonical => GeneratedUuidText;
    }

    /// <summary>
    /// Parses <paramref name="text"/> as a default for a column of <paramref name="dataType"/>.
    /// Null or empty means "no default". Length/precision/scale must already be valid for the type.
    /// </summary>
    /// <exception cref="DomainException">The value doesn't fit the type (<see cref="DomainException.Field"/> is <c>defaultValue</c>).</exception>
    public static ColumnDefault? Parse(DataType dataType, string? text, int? length, int? precision, int? scale)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (text.Length > ProjectColumn.DefaultValueMaxLength)
        {
            throw Invalid($"The default must be at most {ProjectColumn.DefaultValueMaxLength} characters.");
        }

        // Only Varchar keeps surrounding spaces: they are part of the string.
        var trimmed = text.Trim();
        return dataType switch
        {
            DataType.Int => ParseInteger(trimmed, int.MinValue, int.MaxValue, "Int"),
            DataType.BigInt => ParseInteger(trimmed, long.MinValue, long.MaxValue, "BigInt"),
            DataType.Decimal => ParseDecimal(trimmed, precision!.Value, scale!.Value),
            DataType.Bool => ParseBoolean(trimmed),
            DataType.Varchar => ParseString(text, length!.Value),
            DataType.DateTime => ParseDateTime(trimmed),
            DataType.Date => ParseDate(trimmed),
            DataType.Uuid => ParseUuid(trimmed),
            DataType.Text or DataType.Json => throw Invalid($"{dataType} columns can't have a default."),
            _ => throw Invalid("Unknown data type."),
        };
    }

    private static IntegerValue ParseInteger(string text, long min, long max, string typeName)
    {
        if (!IntegerPattern().IsMatch(text))
        {
            throw Invalid($"The default for a {typeName} column must be a whole number.");
        }

        return long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
            && value >= min && value <= max
            ? new IntegerValue(value)
            : throw Invalid($"The default for a {typeName} column must be between {min} and {max}.");
    }

    private static DecimalValue ParseDecimal(string text, int precision, int scale)
    {
        var match = DecimalPattern().Match(text);
        if (!match.Success)
        {
            throw Invalid("The default for a Decimal column must be a number such as 12.50.");
        }

        var integerDigits = match.Groups["int"].Value.TrimStart('0');
        var fractionDigits = match.Groups["frac"].Value;
        if (integerDigits.Length > precision - scale)
        {
            throw Invalid($"The default has too many digits before the decimal point for DECIMAL({precision},{scale}).");
        }

        if (fractionDigits.Length > scale)
        {
            throw Invalid($"The default has more than {scale} digits after the decimal point.");
        }

        var isZero = integerDigits.Length == 0 && fractionDigits.All(digit => digit == '0');
        var sign = match.Groups["sign"].Value == "-" && !isZero ? "-" : string.Empty;
        var value = sign + (integerDigits.Length == 0 ? "0" : integerDigits)
            + (fractionDigits.Length == 0 ? string.Empty : "." + fractionDigits);
        return new DecimalValue(value);
    }

    private static BooleanValue ParseBoolean(string text) => text.ToUpperInvariant() switch
    {
        "TRUE" => new BooleanValue(true),
        "FALSE" => new BooleanValue(false),
        _ => throw Invalid("The default for a Bool column must be true or false."),
    };

    private static StringValue ParseString(string text, int length) =>
        text.EnumerateRunes().Count() <= length
            ? new StringValue(text)
            : throw Invalid($"The default is longer than the column's length ({length}).");

    private static ColumnDefault ParseDateTime(string text)
    {
        if (string.Equals(text, CurrentTimestampText, StringComparison.OrdinalIgnoreCase))
        {
            return new CurrentTimestamp();
        }

        string[] formats = ["yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFFFFF", "yyyy-MM-dd'T'HH:mm"];
        return DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
            && value >= MinDateTime
            ? new DateTimeValue(DateTime.SpecifyKind(value, DateTimeKind.Unspecified))
            : throw Invalid(
                $"The default for a DateTime column must be {CurrentTimestampText} or a date and time " +
                "such as 2026-01-31T09:30:00 (year 1000 or later, no time zone).");
    }

    private static DateValue ParseDate(string text) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
            && value >= MinDate
            ? new DateValue(value)
            : throw Invalid("The default for a Date column must be a date such as 2026-01-31 (year 1000 or later).");

    private static ColumnDefault ParseUuid(string text)
    {
        if (string.Equals(text, GeneratedUuidText, StringComparison.OrdinalIgnoreCase))
        {
            return new GeneratedUuid();
        }

        return Guid.TryParseExact(text, "D", out var value)
            ? new UuidValue(value)
            : throw Invalid(
                $"The default for a Uuid column must be {GeneratedUuidText} or a UUID such as 3f2504e0-4f89-11d3-9a0c-0305e82c3301.");
    }

    private static DomainException Invalid(string message) => new(message) { Field = Field };

    [GeneratedRegex(@"^[+-]?[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex IntegerPattern();

    [GeneratedRegex(@"^(?<sign>[+-]?)(?<int>[0-9]+)(\.(?<frac>[0-9]+))?$", RegexOptions.CultureInvariant)]
    private static partial Regex DecimalPattern();
}
