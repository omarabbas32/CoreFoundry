using CoreFoundry.Domain.Common;

namespace CoreFoundry.Domain.Schema;

/// <summary>
/// A table in a project's <b>draft</b> schema. <see cref="AppliedName"/> is its name in the real
/// database, or null if it has never been applied.
/// </summary>
/// <remarks>Full identifier rules (regex, reserved words) arrive with the table designer in M2.</remarks>
public sealed class ProjectTable
{
    public const int NameMaxLength = 64;

    private readonly List<ProjectColumn> _columns = [];

    private ProjectTable() { } // EF Core

    public ProjectTable(long projectId, string name)
    {
        ProjectId = Guard.PositiveId(projectId, nameof(ProjectId));
        Name = Guard.NotBlank(name, nameof(Name), NameMaxLength);
    }

    public long Id { get; private set; }
    public long ProjectId { get; private set; }
    public string Name { get; private set; } = null!;
    public string? AppliedName { get; private set; }
    public bool PendingDrop { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    public IReadOnlyCollection<ProjectColumn> Columns => _columns.AsReadOnly();

    public bool IsApplied => AppliedName is not null;
}
