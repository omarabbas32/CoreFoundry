using CoreFoundry.Application.Auth;
using CoreFoundry.Application.Common;
using CoreFoundry.Domain.Common;
using CoreFoundry.Domain.Projects;
using CoreFoundry.Domain.Users;

namespace CoreFoundry.Application.Projects;

/// <summary>A pending invitation, as the project's members and the invited user see it.</summary>
/// <param name="Email">The invited user's email.</param>
/// <param name="InvitedByEmail">Who sent it, or null if that account is gone.</param>
public sealed record InvitationDto(
    long Id, long ProjectId, string ProjectName, string Email, ProjectRole Role, string? InvitedByEmail, DateTime CreatedAt);

/// <summary>
/// Joining a project takes two people: an admin invites an existing account, and that user accepts or declines.
/// Until they accept they aren't a member and can't see the project; their pending invitations are their
/// notifications. Accepting runs <see cref="Project.AddMember"/>, so the membership rules stay in one place.
/// </summary>
public sealed class InvitationService(
    IProjectRepository projects, IInvitationRepository invitations, IUserRepository users, IUnitOfWork unitOfWork)
{
    /// <summary>Invites an existing account as Admin or Developer.</summary>
    /// <exception cref="NotFoundException">No account has that email.</exception>
    /// <exception cref="ConflictException">They're already a member, or already invited.</exception>
    public async Task<InvitationDto> InviteAsync(
        long projectId, long inviterId, string email, ProjectRole role, CancellationToken cancellationToken)
    {
        var project = await FindVisibleAsync(projectId, cancellationToken);
        var user = await users.FindByEmailAsync(NormalizedEmail(email), cancellationToken)
            ?? throw new NotFoundException("There is no account with this email. Ask them to register first, then invite them.");

        if (project.FindMember(user.Id) is not null)
        {
            throw new ConflictException("This user is already a member of the project.");
        }

        if (await invitations.ExistsAsync(projectId, user.Id, cancellationToken))
        {
            throw new ConflictException("This user is already invited. They can accept it from their notifications.");
        }

        var invitation = Validated(() => new ProjectInvitation(projectId, user.Id, role, inviterId));
        invitations.Add(invitation);
        await SaveAsync(cancellationToken);
        return await ToDtoAsync(invitation, project, cancellationToken);
    }

    /// <summary>The project's pending invitations (members see who's been invited).</summary>
    public async Task<IReadOnlyList<InvitationDto>> ListForProjectAsync(long projectId, CancellationToken cancellationToken)
    {
        var project = await FindVisibleAsync(projectId, cancellationToken);
        var pending = await invitations.ListForProjectAsync(projectId, cancellationToken);
        var emails = await EmailsAsync(pending, cancellationToken);
        return [.. pending.Select(invitation => ToDto(invitation, project, emails))];
    }

    /// <summary>Withdraws an invitation that hasn't been answered yet.</summary>
    public async Task CancelAsync(long projectId, long invitationId, CancellationToken cancellationToken)
    {
        await FindVisibleAsync(projectId, cancellationToken);
        var invitation = await invitations.FindAsync(invitationId, cancellationToken);
        if (invitation is null || invitation.ProjectId != projectId)
        {
            throw new NotFoundException("Invitation not found. It may have been answered already.");
        }

        invitations.Remove(invitation);
        await SaveAsync(cancellationToken);
    }

    /// <summary>The signed-in user's pending invitations: what the notifications show.</summary>
    public async Task<IReadOnlyList<InvitationDto>> ListMineAsync(long userId, CancellationToken cancellationToken)
    {
        var pending = await invitations.ListForUserAsync(userId, cancellationToken);
        var emails = await EmailsAsync([.. pending.Select(entry => entry.Invitation)], cancellationToken);
        return [.. pending.Select(entry => ToDto(entry.Invitation, entry.Project, emails))];
    }

    /// <summary>Joins the project with the invited role; returns the project as the new member sees it.</summary>
    public async Task<ProjectDto> AcceptAsync(long userId, long invitationId, CancellationToken cancellationToken)
    {
        var invitation = await MineAsync(userId, invitationId, cancellationToken);
        var project = await FindVisibleAsync(invitation.ProjectId, cancellationToken);

        var member = project.FindMember(userId) ?? Validated(() => project.AddMember(userId, invitation.Role));
        invitations.Remove(invitation);
        await SaveAsync(cancellationToken);
        return ProjectDto.From(project, member.Role);
    }

    public async Task DeclineAsync(long userId, long invitationId, CancellationToken cancellationToken)
    {
        invitations.Remove(await MineAsync(userId, invitationId, cancellationToken));
        await SaveAsync(cancellationToken);
    }

    /// <summary>Someone else's invitation is "not found": ids can't be probed.</summary>
    private async Task<ProjectInvitation> MineAsync(long userId, long invitationId, CancellationToken cancellationToken)
    {
        var invitation = await invitations.FindAsync(invitationId, cancellationToken);
        return invitation is not null && invitation.UserId == userId
            ? invitation
            : throw new NotFoundException("Invitation not found. It may have been cancelled or answered already.");
    }

    private async Task<Project> FindVisibleAsync(long projectId, CancellationToken cancellationToken)
    {
        var project = await projects.FindAsync(projectId, cancellationToken);
        return project is null || project.Status == ProjectStatus.Deleting
            ? throw new NotFoundException("Project not found.")
            : project;
    }

    private async Task<Dictionary<long, string>> EmailsAsync(IReadOnlyList<ProjectInvitation> pending, CancellationToken cancellationToken)
    {
        var ids = pending.SelectMany(invitation => invitation.InvitedBy is long by ? new[] { invitation.UserId, by } : [invitation.UserId])
            .Distinct()
            .ToList();
        return (await users.ListByIdsAsync(ids, cancellationToken)).ToDictionary(user => user.Id, user => user.Email);
    }

    private async Task<InvitationDto> ToDtoAsync(ProjectInvitation invitation, Project project, CancellationToken cancellationToken) =>
        ToDto(invitation, project, await EmailsAsync([invitation], cancellationToken));

    private static InvitationDto ToDto(ProjectInvitation invitation, Project project, Dictionary<long, string> emails) => new(
        invitation.Id,
        project.Id,
        project.Name,
        emails.GetValueOrDefault(invitation.UserId) ?? string.Empty,
        invitation.Role,
        invitation.InvitedBy is long by ? emails.GetValueOrDefault(by) : null,
        invitation.CreatedAt);

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (ConflictException)
        {
            // A racing request invited the same person (the unique key on project and user caught it).
            throw new ConflictException("This user was just invited or answered an invitation. Refresh and try again.");
        }
    }

    private static T Validated<T>(Func<T> operation)
    {
        try
        {
            return operation();
        }
        catch (DomainException ex)
        {
            throw new ValidationFailedException(ex.Field ?? "role", ex.Message);
        }
    }

    private static string NormalizedEmail(string email)
    {
        try
        {
            return User.NormalizeEmail(email);
        }
        catch (DomainException ex)
        {
            throw new ValidationFailedException("email", ex.Message);
        }
    }
}
