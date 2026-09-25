using System.Text.Json;
using CoreFoundry.Application.Common;
using CoreFoundry.Application.Projects;
using CoreFoundry.Application.SchemaEngine;
using CoreFoundry.Domain.Projects;

namespace CoreFoundry.Application.Data;

/// <param name="Type">E.g. <c>Varchar(200)</c>; for a column changed outside CoreFoundry, MySQL's type (then <paramref name="IsWritable"/> is false).</param>
/// <param name="DataType">The CoreFoundry type name (<c>Varchar</c>, <c>Decimal</c> …), or null if unknown.</param>
public sealed record DataColumnDto(
    string Name, string Type, string? DataType, int? Length, int? Precision, int? Scale,
    bool IsNullable, bool IsUnique, string? Default, string? References, bool IsWritable);

public sealed record DataTableDto(string Name, IReadOnlyList<DataColumnDto> Columns, string? LabelColumn);

/// <param name="SchemaVersion">The schema version these tables were applied at.</param>
public sealed record DataSchemaDto(int SchemaVersion, IReadOnlyList<DataTableDto> Tables);

public sealed record DataPageDto(IReadOnlyList<DataRow> Items, int Page, int PageSize, long Total);

/// <summary>
/// Rows of a project's applied tables. Tables and columns are those of the last successful apply
/// (<see cref="ISnapshotProvider"/>); request names are only looked up there, never put into SQL.
/// </summary>
public sealed class DataService(IProjectRepository projects, ISnapshotProvider snapshots, IDataRepository rows)
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 100;
    public const int DefaultLookupSize = 20;
    public const int MaxLookupSize = 100;

    public async Task<DataSchemaDto> TablesAsync(long projectId, CancellationToken cancellationToken)
    {
        var (_, schema) = await SchemaAsync(projectId, cancellationToken);
        return new DataSchemaDto(schema.SchemaVersion, [.. schema.Tables.Select(ToDto)]);
    }

    public async Task<DataPageDto> ListAsync(
        long projectId, string tableName, int page, int pageSize, string? sort, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (page < 1)
        {
            errors["page"] = ["Must be 1 or more."];
        }

        if (pageSize is < 1 or > MaxPageSize)
        {
            errors["pageSize"] = [$"Must be from 1 to {MaxPageSize}."];
        }

        var (project, table) = await TableAsync(projectId, tableName, cancellationToken);
        SortOrder? order = null;
        try
        {
            order = SortOrder.Parse(table, sort);
        }
        catch (ValidationFailedException ex)
        {
            errors["sort"] = ex.Errors["sort"];
        }

        if (errors.Count > 0)
        {
            throw new ValidationFailedException(errors);
        }

        var (items, total) = await rows.ListAsync(project.DatabaseName, table, order!, (page - 1) * pageSize, pageSize, cancellationToken);
        return new DataPageDto(items, page, pageSize, total);
    }

    public async Task<DataRow> GetAsync(long projectId, string tableName, long id, CancellationToken cancellationToken)
    {
        var (project, table) = await TableAsync(projectId, tableName, cancellationToken);
        return await rows.FindAsync(project.DatabaseName, table, id, cancellationToken) ?? throw RowNotFound(table, id);
    }

    public async Task<DataRow> CreateAsync(long projectId, string tableName, JsonElement body, CancellationToken cancellationToken)
    {
        var (project, table) = await TableAsync(projectId, tableName, cancellationToken);
        var values = RowCoercer.Coerce(table, body, replace: false);
        var id = await rows.InsertAsync(project.DatabaseName, table, values, cancellationToken);
        return await rows.FindAsync(project.DatabaseName, table, id, cancellationToken) ?? throw RowNotFound(table, id);
    }

    /// <summary>Full replace: columns left out are set to their default, or NULL.</summary>
    public async Task<DataRow> ReplaceAsync(
        long projectId, string tableName, long id, JsonElement body, CancellationToken cancellationToken)
    {
        var (project, table) = await TableAsync(projectId, tableName, cancellationToken);
        var values = RowCoercer.Coerce(table, body, replace: true);
        if (!await rows.UpdateAsync(project.DatabaseName, table, id, values, cancellationToken))
        {
            throw RowNotFound(table, id);
        }

        return await rows.FindAsync(project.DatabaseName, table, id, cancellationToken) ?? throw RowNotFound(table, id);
    }

    public async Task DeleteAsync(long projectId, string tableName, long id, CancellationToken cancellationToken)
    {
        var (project, table) = await TableAsync(projectId, tableName, cancellationToken);
        if (!await rows.DeleteAsync(project.DatabaseName, table, id, cancellationToken))
        {
            throw RowNotFound(table, id);
        }
    }

    /// <summary>Rows to pick from for a reference column: id and label, searched by label or exact id.</summary>
    public async Task<IReadOnlyList<LookupItem>> LookupAsync(
        long projectId, string tableName, string? search, int limit, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > MaxLookupSize)
        {
            throw new ValidationFailedException("limit", $"Must be from 1 to {MaxLookupSize}.");
        }

        var (project, table) = await TableAsync(projectId, tableName, cancellationToken);
        return await rows.LookupAsync(project.DatabaseName, table, search?.Trim(), limit, cancellationToken);
    }

    private async Task<(Project Project, DataSchema Schema)> SchemaAsync(long projectId, CancellationToken cancellationToken)
    {
        var project = await SchemaPlanService.ReadyProjectAsync(projects, projectId, cancellationToken);
        return (project, await snapshots.GetAsync(projectId, project.SchemaVersion, cancellationToken));
    }

    private async Task<(Project Project, DataTable Table)> TableAsync(long projectId, string tableName, CancellationToken cancellationToken)
    {
        var (project, schema) = await SchemaAsync(projectId, cancellationToken);
        return (project, schema.FindTable(tableName)
            ?? throw new NotFoundException($"There is no applied table named {tableName}. Tables appear here once a plan that creates them is applied."));
    }

    private static NotFoundException RowNotFound(DataTable table, long id) => new($"{table.Name} has no row with id {id}.");

    private static DataTableDto ToDto(DataTable table) => new(
        table.Name,
        [.. table.Columns.Select(column => new DataColumnDto(
            column.Name,
            column.RawType,
            column.Type?.DataType.ToString(),
            column.Type?.Length,
            column.Type?.Precision,
            column.Type?.Scale,
            column.IsNullable,
            column.IsUnique,
            column.Default,
            column.References,
            column.IsWritable))],
        table.LabelColumn?.Name);
}
