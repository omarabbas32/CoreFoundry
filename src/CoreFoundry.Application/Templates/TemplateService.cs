using CoreFoundry.Application.Common;
using CoreFoundry.Application.Projects;
using CoreFoundry.Application.Schema;
using CoreFoundry.Application.SchemaEngine;

namespace CoreFoundry.Application.Templates;

/// <param name="References">The tables this one's columns reference (for a preview of the relations).</param>
public sealed record TemplateTableDto(string Name, int ColumnCount, IReadOnlyList<string> References);

public sealed record TemplateDto(string Key, string Name, string Description, IReadOnlyList<TemplateTableDto> Tables, int SampleRowCount);

public sealed record UsedTemplateDto(string TemplateKey, bool SampleDataPending, IReadOnlyList<TableSummaryDto> Tables);

/// <summary>
/// Ready schemas: lists them, and turns one into the draft tables of an empty project. The tables go
/// through <see cref="TableService"/>, so they're checked exactly like tables made in the designer.
/// Nothing reaches MySQL until the user reviews the plan and applies it.
/// </summary>
public sealed class TemplateService(IProjectRepository projects, ITableRepository tables, TableService tableService, IUnitOfWork unitOfWork)
{
    public static IReadOnlyList<TemplateDto> List() => [.. SchemaTemplates.All.Select(template => new TemplateDto(
        template.Key,
        template.Name,
        template.Description,
        [.. template.Tables.Select(table => new TemplateTableDto(
            table.Name,
            table.Columns.Count,
            [.. table.Columns.Select(column => column.References).OfType<string>().Distinct(StringComparer.Ordinal)]))],
        template.SampleRows.Count))];

    /// <exception cref="NotFoundException">No such template (or project).</exception>
    /// <exception cref="ConflictException">The project already has tables.</exception>
    public async Task<UsedTemplateDto> UseAsync(long projectId, string key, bool withSampleData, CancellationToken cancellationToken)
    {
        var template = SchemaTemplates.Find(key) ?? throw new NotFoundException($"There is no template named {key}.");
        var project = await SchemaPlanService.VisibleProjectAsync(projects, projectId, cancellationToken);
        if (await tables.CountAsync(projectId, cancellationToken) > 0)
        {
            throw new ConflictException("A template can only start a project that has no tables yet.");
        }

        project.UseTemplate(template.Key, withSampleData);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        var created = new Dictionary<string, long>(StringComparer.Ordinal);
        try
        {
            // Columns that reference a table not created yet (itself, or a later one) are added afterwards.
            var deferred = new List<(string Table, TemplateColumn Column)>();
            foreach (var table in template.Tables)
            {
                var now = table.Columns.Where(column => column.References is null || created.ContainsKey(column.References)).ToList();
                deferred.AddRange(table.Columns.Except(now).Select(column => (table.Name, column)));
                var dto = await tableService.CreateAsync(projectId, table.Name, [.. now.Select(column => Input(column, created))], cancellationToken);
                created[table.Name] = dto.Id;
            }

            foreach (var (table, column) in deferred)
            {
                var current = await tableService.GetAsync(projectId, created[table], cancellationToken);
                await tableService.AddColumnAsync(projectId, current.Id, current.Version, Input(column, created), cancellationToken);
            }
        }
        catch
        {
            // Leave the project empty rather than half-templated. The tables are new, so deleting removes them.
            foreach (var id in created.Values.Reverse())
            {
                if (await tables.FindAsync(projectId, id, cancellationToken) is { } table)
                {
                    tables.Remove(table);
                }
            }

            await unitOfWork.SaveChangesAsync(CancellationToken.None);
            throw;
        }

        return new UsedTemplateDto(template.Key, project.SampleDataPending, await tableService.ListAsync(projectId, cancellationToken));
    }

    private static ColumnInput Input(TemplateColumn column, Dictionary<string, long> tableIds) => new(
        column.Name,
        column.Type,
        column.Length,
        column.Precision,
        column.Scale,
        column.Nullable,
        column.Unique,
        column.Default,
        column.References is { } target ? tableIds[target] : null,
        column.OnDelete);
}
