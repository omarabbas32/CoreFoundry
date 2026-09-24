using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CoreFoundry.Domain.SchemaEngine;

namespace CoreFoundry.Application.SchemaEngine;

/// <summary>
/// The real schema right after an apply, stored as <c>SchemaMigrations.SnapshotJson</c>. Plain values
/// only, so it reads back the same years later. Used to spot drift (changes made outside CoreFoundry)
/// and draft changes not applied yet.
/// </summary>
public sealed record SchemaSnapshot(IReadOnlyList<TableSnapshot> Tables)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static SchemaSnapshot Empty { get; } = new([]);

    public static SchemaSnapshot From(SchemaModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return new([.. model.Tables.OrderBy(table => table.Name, StringComparer.Ordinal).Select(table => new TableSnapshot(
            table.Name,
            [.. table.Columns.Select(column => new ColumnSnapshot(
                column.Name,
                column.Type?.ToString() ?? column.RawType ?? "unknown",
                column.IsNullable,
                column.IsUnique,
                column.Default?.Canonical,
                column.Reference?.TargetTable,
                column.Reference?.OnDelete.ToString()))]))]);
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static SchemaSnapshot FromJson(string? json) =>
        string.IsNullOrWhiteSpace(json) ? Empty : JsonSerializer.Deserialize<SchemaSnapshot>(json, Json) ?? Empty;

    public TableSnapshot? FindTable(string name) => Tables.FirstOrDefault(table => table.Name == name);

    /// <summary>What differs from <paramref name="later"/>, as sentences (for drift).</summary>
    public IReadOnlyList<string> DifferencesTo(SchemaSnapshot later)
    {
        ArgumentNullException.ThrowIfNull(later);
        var differences = new List<string>();
        foreach (var table in Tables.Where(table => later.FindTable(table.Name) is null))
        {
            differences.Add($"Table {table.Name} was dropped or renamed outside CoreFoundry.");
        }

        foreach (var table in later.Tables)
        {
            if (FindTable(table.Name) is not { } before)
            {
                differences.Add($"Table {table.Name} was created outside CoreFoundry.");
                continue;
            }

            foreach (var column in before.Columns.Where(column => table.FindColumn(column.Name) is null))
            {
                differences.Add($"Column {table.Name}.{column.Name} was dropped or renamed outside CoreFoundry.");
            }

            foreach (var column in table.Columns)
            {
                var old = before.FindColumn(column.Name);
                if (old is null)
                {
                    differences.Add($"Column {table.Name}.{column.Name} was added outside CoreFoundry.");
                }
                else if (old != column)
                {
                    differences.Add($"Column {table.Name}.{column.Name} was changed outside CoreFoundry: {old.Describe()} → {column.Describe()}.");
                }
            }
        }

        return differences;
    }
}

public sealed record TableSnapshot(string Name, IReadOnlyList<ColumnSnapshot> Columns)
{
    public ColumnSnapshot? FindColumn(string name) => Columns.FirstOrDefault(column => column.Name == name);
}

/// <param name="Type">E.g. <c>Varchar(200)</c>, or MySQL's raw type for types CoreFoundry doesn't create.</param>
/// <param name="References">The referenced table's physical name, or null.</param>
public sealed record ColumnSnapshot(
    string Name, string Type, bool IsNullable, bool IsUnique, string? Default, string? References, string? OnDelete)
{
    public string Describe() =>
        $"{Type}{(IsNullable ? "" : " not null")}{(IsUnique ? " unique" : "")}" +
        $"{(Default is null ? "" : $" default {Default}")}{(References is null ? "" : $" → {References} ({OnDelete})")}";
}

/// <summary>What the user reviewed is what runs: the apply refuses a plan whose hash changed.</summary>
public static class PlanHash
{
    /// <summary>Hex SHA-256 of the schema version and the statements, one per line.</summary>
    public static string Compute(int schemaVersion, IReadOnlyList<string> statements)
    {
        ArgumentNullException.ThrowIfNull(statements);
        var text = $"{schemaVersion}\n{string.Join("\n", statements)}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}
