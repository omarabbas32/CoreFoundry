using CoreFoundry.Application.Projects;
using CoreFoundry.Domain.Projects;
using Microsoft.EntityFrameworkCore;

namespace CoreFoundry.Infrastructure.Persistence;

internal sealed class ProjectRepository(MetadataDbContext db) : IProjectRepository
{
    public Task<Project?> FindAsync(long projectId, CancellationToken cancellationToken) =>
        db.Projects.Include(project => project.Members).SingleOrDefaultAsync(project => project.Id == projectId, cancellationToken);

    public async Task<IReadOnlyList<(Project Project, ProjectRole Role)>> ListForMemberAsync(
        long userId, CancellationToken cancellationToken)
    {
        var rows = await db.ProjectMembers
            .Where(member => member.UserId == userId)
            .Join(
                db.Projects.Where(project => project.Status != ProjectStatus.Deleting),
                member => member.ProjectId,
                project => project.Id,
                (member, project) => new { Project = project, member.Role })
            .OrderBy(row => row.Project.Name)
            .ToListAsync(cancellationToken);

        return [.. rows.Select(row => (row.Project, row.Role))];
    }

    public async Task<IReadOnlyList<Project>> ListByStatusAsync(ProjectStatus status, CancellationToken cancellationToken) =>
        await db.Projects.Include(project => project.Members)
            .Where(project => project.Status == status)
            .ToListAsync(cancellationToken);

    public Task<bool> SlugExistsAsync(string slug, CancellationToken cancellationToken) =>
        db.Projects.AnyAsync(project => project.Slug == slug, cancellationToken);

    public Task<ProjectRole?> GetMemberRoleAsync(long projectId, long userId, CancellationToken cancellationToken) =>
        db.ProjectMembers
            .Where(member => member.ProjectId == projectId && member.UserId == userId)
            .Select(member => (ProjectRole?)member.Role)
            .SingleOrDefaultAsync(cancellationToken);

    public void Add(Project project) => db.Projects.Add(project);

    public void Remove(Project project) => db.Projects.Remove(project);
}
