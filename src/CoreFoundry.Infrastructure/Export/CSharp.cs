using System.Globalization;
using System.Text;
using CoreFoundry.Application.Export;
using CoreFoundry.Domain.Schema;
using CoreFoundry.Domain.SchemaEngine;

namespace CoreFoundry.Infrastructure.Export;

/// <summary>C# spellings of types, literals and defaults for the generated code.</summary>
internal static class CSharp
{
    public static string ClrType(ColumnType type) => type.DataType switch
    {
        DataType.Int => "int",
        DataType.BigInt => "long",
        DataType.Decimal => "decimal",
        DataType.Bool => "bool",
        DataType.Varchar or DataType.Text or DataType.Json => "string",
        DataType.DateTime => "DateTime",
        DataType.Date => "DateOnly",
        DataType.Uuid => "Guid",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown type."),
    };

    /// <summary>
    /// The entity property may be null in C#: when the column is nullable, or when it has a database
    /// default (null means "not set", so EF leaves it out of the INSERT and MySQL fills in the default).
    /// </summary>
    public static bool EntityNullable(ExportProperty property) => property.IsNullable || property.Default is not null;

    public static string EntityType(ExportProperty property) =>
        ClrType(property.Type) + (EntityNullable(property) ? "?" : "");

    /// <summary>Json columns are exchanged as JSON values, not as text.</summary>
    public static string DtoType(ExportProperty property) =>
        (property.Type.DataType == DataType.Json ? "JsonElement" : ClrType(property.Type)) + (property.IsNullable ? "?" : "");

    /// <summary>Every input field is nullable, so "left out" can be told apart from a value.</summary>
    public static string InputType(ExportProperty property) =>
        (property.Type.DataType == DataType.Json ? "JsonElement" : ClrType(property.Type)) + "?";

    public static bool IsRequiredOnInsert(ExportProperty property) => !property.IsNullable && property.Default is null;

    /// <summary>A C# string literal.</summary>
    public static string String(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var builder = new StringBuilder("\"", value.Length + 2);
        foreach (var character in value)
        {
            builder.Append(character switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ when char.IsControl(character) || char.IsSurrogate(character) || character > '~' =>
                    $"\\u{(int)character:x4}",
                _ => character.ToString(),
            });
        }

        return builder.Append('"').ToString();
    }

    /// <summary>The column's default as a C# expression of its type (used when a full replace leaves the field out).</summary>
    public static string DefaultValue(ColumnDefault value, ColumnType type) => value switch
    {
        ColumnDefault.IntegerValue integer => type.DataType == DataType.BigInt
            ? integer.Value.ToString(CultureInfo.InvariantCulture) + "L"
            : integer.Value.ToString(CultureInfo.InvariantCulture),
        ColumnDefault.DecimalValue number => number.Value + "m",
        ColumnDefault.BooleanValue boolean => boolean.Value ? "true" : "false",
        ColumnDefault.StringValue text => String(text.Value),
        ColumnDefault.DateTimeValue dateTime =>
            $"DateTime.Parse({String(dateTime.Canonical)}, CultureInfo.InvariantCulture)",
        ColumnDefault.DateValue date => $"new DateOnly({date.Value.Year}, {date.Value.Month}, {date.Value.Day})",
        ColumnDefault.UuidValue uuid => $"Guid.Parse({String(uuid.Canonical)})",
        ColumnDefault.CurrentTimestamp => "DateTime.UtcNow",
        ColumnDefault.GeneratedUuid => "Guid.NewGuid()",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown default."),
    };

    /// <summary>JSON property name for a column: the column's own name, so the API matches the database.</summary>
    public static string JsonName(ExportProperty property) => property.Column;

    /// <summary>The blocks with an empty line between each two.</summary>
    public static IEnumerable<string> Separated(IEnumerable<string> blocks)
    {
        var first = true;
        foreach (var block in blocks)
        {
            if (!first)
            {
                yield return string.Empty;
            }

            first = false;
            yield return block;
        }
    }

    /// <summary>Joins lines, each indented by <paramref name="spaces"/>; no trailing newline.</summary>
    public static string Lines(IEnumerable<string> lines, int spaces)
    {
        var indent = new string(' ', spaces);
        return string.Join("\n", lines.SelectMany(line => line.Split('\n')).Select(line => line.Length == 0 ? line : indent + line));
    }
}
