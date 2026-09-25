using System.Text.Json;
using CoreFoundry.Api.Authorization;
using CoreFoundry.Application.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoreFoundry.Api.Projects;

/// <summary>
/// The Data API: rows of the project's applied tables. <c>{table}</c> is the table's applied name;
/// bodies are JSON objects of column values (decimals may be strings, dates <c>yyyy-MM-dd</c>).
/// </summary>
[ApiController]
[Route("api/projects/{projectId:long}/data")]
[Authorize(Policy = ProjectPolicies.Developer)]
public sealed class DataController(DataService data) : ControllerBase
{
    /// <summary>The applied tables and their columns (what rows look like and what can be written).</summary>
    [HttpGet]
    public Task<DataSchemaDto> Tables(long projectId, CancellationToken cancellationToken) =>
        data.TablesAsync(projectId, cancellationToken);

    /// <summary>A page of rows. <c>sort</c> is a column name, <c>-</c> first for descending; ties are ordered by id.</summary>
    [HttpGet("{table}")]
    public Task<DataPageDto> List(
        long projectId, string table, CancellationToken cancellationToken,
        [FromQuery] int page = 1, [FromQuery] int pageSize = DataService.DefaultPageSize, [FromQuery] string? sort = null) =>
        data.ListAsync(projectId, table, page, pageSize, sort, cancellationToken);

    /// <summary>Id and label of rows to pick for a reference column; <c>q</c> searches the label or matches the id.</summary>
    [HttpGet("{table}/lookup")]
    public Task<IReadOnlyList<LookupItem>> Lookup(
        long projectId, string table, CancellationToken cancellationToken,
        [FromQuery] string? q = null, [FromQuery] int limit = DataService.DefaultLookupSize) =>
        data.LookupAsync(projectId, table, q, limit, cancellationToken);

    [HttpGet("{table}/{id:long}")]
    public Task<DataRow> Get(long projectId, string table, long id, CancellationToken cancellationToken) =>
        data.GetAsync(projectId, table, id, cancellationToken);

    [HttpPost("{table}")]
    public async Task<ActionResult<DataRow>> Create(long projectId, string table, JsonElement body, CancellationToken cancellationToken)
    {
        var row = await data.CreateAsync(projectId, table, body, cancellationToken);
        return Created($"/api/projects/{projectId}/data/{table}/{row.Id}", row);
    }

    /// <summary>Full replace of the row's columns; columns left out get their default, or NULL.</summary>
    [HttpPut("{table}/{id:long}")]
    public Task<DataRow> Replace(long projectId, string table, long id, JsonElement body, CancellationToken cancellationToken) =>
        data.ReplaceAsync(projectId, table, id, body, cancellationToken);

    [HttpDelete("{table}/{id:long}")]
    public async Task<IActionResult> Delete(long projectId, string table, long id, CancellationToken cancellationToken)
    {
        await data.DeleteAsync(projectId, table, id, cancellationToken);
        return NoContent();
    }
}
