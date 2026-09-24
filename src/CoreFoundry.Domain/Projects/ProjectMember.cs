namespace CoreFoundry.Domain.Projects;

public sealed class ProjectMember
{
    private ProjectMember() { } // EF Core

    internal ProjectMember(long userId, ProjectRole role)
    {
        UserId = userId;
        Role = role;
    }

    public long ProjectId { get; private set; }
    public long UserId { get; private set; }
    public ProjectRole Role { get; private set; }
    public DateTime CreatedAt { get; private set; }

    /// <summary>Only <see cref="Project"/> changes roles, so it can keep "exactly one Owner" true.</summary>
    internal void ChangeRole(ProjectRole role) => Role = role;
}
