using System.Text.Json;
using CoreFoundry.Application.Common;
using CoreFoundry.Application.Data;
using CoreFoundry.Application.Projects;
using CoreFoundry.Application.SchemaEngine;
using Microsoft.Extensions.Logging;

namespace CoreFoundry.Application.Templates;

/// <param name="Inserted">Rows inserted.</param>
/// <param name="Skipped">Why rows were left out, e.g. <c>reviews: not applied</c>. Empty when every row went in.</param>
public sealed record SampleDataResultDto(int Inserted, IReadOnlyList<string> Skipped);

/// <summary>
/// Inserts a template's sample rows into the applied tables, through the Data API's own validation and
/// repository. Parents go first and each <c>@key</c> becomes the id of the row inserted for it. A
/// table that isn't applied under its template name, lacks a column, or already has rows is skipped,
/// together with the rows that point into it.
/// </summary>
public sealed partial class SampleDataService(
    IProjectRepository projects, ISnapshotProvider snapshots, IDataRepository rows, IUnitOfWork unitOfWork, ILogger<SampleDataService> logger)
{
    /// <summary>After a successful apply: inserts the rows if the project asked for them. Never throws; returns null if there was nothing to do.</summary>
    public async Task<SampleDataResultDto?> InsertPendingAsync(long projectId, CancellationToken cancellationToken)
    {
        try
        {
            var project = await SchemaPlanService.VisibleProjectAsync(projects, projectId, cancellationToken);
            return project.SampleDataPending ? await InsertAsync(projectId, cancellationToken) : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The apply succeeded; sample data is a bonus and must not turn it into an error.
            LogSampleDataFailed(logger, projectId, ex.Message);
            return null;
        }
    }

    /// <exception cref="ConflictException">The project didn't start from a template.</exception>
    public async Task<SampleDataResultDto> InsertAsync(long projectId, CancellationToken cancellationToken)
    {
        var project = await SchemaPlanService.ReadyProjectAsync(projects, projectId, cancellationToken);
        var template = project.TemplateKey is { } key ? SchemaTemplates.Find(key) : null;
        if (template is null)
        {
            throw new ConflictException("This project didn't start from a template, so there is no sample data for it.");
        }

        var schema = await snapshots.GetAsync(projectId, project.SchemaVersion, cancellationToken);
        var skipped = new List<string>();
        var usable = new Dictionary<string, DataTable>(StringComparer.Ordinal);
        foreach (var table in template.Tables)
        {
            var reason = await UnusableAsync(project.DatabaseName, schema, template, table, cancellationToken);
            if (reason is null)
            {
                usable[table.Name] = schema.FindTable(table.Name)!;
            }
            else
            {
                skipped.Add($"{table.Name}: {reason}");
            }
        }

        var ids = new Dictionary<(string Table, string Key), long>();
        var failed = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in template.SampleRows)
        {
            if (!usable.TryGetValue(row.Table, out var table) || Values(row, table, ids) is not { } values)
            {
                if (usable.ContainsKey(row.Table))
                {
                    failed[row.Table] = failed.GetValueOrDefault(row.Table) + 1; // a row it points to is missing
                }

                continue;
            }

            try
            {
                var coerced = RowCoercer.Coerce(table, JsonSerializer.SerializeToElement(values), replace: false);
                ids[(row.Table, row.Key)] = await rows.InsertAsync(project.DatabaseName, table, coerced, cancellationToken);
            }
            catch (Exception ex) when (ex is ValidationFailedException or ConflictException)
            {
                failed[row.Table] = failed.GetValueOrDefault(row.Table) + 1;
            }
        }

        skipped.AddRange(failed.Select(pair => $"{pair.Key}: {pair.Value} row(s) left out (a row they point to is missing, or a value doesn't fit)"));
        project.SampleDataDone();
        await unitOfWork.SaveChangesAsync(cancellationToken);
        LogSampleDataInserted(logger, projectId, ids.Count, skipped.Count);
        return new SampleDataResultDto(ids.Count, skipped);
    }

    private async Task<string?> UnusableAsync(
        string databaseName, DataSchema schema, SchemaTemplate template, TemplateTable table, CancellationToken cancellationToken)
    {
        if (schema.FindTable(table.Name) is not { } applied)
        {
            return "not applied under this name";
        }

        var columns = template.SampleRows.Where(row => row.Table == table.Name).SelectMany(row => row.Values.Keys).Distinct(StringComparer.Ordinal);
        if (columns.FirstOrDefault(column => applied.FindColumn(column) is not { IsWritable: true }) is { } missing)
        {
            return $"column {missing} isn't applied";
        }

        var (_, total) = await rows.ListAsync(databaseName, applied, SortOrder.ById, 0, 1, cancellationToken);
        return total > 0 ? "already has rows" : null;
    }

    /// <summary>The row's values with every <c>@key</c> replaced by an id, or null if a referenced row wasn't inserted.</summary>
    private static Dictionary<string, object?>? Values(SampleRow row, DataTable table, Dictionary<(string Table, string Key), long> ids)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (column, value) in row.Values)
        {
            if (table.FindColumn(column)?.References is { } target && value is string reference && reference.StartsWith(SampleRow.ReferencePrefix))
            {
                if (!ids.TryGetValue((target, reference[1..]), out var id))
                {
                    return null;
                }

                values[column] = id;
            }
            else
            {
                values[column] = value;
            }
        }

        return values;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Project {ProjectId}: {Rows} sample rows inserted, {Skipped} note(s)")]
    private static partial void LogSampleDataInserted(ILogger logger, long projectId, int rows, int skipped);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Project {ProjectId}: sample data wasn't inserted: {Error}")]
    private static partial void LogSampleDataFailed(ILogger logger, long projectId, string error);
}
