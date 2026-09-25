using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CoreFoundry.Application.Common;
using CoreFoundry.Domain.Schema;

namespace CoreFoundry.Application.Data;

/// <summary>A value for one column. <see cref="UseDefault"/>: write the column's default (<c>DEFAULT</c>).</summary>
/// <param name="Value">
/// The typed value: <see cref="int"/>, <see cref="long"/>, <see cref="bool"/>, <see cref="string"/> (Varchar, Text,
/// Decimal as its exact digits, Uuid in canonical form, Json as its text), <see cref="DateOnly"/>,
/// <see cref="DateTime"/>; or null for SQL NULL.
/// </param>
public sealed record ColumnValue(DataColumn Column, object? Value, bool UseDefault = false);

/// <summary>
/// Turns a JSON request body into typed column values, checked against the applied table. Pure.
/// Every problem is collected and reported together, keyed by field name.
/// </summary>
public static partial class RowCoercer
{
    /// <summary>MySQL's TEXT limit, in bytes.</summary>
    public const int TextMaxBytes = 65_535;

    private const string BodyKey = "";

    /// <summary>
    /// Values for an insert (<paramref name="replace"/> false: fields left out get NULL or their
    /// default from MySQL) or for a full replace (true: fields left out are set to their default,
    /// or NULL). Columns whose type CoreFoundry doesn't know are read-only and never written.
    /// </summary>
    /// <exception cref="ValidationFailedException">Anything is wrong; all errors at once.</exception>
    public static IReadOnlyList<ColumnValue> Coerce(DataTable table, JsonElement body, bool replace)
    {
        ArgumentNullException.ThrowIfNull(table);
        var errors = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        void Fail(string field, string message)
        {
            if (!errors.TryGetValue(field, out var list))
            {
                errors[field] = list = [];
            }

            list.Add(message);
        }

        if (body.ValueKind != JsonValueKind.Object)
        {
            throw new ValidationFailedException(BodyKey, "The body must be a JSON object of column values.");
        }

        var given = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in body.EnumerateObject())
        {
            if (property.Name == "id")
            {
                Fail("id", "The id is set by the database; leave it out.");
            }
            else if (!given.TryAdd(property.Name, property.Value))
            {
                Fail(property.Name, "Given more than once.");
            }
            else if (table.FindColumn(property.Name) is not { } column)
            {
                Fail(property.Name, $"Table {table.Name} has no column {property.Name} (only applied columns can be written).");
            }
            else if (!column.IsWritable)
            {
                Fail(property.Name, $"This column's type ({column.RawType}) was changed outside CoreFoundry, so it is read-only.");
            }
        }

        var values = new List<ColumnValue>();
        foreach (var column in table.Columns.Where(column => column.IsWritable))
        {
            if (given.TryGetValue(column.Name, out var element))
            {
                if (TryConvert(column, element, out var value, out var error))
                {
                    values.Add(new ColumnValue(column, value));
                }
                else
                {
                    Fail(column.Name, error);
                }
            }
            else if (!column.IsOptionalOnInsert)
            {
                Fail(column.Name, "Required.");
            }
            else if (replace)
            {
                values.Add(column.Default is not null ? new ColumnValue(column, null, UseDefault: true) : new ColumnValue(column, null));
            }
        }

        if (errors.Count > 0)
        {
            throw new ValidationFailedException(errors.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal));
        }

        return values;
    }

    /// <summary>One JSON value → the column's typed value, or the reason it doesn't fit.</summary>
    public static bool TryConvert(DataColumn column, JsonElement element, out object? value, out string error)
    {
        ArgumentNullException.ThrowIfNull(column);
        value = null;
        error = string.Empty;
        var type = column.Type ?? throw new InvalidOperationException($"Column {column.Name} is read-only.");

        if (element.ValueKind == JsonValueKind.Null)
        {
            error = column.IsNullable ? string.Empty : "Can't be null.";
            return column.IsNullable;
        }

        (value, error) = type.DataType switch
        {
            DataType.Int => element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var number)
                ? (number, "")
                : (null, $"Must be a whole number from {int.MinValue} to {int.MaxValue}."),
            DataType.BigInt => element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var number)
                ? (number, "")
                : (null, column.References is null
                    ? $"Must be a whole number from {long.MinValue} to {long.MaxValue}."
                    : $"Must be the id of a row in {column.References}."),
            DataType.Decimal => ToDecimal(element, type.Precision!.Value, type.Scale!.Value),
            DataType.Bool => element.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? (element.GetBoolean(), "")
                : (null, "Must be true or false."),
            DataType.Varchar => element.ValueKind != JsonValueKind.String
                ? (null, "Must be a string.")
                : CountCharacters(element.GetString()!) is var length && length > type.Length
                    ? (null, $"Must be at most {type.Length} characters (got {length}).")
                    : (element.GetString(), ""),
            DataType.Text => element.ValueKind != JsonValueKind.String
                ? (null, "Must be a string.")
                : Encoding.UTF8.GetByteCount(element.GetString()!) > TextMaxBytes
                    ? (null, $"Must be at most {TextMaxBytes:N0} bytes as UTF-8.")
                    : (element.GetString(), ""),
            DataType.Date => ToDate(element),
            DataType.DateTime => ToDateTime(element),
            DataType.Uuid => element.ValueKind == JsonValueKind.String
                && Guid.TryParseExact(element.GetString(), "D", out var uuid)
                ? (uuid.ToString("D"), "")
                : (null, "Must be a UUID like 3f2504e0-4f89-11d3-9a0c-0305e82c3301."),
            DataType.Json => (element.GetRawText(), ""),
            _ => (null, $"Type {type} isn't supported."),
        };
        return error.Length == 0;
    }

    /// <summary>MySQL counts VARCHAR length in characters (code points), not UTF-16 units.</summary>
    private static int CountCharacters(string text) => text.EnumerateRunes().Count();

    /// <summary>Exact digits only: no exponent and no rounding. Returned as text so no precision is lost.</summary>
    private static (object?, string) ToDecimal(JsonElement element, int precision, int scale)
    {
        var text = element.ValueKind switch
        {
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.String => element.GetString()!,
            _ => null,
        };
        var shape = $"Must be a number with at most {precision - scale} digits before the point and {scale} after it.";
        if (text is null || DecimalPattern().Match(text) is not { Success: true } match)
        {
            return (null, shape);
        }

        var integer = match.Groups["int"].Value.TrimStart('0');
        var fraction = match.Groups["frac"].Value.TrimEnd('0');
        if (fraction.Length > scale)
        {
            return (null, $"Must have at most {scale} decimals.");
        }

        if (integer.Length > precision - scale)
        {
            return (null, shape);
        }

        var negative = match.Groups["sign"].Value == "-" && (integer.Length > 0 || fraction.Length > 0);
        var digits = (integer.Length == 0 ? "0" : integer) + (fraction.Length == 0 ? "" : "." + fraction);
        return ((negative ? "-" : "") + digits, "");
    }

    private static (object?, string) ToDate(JsonElement element) =>
        element.ValueKind == JsonValueKind.String
        && DateOnly.TryParseExact(element.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.Year >= 1000 ? (date, "") : (null, "Must be from 1000-01-01 to 9999-12-31.")
            : (null, "Must be a date like 2024-02-29 (yyyy-MM-dd).");

    /// <summary>ISO-8601, up to microseconds (the column's precision). A value with an offset is converted to UTC.</summary>
    private static (object?, string) ToDateTime(JsonElement element)
    {
        const string Shape = "Must be a date and time like 2024-02-29T13:45:00, optionally with fractions (up to 6 digits) and Z or an offset.";
        if (element.ValueKind != JsonValueKind.String || element.GetString() is not { } text || !DateTimePattern().IsMatch(text))
        {
            return (null, Shape);
        }

        DateTime value;
        if (text.EndsWith('Z') || text.Length > 6 && text[^6] is '+' or '-')
        {
            if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var withOffset))
            {
                return (null, Shape);
            }

            value = withOffset.UtcDateTime;
        }
        else if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out value))
        {
            return (null, Shape);
        }

        value = DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
        return value.Year >= 1000 ? (value, "") : (null, "Must be from year 1000 to 9999.");
    }

    // [0-9], not \d: \d also matches non-ASCII digits such as '٣'.
    [GeneratedRegex(@"^(?<sign>-)?(?<int>[0-9]+)(\.(?<frac>[0-9]+))?$", RegexOptions.CultureInvariant)]
    private static partial Regex DecimalPattern();

    [GeneratedRegex(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}(:[0-9]{2}(\.[0-9]{1,6})?)?(Z|[+-][0-9]{2}:[0-9]{2})?$", RegexOptions.CultureInvariant)]
    private static partial Regex DateTimePattern();
}
