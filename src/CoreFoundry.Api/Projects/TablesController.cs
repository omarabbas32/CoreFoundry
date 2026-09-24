using CoreFoundry.Api.Authorization;
using CoreFoundry.Application.Schema;
using CoreFoundry.Domain.Schema;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace CoreFoundry.Api.Projects;

public sealed record ColumnRequest(
    string Name,
    DataType DataType,
    int? Length,
    int? Precision,
    int? Scale,
    bool IsNullable,
    bool IsUnique,
    string? DefaultValue,
    long? ReferencesTableId = null,
    ReferenceAction? OnDelete = null)
{
    public ColumnInput ToInput() =>
        new(Name, DataType, Length, Precision, Scale, IsNullable, IsUnique, DefaultValue, ReferencesTableId, OnDelete);
}

public sealed record CreateTableRequest(string Name, IReadOnlyList<ColumnRequest>? Columns);

public sealed record RenameTableRequest(int Version, string Name);

public sealed record VersionRequest(int Version);

/// <summary>A column add or update; <see cref="Version"/> is the table's version the caller last saw.</summary>
public sealed record SaveColumnRequest(
    int Version,
    string Name,
    DataType DataType,
    int? Length,
    int? Precision,
    int? Scale,
    bool IsNullable,
    bool IsUnique,
    string? DefaultValue,
    long? ReferencesTableId = null,
    ReferenceAction? OnDelete = null)
{
    public ColumnInput ToInput() =>
        new(Name, DataType, Length, Precision, Scale, IsNullable, IsUnique, DefaultValue, ReferencesTableId, OnDelete);
}

public sealed record ReorderColumnsRequest(int Version, IReadOnlyList<long> ColumnIds);

/// <summary>
/// The table designer. Edits only draft metadata; nothing is sent to the project's database until
/// the schema engine applies it. Changes return the whole table with its new <c>version</c>.
/// </summary>
[ApiController]
[Route("api/projects/{projectId:long}/tables")]
[Authorize(Policy = ProjectPolicies.Developer)]
public sealed class TablesController(TableService tables) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<TableSummaryDto>> List(long projectId, CancellationToken cancellationToken) =>
        tables.ListAsync(projectId, cancellationToken);

    /// <summary>The whole draft schema: every table with its columns and references (for the diagram).</summary>
    [HttpGet("~/api/projects/{projectId:long}/schema")]
    public Task<IReadOnlyList<TableDto>> Schema(long projectId, CancellationToken cancellationToken) =>
        tables.GetSchemaAsync(projectId, cancellationToken);

    [HttpGet("{tableId:long}")]
    public Task<TableDto> Get(long projectId, long tableId, CancellationToken cancellationToken) =>
        tables.GetAsync(projectId, tableId, cancellationToken);

    /// <summary>Creates a table, optionally with its first columns (errors keyed like <c>columns[2].length</c>).</summary>
    [HttpPost]
    public async Task<ActionResult<TableDto>> Create(long projectId, CreateTableRequest request, CancellationToken cancellationToken)
    {
        var created = await tables.CreateAsync(
            projectId, request.Name, request.Columns?.Select(column => column.ToInput()).ToList(), cancellationToken);
        return CreatedAtAction(nameof(Get), new { projectId, tableId = created.Id }, created);
    }

    [HttpPut("{tableId:long}")]
    public Task<TableDto> Rename(long projectId, long tableId, RenameTableRequest request, CancellationToken cancellationToken) =>
        tables.RenameAsync(projectId, tableId, request.Version, request.Name, cancellationToken);

    /// <summary>204 if the table was never applied (deleted outright); otherwise 200 with the table marked pending drop.</summary>
    [HttpDelete("{tableId:long}")]
    public async Task<ActionResult<TableDto>> Delete(
        long projectId, long tableId, [FromQuery, BindRequired] int version, CancellationToken cancellationToken)
    {
        var table = await tables.DeleteAsync(projectId, tableId, version, cancellationToken);
        return table is null ? NoContent() : table;
    }

    [HttpPost("{tableId:long}/restore")]
    public Task<TableDto> Restore(long projectId, long tableId, VersionRequest request, CancellationToken cancellationToken) =>
        tables.RestoreAsync(projectId, tableId, request.Version, cancellationToken);

    [HttpPost("{tableId:long}/columns")]
    public Task<TableDto> AddColumn(long projectId, long tableId, SaveColumnRequest request, CancellationToken cancellationToken) =>
        tables.AddColumnAsync(projectId, tableId, request.Version, request.ToInput(), cancellationToken);

    [HttpPut("{tableId:long}/columns/order")]
    public Task<TableDto> ReorderColumns(
        long projectId, long tableId, ReorderColumnsRequest request, CancellationToken cancellationToken) =>
        tables.ReorderColumnsAsync(projectId, tableId, request.Version, request.ColumnIds, cancellationToken);

    [HttpPut("{tableId:long}/columns/{columnId:long}")]
    public Task<TableDto> UpdateColumn(
        long projectId, long tableId, long columnId, SaveColumnRequest request, CancellationToken cancellationToken) =>
        tables.UpdateColumnAsync(projectId, tableId, columnId, request.Version, request.ToInput(), cancellationToken);

    /// <summary>Removes a never-applied column; marks an applied one pending drop (undo with restore).</summary>
    [HttpDelete("{tableId:long}/columns/{columnId:long}")]
    public Task<TableDto> DeleteColumn(
        long projectId, long tableId, long columnId, [FromQuery, BindRequired] int version, CancellationToken cancellationToken) =>
        tables.DeleteColumnAsync(projectId, tableId, columnId, version, cancellationToken);

    [HttpPost("{tableId:long}/columns/{columnId:long}/restore")]
    public Task<TableDto> RestoreColumn(
        long projectId, long tableId, long columnId, VersionRequest request, CancellationToken cancellationToken) =>
        tables.RestoreColumnAsync(projectId, tableId, columnId, request.Version, cancellationToken);
}
