using CoreFoundry.Domain.Schema;

namespace CoreFoundry.Application.Templates;

/// <summary>
/// A ready schema a project can start from: draft tables (created through the same rules as the
/// designer) and optional sample rows inserted after the first apply.
/// </summary>
/// <param name="Key">Stable id used by the API, e.g. <c>ecommerce</c>.</param>
public sealed record SchemaTemplate(
    string Key, string Name, string Description, IReadOnlyList<TemplateTable> Tables, IReadOnlyList<SampleRow> SampleRows)
{
    public TemplateTable? FindTable(string name) => Tables.FirstOrDefault(table => table.Name == name);
}

public sealed record TemplateTable(string Name, IReadOnlyList<TemplateColumn> Columns);

/// <param name="References">Another table of the same template (or this one), or null.</param>
public sealed record TemplateColumn(
    string Name,
    DataType Type,
    int? Length = null,
    int? Precision = null,
    int? Scale = null,
    bool Nullable = false,
    bool Unique = false,
    string? Default = null,
    string? References = null,
    ReferenceAction? OnDelete = null);

/// <summary>
/// One sample row. A string value starting with <c>@</c> in a reference column is the <see cref="Key"/>
/// of a row of the referenced table, replaced by that row's real id when inserting.
/// </summary>
public sealed record SampleRow(string Table, string Key, IReadOnlyDictionary<string, object?> Values)
{
    public const char ReferencePrefix = '@';
}

/// <summary>Every template CoreFoundry ships.</summary>
public static class SchemaTemplates
{
    public static IReadOnlyList<SchemaTemplate> All { get; } = [EcommerceTemplate.Create()];

    public static SchemaTemplate? Find(string key) => All.FirstOrDefault(template => template.Key == key);
}
