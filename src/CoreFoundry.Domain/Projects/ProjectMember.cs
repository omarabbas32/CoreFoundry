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
}
