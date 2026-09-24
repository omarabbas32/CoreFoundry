using CoreFoundry.Application.Common;
using CoreFoundry.Domain.Common;
using CoreFoundry.Domain.Projects;
using Microsoft.Extensions.Logging;

namespace CoreFoundry.Application.Projects;

/// <summary>
/// Project lifecycle. The metadata row and the physical database can't share a transaction
/// (MySQL commits DDL immediately), so <see cref="ProjectStatus"/> records how far each step got and
/// <see cref="RecoverAsync"/> finishes anything a crash left half-done.
/// </summary>
/// <remarks>Authorization (membership and role) is enforced by the API before these methods run.</remarks>
public sealed partial class ProjectService(
    IProjectRepository projects,
    IProjectDatabaseProvisioner provisioner,
    IUnitOfWork unitOfWork,
    ILogger<ProjectService> logger)
{
    private const int MaxSlugAttempts = 50;

    public async Task<IReadOnlyList<ProjectDto>> ListForUserAsync(long userId, CancellationToken cancellationToken) =>
        [.. (await projects.ListForMemberAsync(userId, cancellationToken))
            .Select(entry => ProjectDto.From(entry.Project, entry.Role))];

    public async Task<ProjectDto> GetAsync(long projectId, long userId, CancellationToken cancellationToken)
    {
        var project = await FindVisibleAsync(projectId, cancellationToken);
        return ProjectDto.From(project, await RoleOfAsync(projectId, userId, cancellationToken));
    }

    /// <summary>Saves the project (Provisioning), then creates <c>cf_p_{Id}</c> and marks it Active or Failed.</summary>
    public async Task<ProjectDto> CreateAsync(long ownerId, string name, CancellationToken cancellationToken)
    {
        var slug = await AvailableSlugAsync(Project.SlugFrom(name), cancellationToken);
        var project = NewProject(name, slug, ownerId);
        projects.Add(project);
        await unitOfWork.SaveChangesAsync(cancellationToken); // assigns Id → DatabaseName

        await ProvisionAsync(project, cancellationToken);
        return ProjectDto.From(project, ProjectRole.Owner);
    }

    public async Task<ProjectDto> RenameAsync(long projectId, long userId, string name, CancellationToken cancellationToken)
    {
        var project = await FindVisibleAsync(projectId, cancellationToken);
        try
        {
            project.Rename(name);
        }
        catch (DomainException ex)
        {
            throw new ValidationFailedException("name", ex.Message);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return ProjectDto.From(project, await RoleOfAsync(projectId, userId, cancellationToken));
    }

    /// <summary>Only a <see cref="ProjectStatus.Failed"/> project can be retried.</summary>
    public async Task<ProjectDto> RetryProvisioningAsync(long projectId, long userId, CancellationToken cancellationToken)
    {
        var project = await FindVisibleAsync(projectId, cancellationToken);
        project.RetryProvisioning();
        await unitOfWork.SaveChangesAsync(cancellationToken);

        await ProvisionAsync(project, cancellationToken);
        return ProjectDto.From(project, await RoleOfAsync(projectId, userId, cancellationToken));
    }

    /// <summary>
    /// Marks the project Deleting (it disappears from lists at once), drops its database, then removes
    /// the metadata. If the drop fails the project stays Deleting and recovery finishes the job.
    /// </summary>
    public async Task DeleteAsync(long projectId, CancellationToken cancellationToken)
    {
        var project = await FindVisibleAsync(projectId, cancellationToken);
        project.MarkDeleting();
        await unitOfWork.SaveChangesAsync(cancellationToken);

        await FinishDeletingAsync(project, cancellationToken);
    }

    /// <summary>Finishes projects left in Provisioning or Deleting by a crash. Safe to run repeatedly.</summary>
    public async Task RecoverAsync(CancellationToken cancellationToken)
    {
        foreach (var project in await projects.ListByStatusAsync(ProjectStatus.Provisioning, cancellationToken))
        {
            LogRecovering(project.Id, ProjectStatus.Provisioning);
            await ProvisionAsync(project, cancellationToken);
        }

        foreach (var project in await projects.ListByStatusAsync(ProjectStatus.Deleting, cancellationToken))
        {
            LogRecovering(project.Id, ProjectStatus.Deleting);
            try
            {
                await FinishDeletingAsync(project, cancellationToken);
            }
            catch (DatabaseProvisioningException)
            {
                // Already logged; the next startup tries again.
            }
        }
    }

    public Task<ProjectRole?> GetMemberRoleAsync(long projectId, long userId, CancellationToken cancellationToken) =>
        projects.GetMemberRoleAsync(projectId, userId, cancellationToken);

    private async Task ProvisionAsync(Project project, CancellationToken cancellationToken)
    {
        try
        {
            await provisioner.CreateDatabaseAsync(project.DatabaseName, cancellationToken);
            project.MarkActive();
        }
        catch (DatabaseProvisioningException ex)
        {
            LogProvisioningFailed(ex, project.Id, project.DatabaseName);
            project.MarkProvisioningFailed();
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    private async Task FinishDeletingAsync(Project project, CancellationToken cancellationToken)
    {
        try
        {
            await provisioner.DropDatabaseAsync(project.DatabaseName, cancellationToken);
        }
        catch (DatabaseProvisioningException ex)
        {
            LogDropFailed(ex, project.Id, project.DatabaseName);
            throw new DatabaseProvisioningException(
                "The project is being deleted, but its database couldn't be dropped yet. It will be retried automatically.", ex);
        }

        projects.Remove(project); // members, draft tables and migrations cascade
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    private async Task<Project> FindVisibleAsync(long projectId, CancellationToken cancellationToken)
    {
        var project = await projects.FindAsync(projectId, cancellationToken);
        return project is null || project.Status == ProjectStatus.Deleting
            ? throw new NotFoundException("Project not found.")
            : project;
    }

    private async Task<ProjectRole> RoleOfAsync(long projectId, long userId, CancellationToken cancellationToken) =>
        await projects.GetMemberRoleAsync(projectId, userId, cancellationToken)
        ?? throw new NotFoundException("Project not found.");

    private async Task<string> AvailableSlugAsync(string baseSlug, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxSlugAttempts; attempt++)
        {
            var candidate = Project.SlugCandidate(baseSlug, attempt);
            if (!await projects.SlugExistsAsync(candidate, cancellationToken))
            {
                return candidate;
            }
        }

        throw new ConflictException("Too many projects share this name; choose a different one.");
    }

    private static Project NewProject(string name, string slug, long ownerId)
    {
        try
        {
            return new Project(name, slug, ownerId);
        }
        catch (DomainException ex)
        {
            throw new ValidationFailedException("name", ex.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Creating database {DatabaseName} for project {ProjectId} failed")]
    private partial void LogProvisioningFailed(Exception exception, long projectId, string databaseName);

    [LoggerMessage(Level = LogLevel.Error, Message = "Dropping database {DatabaseName} for project {ProjectId} failed")]
    private partial void LogDropFailed(Exception exception, long projectId, string databaseName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recovering project {ProjectId} stuck in {Status}")]
    private partial void LogRecovering(long projectId, ProjectStatus status);
}
