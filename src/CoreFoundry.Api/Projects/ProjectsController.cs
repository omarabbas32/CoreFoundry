using CoreFoundry.Api.Authorization;
using CoreFoundry.Application.Projects;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoreFoundry.Api.Projects;

public sealed record CreateProjectRequest(string Name);

public sealed record RenameProjectRequest(string Name);

[ApiController]
[Authorize]
[Route("api/projects")]
public sealed class ProjectsController(ProjectService projects) : ControllerBase
{
    /// <summary>Projects the caller is a member of.</summary>
    [HttpGet]
    public Task<IReadOnlyList<ProjectDto>> List(CancellationToken cancellationToken) =>
        projects.ListForUserAsync(CurrentUserId, cancellationToken);

    /// <summary>Creates the project with the caller as Owner and provisions its database.</summary>
    [HttpPost]
    public async Task<ActionResult<ProjectDto>> Create(CreateProjectRequest request, CancellationToken cancellationToken)
    {
        var created = await projects.CreateAsync(CurrentUserId, request.Name, cancellationToken);
        return CreatedAtAction(nameof(Get), new { projectId = created.Id }, created);
    }

    [HttpGet("{projectId:long}")]
    [Authorize(Policy = ProjectPolicies.Developer)]
    public Task<ProjectDto> Get(long projectId, CancellationToken cancellationToken) =>
        projects.GetAsync(projectId, CurrentUserId, cancellationToken);

    [HttpPatch("{projectId:long}")]
    [Authorize(Policy = ProjectPolicies.Admin)]
    public Task<ProjectDto> Rename(long projectId, RenameProjectRequest request, CancellationToken cancellationToken) =>
        projects.RenameAsync(projectId, CurrentUserId, request.Name, cancellationToken);

    [HttpDelete("{projectId:long}")]
    [Authorize(Policy = ProjectPolicies.Owner)]
    public async Task<IActionResult> Delete(long projectId, CancellationToken cancellationToken)
    {
        await projects.DeleteAsync(projectId, cancellationToken);
        return NoContent();
    }

    /// <summary>Tries again to create the database of a project whose provisioning failed.</summary>
    [HttpPost("{projectId:long}/retry-provisioning")]
    [Authorize(Policy = ProjectPolicies.Owner)]
    public Task<ProjectDto> RetryProvisioning(long projectId, CancellationToken cancellationToken) =>
        projects.RetryProvisioningAsync(projectId, CurrentUserId, cancellationToken);

    // [Authorize] guarantees an authenticated user; the JwtBearer handler only accepts tokens with our "sub".
    private long CurrentUserId => User.GetUserId()
        ?? throw new InvalidOperationException("Authenticated request without a user id claim.");
}
