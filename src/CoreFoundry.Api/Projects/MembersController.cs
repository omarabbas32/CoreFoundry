using CoreFoundry.Api.Authorization;
using CoreFoundry.Application.Projects;
using CoreFoundry.Domain.Projects;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoreFoundry.Api.Projects;

public sealed record InviteMemberRequest(string Email, ProjectRole Role);

public sealed record ChangeMemberRoleRequest(ProjectRole Role);

public sealed record TransferOwnershipRequest(long UserId);

[ApiController]
[Route("api/projects/{projectId:long}")]
public sealed class MembersController(MemberService members, InvitationService invitations) : ControllerBase
{
    [HttpGet("members")]
    [Authorize(Policy = ProjectPolicies.Developer)]
    public Task<IReadOnlyList<MemberDto>> List(long projectId, CancellationToken cancellationToken) =>
        members.ListAsync(projectId, cancellationToken);

    /// <summary>Pending invitations: who has been asked to join and hasn't answered yet.</summary>
    [HttpGet("invitations")]
    [Authorize(Policy = ProjectPolicies.Developer)]
    public Task<IReadOnlyList<InvitationDto>> Invitations(long projectId, CancellationToken cancellationToken) =>
        invitations.ListForProjectAsync(projectId, cancellationToken);

    /// <summary>
    /// Invites an existing account as Admin or Developer. They join only when they accept it (from their
    /// notifications); until then they aren't a member.
    /// </summary>
    [HttpPost("invitations")]
    [Authorize(Policy = ProjectPolicies.Admin)]
    public async Task<ActionResult<InvitationDto>> Invite(long projectId, InviteMemberRequest request, CancellationToken cancellationToken)
    {
        var invitation = await invitations.InviteAsync(projectId, User.GetUserId()!.Value, request.Email, request.Role, cancellationToken);
        return CreatedAtAction(nameof(Invitations), new { projectId }, invitation);
    }

    [HttpDelete("invitations/{invitationId:long}")]
    [Authorize(Policy = ProjectPolicies.Admin)]
    public async Task<IActionResult> CancelInvitation(long projectId, long invitationId, CancellationToken cancellationToken)
    {
        await invitations.CancelAsync(projectId, invitationId, cancellationToken);
        return NoContent();
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

/// <summary>The signed-in user's invitations to join projects: their notifications. Accept to join, or decline.</summary>
[ApiController]
[Route("api/me/invitations")]
[Authorize]
public sealed class MyInvitationsController(InvitationService invitations) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<InvitationDto>> List(CancellationToken cancellationToken) => invitations.ListMineAsync(UserId, cancellationToken);

    /// <summary>Joins the project with the invited role; answers with the project.</summary>
    [HttpPost("{invitationId:long}/accept")]
    public Task<ProjectDto> Accept(long invitationId, CancellationToken cancellationToken) =>
        invitations.AcceptAsync(UserId, invitationId, cancellationToken);

    [HttpPost("{invitationId:long}/decline")]
    public async Task<IActionResult> Decline(long invitationId, CancellationToken cancellationToken)
    {
        await invitations.DeclineAsync(UserId, invitationId, cancellationToken);
        return NoContent();
    }

    private long UserId => User.GetUserId() ?? throw new InvalidOperationException("The signed-in user has no id.");
}
