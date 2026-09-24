using CoreFoundry.Domain.Projects;

namespace CoreFoundry.Application.Projects;

/// <param name="Role">The caller's role in this project.</param>
public sealed record ProjectDto(
    long Id,
    string Name,
    string Slug,
    ProjectStatus Status,
    ProjectRole Role,
    int SchemaVersion,
    string DatabaseName,
    DateTime CreatedAt)
{
    public static ProjectDto From(Project project, ProjectRole role) => new(
        project.Id, project.Name, project.Slug, project.Status, role,
        project.SchemaVersion, project.DatabaseName, project.CreatedAt);
}
