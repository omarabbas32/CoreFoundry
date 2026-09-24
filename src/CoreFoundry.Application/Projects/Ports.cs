using CoreFoundry.Domain.Projects;

namespace CoreFoundry.Application.Projects;

public interface IProjectRepository
{
    /// <summary>The project with its members, or null.</summary>
    Task<Project?> FindAsync(long projectId, CancellationToken cancellationToken);

    /// <summary>Projects the user is a member of, with their role, excluding ones being deleted.</summary>
    Task<IReadOnlyList<(Project Project, ProjectRole Role)>> ListForMemberAsync(long userId, CancellationToken cancellationToken);

    Task<IReadOnlyList<Project>> ListByStatusAsync(ProjectStatus status, CancellationToken cancellationToken);

    Task<bool> SlugExistsAsync(string slug, CancellationToken cancellationToken);

    /// <summary>The user's role in the project, or null if they are not a member.</summary>
    Task<ProjectRole?> GetMemberRoleAsync(long projectId, long userId, CancellationToken cancellationToken);

    void Add(Project project);

    void Remove(Project project);
}

/// <summary>Creates and drops a project's physical database (<c>cf_p_{Id}</c>). Both operations are idempotent.</summary>
public interface IProjectDatabaseProvisioner
{
    /// <exception cref="Common.DatabaseProvisioningException">MySQL refused or was unreachable.</exception>
    Task CreateDatabaseAsync(string databaseName, CancellationToken cancellationToken);

    /// <exception cref="Common.DatabaseProvisioningException">MySQL refused or was unreachable.</exception>
    Task DropDatabaseAsync(string databaseName, CancellationToken cancellationToken);
}
