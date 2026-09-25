using CoreFoundry.Api.Authorization;
using CoreFoundry.Application.Templates;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoreFoundry.Api.Projects;

/// <param name="WithSampleData">Insert the template's sample rows right after the first successful apply.</param>
public sealed record UseTemplateRequest(bool WithSampleData);

/// <summary>Ready schemas (e.g. E-commerce) a project can start from.</summary>
[ApiController]
public sealed class TemplatesController(TemplateService templates) : ControllerBase
{
    [HttpGet("api/templates")]
    [Authorize]
    public IReadOnlyList<TemplateDto> List() => TemplateService.List();

    /// <summary>Creates the template's tables as drafts in a project without tables (409 otherwise).</summary>
    [HttpPost("api/projects/{projectId:long}/templates/{key}")]
    [Authorize(Policy = ProjectPolicies.Developer)]
    public Task<UsedTemplateDto> Use(long projectId, string key, UseTemplateRequest request, CancellationToken cancellationToken) =>
        templates.UseAsync(projectId, key, request.WithSampleData, cancellationToken);
}
