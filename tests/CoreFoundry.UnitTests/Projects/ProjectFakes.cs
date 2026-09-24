using CoreFoundry.Application.Common;
using CoreFoundry.Application.Projects;
using CoreFoundry.Domain.Projects;
using Microsoft.Extensions.Logging.Abstractions;

namespace CoreFoundry.UnitTests.Projects;

internal sealed class ProjectsHarness
{
    public ProjectsHarness()
    {
        UnitOfWork = new FakeProjectsUnitOfWork(Projects);
        Service = new ProjectService(Projects, Provisioner, UnitOfWork, NullLogger<ProjectService>.Instance);
    }

    public FakeProjects Projects { get; } = new();
    public FakeProvisioner Provisioner { get; } = new();
    public FakeProjectsUnitOfWork UnitOfWork { get; }
    public ProjectService Service { get; }

    public Project Stored(long id) => Projects.All.Single(project => project.Id == id);
}

internal sealed class FakeProjects : IProjectRepository
{
    public List<Project> All { get; } = [];

    public Task<Project?> FindAsync(long projectId, CancellationToken cancellationToken) =>
        Task.FromResult(All.SingleOrDefault(project => project.Id == projectId));

    public Task<IReadOnlyList<(Project Project, ProjectRole Role)>> ListForMemberAsync(long userId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<(Project, ProjectRole)>>(
        [
            .. All.Where(project => project.Status != ProjectStatus.Deleting)
                .SelectMany(project => project.Members
                    .Where(member => member.UserId == userId)
                    .Select(member => (project, member.Role))),
        ]);

    public Task<IReadOnlyList<Project>> ListByStatusAsync(ProjectStatus status, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Project>>([.. All.Where(project => project.Status == status)]);

    public Task<bool> SlugExistsAsync(string slug, CancellationToken cancellationToken) =>
        Task.FromResult(All.Any(project => project.Slug == slug));

    public Task<ProjectRole?> GetMemberRoleAsync(long projectId, long userId, CancellationToken cancellationToken) =>
        Task.FromResult(All.SingleOrDefault(project => project.Id == projectId)?.Members
            .Where(member => member.UserId == userId)
            .Select(member => (ProjectRole?)member.Role)
            .SingleOrDefault());

    public void Add(Project project) => All.Add(project);

    public void Remove(Project project) => All.Remove(project);
}

internal sealed class FakeProvisioner : IProjectDatabaseProvisioner
{
    public HashSet<string> Databases { get; } = [];

    public bool FailCreate { get; set; }

    public bool FailDrop { get; set; }

    public Task CreateDatabaseAsync(string databaseName, CancellationToken cancellationToken)
    {
        if (FailCreate)
        {
            throw new DatabaseProvisioningException("MySQL is down");
        }

        Databases.Add(databaseName);
        return Task.CompletedTask;
    }

    public Task DropDatabaseAsync(string databaseName, CancellationToken cancellationToken)
    {
        if (FailDrop)
        {
            throw new DatabaseProvisioningException("MySQL is down");
        }

        Databases.Remove(databaseName);
        return Task.CompletedTask;
    }
}

internal sealed class FakeProjectsUnitOfWork(FakeProjects projects) : IUnitOfWork
{
    private long _nextId = 1;

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        foreach (var project in projects.All.Where(project => project.Id == 0))
        {
            typeof(Project).GetProperty(nameof(Project.Id))!.SetValue(project, _nextId++);
        }

        return Task.CompletedTask;
    }
}
