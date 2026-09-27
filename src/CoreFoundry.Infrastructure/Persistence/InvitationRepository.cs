using CoreFoundry.Application.Projects;
using CoreFoundry.Domain.Projects;
using Microsoft.EntityFrameworkCore;

namespace CoreFoundry.Infrastructure.Persistence;

internal sealed class InvitationRepository(MetadataDbContext db) : IInvitationRepository
{
    public Task<ProjectInvitation?> FindAsync(long invitationId, CancellationToken cancellationToken) =>
        db.ProjectInvitations.SingleOrDefaultAsync(invitation => invitation.Id == invitationId, cancellationToken);

    public Task<bool> ExistsAsync(long projectId, long userId, CancellationToken cancellationToken) =>
        db.ProjectInvitations.AnyAsync(invitation => invitation.ProjectId == projectId && invitation.UserId == userId, cancellationToken);

    public async Task<IReadOnlyList<ProjectInvitation>> ListForProjectAsync(long projectId, CancellationToken cancellationToken) =>
        await db.ProjectInvitations
            .Where(invitation => invitation.ProjectId == projectId)
            .OrderBy(invitation => invitation.Id)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<(ProjectInvitation Invitation, Project Project)>> ListForUserAsync(
        long userId, CancellationToken cancellationToken)
    {
        var rows = await db.ProjectInvitations
            .Where(invitation => invitation.UserId == userId)
            .Join(db.Projects, invitation => invitation.ProjectId, project => project.Id, (invitation, project) => new { invitation, project })
            .Where(row => row.project.Status != ProjectStatus.Deleting)
            .OrderByDescending(row => row.invitation.Id)
            .ToListAsync(cancellationToken);
        return [.. rows.Select(row => (row.invitation, row.project))];
    }

    public void Add(ProjectInvitation invitation) => db.ProjectInvitations.Add(invitation);

    public void Remove(ProjectInvitation invitation) => db.ProjectInvitations.Remove(invitation);
}
