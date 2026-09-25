using CoreFoundry.Application.Common;
using CoreFoundry.Application.Data;
using CoreFoundry.Domain.Schema;
using CoreFoundry.Domain.SchemaEngine;

namespace CoreFoundry.Application.Export;

/// <summary>
/// Everything the code generator needs, with every C# name decided up front. Built from the applied
/// schema (<see cref="DataSchema"/>), so the exported code matches the database that is running.
/// </summary>
/// <param name="Solution">Solution name and root namespace, e.g. <c>Bookshop</c>.</param>
/// <param name="ProjectName">The project's display name (for the README).</param>
public sealed record ExportModel(string Solution, string ProjectName, int SchemaVersion, IReadOnlyList<ExportEntity> Entities)
{
    /// <summary>File-name friendly form of <see cref="Solution"/>: <c>bookshop</c>.</summary>
    public string Slug => Solution.ToLowerInvariant();

    public ExportEntity Entity(string table) => Entities.Single(entity => entity.Table == table);

    /// <exception cref="ConflictException">A column's type was changed outside CoreFoundry, or a reference points outside the export.</exception>
    public static ExportModel From(string projectName, DataSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var solution = CodeNames.Solution(projectName);

        var unknown = schema.Tables.SelectMany(table => table.Columns.Where(column => column.Type is null).Select(column => $"{table.Name}.{column.Name} ({column.RawType})")).ToList();
        if (unknown.Count > 0)
        {
            throw new ConflictException(
                $"These columns have types CoreFoundry doesn't create (they were changed outside it): {string.Join(", ", unknown)}. Fix the drift, then export.");
        }

        // Class names first (references need their targets'), each unique across everything the generator emits.
        var taken = new HashSet<string>(CodeNames.Reserved, StringComparer.Ordinal) { solution };
        var classes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var table in schema.Tables)
        {
            var singular = CodeNames.Pascal(CodeNames.Singular(table.Name));
            var name = taken.Contains(singular) || GeneratedFor(singular).Any(taken.Contains) ? CodeNames.Pascal(table.Name) : singular;
            if (taken.Contains(name) || GeneratedFor(name).Any(taken.Contains))
            {
                name += "Entity";
            }

            name = CodeNames.Unique(name, taken);
            taken.UnionWith(GeneratedFor(name));
            classes[table.Name] = name;
        }

        // DbContext members and the generated auth set can't be table sets.
        var setNames = new HashSet<string>(StringComparer.Ordinal) { "Users", "Database", "Model", "ChangeTracker" };
        var entities = schema.Tables.Select(table =>
        {
            var className = classes[table.Name];
            var members = new HashSet<string>(StringComparer.Ordinal) { "Id", className };
            var properties = table.Columns.Select(column =>
            {
                var name = CodeNames.Pascal(column.Name);
                // A member can't share its class's name: book.book → BookValue. Other clashes (a_b, a__b) get a number.
                return (Column: column, Name: CodeNames.Unique(name == className ? name + "Value" : name, members));
            }).ToList();

            return new ExportEntity(
                table.Name,
                className,
                CodeNames.Unique(CodeNames.Pascal(table.Name), setNames),
                [.. properties.Select(property => new ExportProperty(
                    property.Column.Name,
                    property.Name,
                    property.Column.Type!,
                    property.Column.IsNullable,
                    property.Column.IsUnique,
                    ParseDefault(property.Column),
                    Reference(table, property.Column, property.Name, classes, members)))]);
        }).ToList();

        return new ExportModel(solution, projectName, schema.SchemaVersion, entities);
    }

    /// <summary>The types the generator writes for an entity class.</summary>
    private static string[] GeneratedFor(string name) =>
        [name, $"{name}Dto", $"{name}Input", $"{name}Service", $"{name}Repository", $"I{name}Repository", $"{name}Configuration", $"{name}Controller"];

    private static ColumnDefault? ParseDefault(DataColumn column) =>
        ColumnDefault.Parse(column.Type!.DataType, column.Default, column.Type.Length, column.Type.Precision, column.Type.Scale);

    private static ExportReference? Reference(
        DataTable table, DataColumn column, string propertyName, Dictionary<string, string> classes, HashSet<string> members)
    {
        if (column.References is not { } target)
        {
            return null;
        }

        if (!classes.TryGetValue(target, out var targetClass))
        {
            throw new ConflictException($"{table.Name}.{column.Name} references {target}, which isn't part of the export.");
        }

        // author_id → Author; a column without "_id" gets "Navigation" (EF's convention for the same clash).
        var navigation = propertyName.EndsWith("Id", StringComparison.Ordinal) && propertyName.Length > 2
            ? propertyName[..^2]
            : propertyName + "Navigation";
        return new ExportReference(
            target,
            targetClass,
            Enum.TryParse<ReferenceAction>(column.OnDelete, out var onDelete) ? onDelete : ReferenceAction.Restrict,
            CodeNames.Unique(members.Contains(navigation) ? navigation + "Navigation" : navigation, members),
            ConstraintNames.ForeignKey(table.Name, column.Name));
    }
}

/// <param name="Table">The table's name in the database (also the route: <c>/api/books</c>).</param>
/// <param name="ClassName">The entity class, e.g. <c>Book</c>.</param>
/// <param name="SetName">The <c>DbSet</c> property, e.g. <c>Books</c>.</param>
public sealed record ExportEntity(string Table, string ClassName, string SetName, IReadOnlyList<ExportProperty> Properties)
{
    public string UniqueKeyName(ExportProperty property) => ConstraintNames.UniqueKey(Table, property.Column);
}

/// <param name="Column">The column's name in the database.</param>
/// <param name="Name">The C# property, e.g. <c>PriceUsd</c>.</param>
public sealed record ExportProperty(
    string Column, string Name, ColumnType Type, bool IsNullable, bool IsUnique, ColumnDefault? Default, ExportReference? Reference);

/// <param name="Navigation">The navigation property, e.g. <c>Author</c> for <c>AuthorId</c>.</param>
public sealed record ExportReference(string TargetTable, string TargetClass, ReferenceAction OnDelete, string Navigation, string ConstraintName);
