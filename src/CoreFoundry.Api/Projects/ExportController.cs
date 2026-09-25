using CoreFoundry.Api.Authorization;
using CoreFoundry.Application.Export;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoreFoundry.Api.Projects;

/// <summary>Code export: the project as a standalone .NET backend.</summary>
[ApiController]
[Route("api/projects/{projectId:long}/export")]
public sealed class ExportController(ExportService exports) : ControllerBase
{
    /// <summary>
    /// A zip of a .NET 10 Clean Architecture solution for the applied tables: EF Core with an initial
    /// migration, JWT auth, Swagger UI, Dockerfile and docker-compose. 409 if nothing is applied yet.
    /// </summary>
    [HttpGet]
    [Authorize(Policy = ProjectPolicies.Developer)]
    public async Task<IActionResult> Export(long projectId, CancellationToken cancellationToken)
    {
        var archive = await exports.ExportAsync(projectId, cancellationToken);
        return File(archive.Content, "application/zip", archive.FileName);
    }
}
