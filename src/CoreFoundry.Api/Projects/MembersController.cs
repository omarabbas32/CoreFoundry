using CoreFoundry.Api.Authorization;
using CoreFoundry.Application.Projects;
using CoreFoundry.Domain.Projects;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoreFoundry.Api.Projects;

public sealed record AddMemberRequest(string Email, ProjectRole Role);

public sealed record ChangeMemberRoleRequest(ProjectRole Role);

public sealed record TransferOwnershipRequest(long UserId);

[ApiController]
[Route("api/projects/{projectId:long}")]
public sealed class MembersController(MemberService members) : ControllerBase
{
    [HttpGet("members")]
    [Authorize(Policy = ProjectPolicies.Developer)]
    public Task<IReadOnlyList<MemberDto>> List(long projectId, CancellationToken cancellationToken) =>
        members.ListAsync(projectId, cancellationToken);

    /// <summary>Adds an existing account as Admin or Developer.</summary>
    [HttpPost("members")]
    [Authorize(Policy = ProjectPolicies.Admin)]
    public async Task<ActionResult<MemberDto>> Add(long projectId, AddMemberRequest request, CancellationToken cancellationToken)
    {
        var added = await members.AddAsync(projectId, request.Email, request.Role, cancellationToken);
        return CreatedAtAction(nameof(List), new { projectId }, added);
    }

    [HttpPut("members/{userId:long}")]
    [Authorize(Policy = ProjectPolicies.Admin)]
    public Task<MemberDto> ChangeRole(long projectId, long userId, ChangeMemberRoleRequest request, CancellationToken cancellationToken) =>
        members.ChangeRoleAsync(projectId, userId, request.Role, cancellationToken);

    /// <summary>Admins remove others; any member may remove themselves (leave the project).</summary>
    [HttpDelete("members/{userId:long}")]
    [Authorize(Policy = ProjectPolicies.Developer)]
    public async Task<IActionResult> Remove(long projectId, long userId, CancellationToken cancellationToken)
    {
        await members.RemoveAsync(projectId, User.GetUserId()!.Value, userId, cancellationToken);
        return NoContent();
    }

    /// <summary>Makes another member the Owner; the caller stays on as Admin.</summary>
    [HttpPost("transfer-ownership")]
    [Authorize(Policy = ProjectPolicies.Owner)]
    public Task<IReadOnlyList<MemberDto>> TransferOwnership(
        long projectId, TransferOwnershipRequest request, CancellationToken cancellationToken) =>
        members.TransferOwnershipAsync(projectId, request.UserId, cancellationToken);
}
