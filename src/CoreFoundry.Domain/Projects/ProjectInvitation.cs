using CoreFoundry.Domain.Common;

namespace CoreFoundry.Domain.Projects;

/// <summary>
/// An offer to join a project, waiting for the invited user to accept or decline. They become a member only by
/// accepting (<see cref="Project.AddMember"/> applies its rules then). Accepting, declining and cancelling all remove
/// the invitation, so at most one is pending per user and project.
/// </summary>
public sealed class ProjectInvitation
{
    private ProjectInvitation() { } // EF Core

    public ProjectInvitation(long projectId, long userId, ProjectRole role, long invitedBy)
    {
        ProjectId = Guard.PositiveId(projectId, nameof(ProjectId));
        UserId = Guard.PositiveId(userId, nameof(UserId));
        InvitedBy = Guard.PositiveId(invitedBy, nameof(InvitedBy));
        Role = role is ProjectRole.Admin or ProjectRole.Developer
            ? role
            : throw new DomainException("Invite someone as Admin or Developer; ownership is transferred, not invited.") { Field = "role" };
    }

    public long Id { get; private set; }
    public long ProjectId { get; private set; }

    /// <summary>Who is invited.</summary>
    public long UserId { get; private set; }

    public ProjectRole Role { get; private set; }

    /// <summary>Who invited them; null once that account is gone.</summary>
    public long? InvitedBy { get; private set; }

    public DateTime CreatedAt { get; private set; }
}
