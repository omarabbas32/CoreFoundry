namespace CoreFoundry.Domain.Projects;

/// <summary>Stored as TINYINT. The numeric values are storage ids, not a ranking; use <see cref="ProjectRoleExtensions.AtLeast"/>.</summary>
public enum ProjectRole : byte
{
    Owner = 1,
    Admin = 2,
    Developer = 3,
}

public static class ProjectRoleExtensions
{
    /// <summary>True when <paramref name="role"/> grants everything <paramref name="minimum"/> grants.</summary>
    public static bool AtLeast(this ProjectRole role, ProjectRole minimum) => Rank(role) >= Rank(minimum);

    private static int Rank(ProjectRole role) => role switch
    {
        ProjectRole.Owner => 3,
        ProjectRole.Admin => 2,
        ProjectRole.Developer => 1,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown project role."),
    };
}
