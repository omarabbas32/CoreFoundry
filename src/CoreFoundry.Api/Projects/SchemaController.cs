using CoreFoundry.Api.Authorization;
using CoreFoundry.Application.SchemaEngine;
using CoreFoundry.Application.Templates;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoreFoundry.Api.Projects;

/// <param name="PlanHash">From the plan the caller reviewed; a different plan is refused (409 plan-stale).</param>
/// <param name="AcknowledgeDestructive">Required when the plan drops or narrows data (otherwise 422).</param>
public sealed record ApplyRequest(string PlanHash, bool AcknowledgeDestructive);

/// <summary>The schema engine: review the plan that turns the draft into real tables, apply it, see history and drift.</summary>
[ApiController]
[Route("api/projects/{projectId:long}/schema")]
public sealed class SchemaController(SchemaPlanService plans, SchemaApplier applier, SampleDataService sampleData) : ControllerBase
{
    /// <summary>The operations and exact SQL that would make the database match the draft. Reads only.</summary>
    [HttpGet("plan")]
    [Authorize(Policy = ProjectPolicies.Developer)]
    public Task<SchemaPlanDto> Plan(long projectId, CancellationToken cancellationToken) =>
        plans.PlanAsync(projectId, cancellationToken);

    /// <summary>Runs the reviewed plan. 409 plan-stale / apply-in-progress, 422 destructive-not-acknowledged, 500 apply-failed.</summary>
    [HttpPost("apply")]
    [Authorize(Policy = ProjectPolicies.Admin)]
    public async Task<ApplyResultDto> Apply(long projectId, ApplyRequest request, CancellationToken cancellationToken)
    {
        var result = await applier.ApplyAsync(projectId, User.GetUserId()!.Value, request.PlanHash, request.AcknowledgeDestructive, cancellationToken);
        // A project started from a template with sample data gets its rows after its first successful apply.
        return result with { SampleData = await sampleData.InsertPendingAsync(projectId, cancellationToken) };
    }

    /// <summary>Applies, newest first.</summary>
    [HttpGet("migrations")]
    [Authorize(Policy = ProjectPolicies.Developer)]
    public Task<MigrationPageDto> Migrations(
        long projectId, CancellationToken cancellationToken, [FromQuery] int page = 1, [FromQuery] int pageSize = SchemaPlanService.DefaultPageSize) =>
        plans.ListMigrationsAsync(projectId, page, pageSize, cancellationToken);

    [HttpGet("migrations/{migrationId:long}")]
    [Authorize(Policy = ProjectPolicies.Developer)]
    public Task<MigrationDto> Migration(long projectId, long migrationId, CancellationToken cancellationToken) =>
        plans.GetMigrationAsync(projectId, migrationId, cancellationToken);

    /// <summary>Changes made to the database outside CoreFoundry since the last apply.</summary>
    [HttpGet("drift")]
    [Authorize(Policy = ProjectPolicies.Developer)]
    public Task<DriftDto> Drift(long projectId, CancellationToken cancellationToken) =>
        plans.DriftAsync(projectId, cancellationToken);
}
