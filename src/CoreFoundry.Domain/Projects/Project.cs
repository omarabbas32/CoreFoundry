using System.Text.RegularExpressions;
using CoreFoundry.Domain.Common;

namespace CoreFoundry.Domain.Projects;

/// <summary>
/// A tenant. Owns its members, its draft schema and its physical database <c>cf_p_{Id}</c>.
/// </summary>
public sealed partial class Project
{
    public const int NameMaxLength = 100;
    public const int SlugMaxLength = 64;
    public const string DatabaseNamePrefix = "cf_p_";

    private readonly List<ProjectMember> _members = [];

    private Project() { } // EF Core

    /// <summary>Creates a project in <see cref="ProjectStatus.Provisioning"/> with its owner as the only member.</summary>
    public Project(string name, string slug, long ownerId)
    {
        Name = Guard.NotBlank(name, nameof(Name), NameMaxLength);
        Slug = ValidSlug(slug);
        OwnerId = Guard.PositiveId(ownerId, nameof(OwnerId));
        Status = ProjectStatus.Provisioning;
        _members.Add(new ProjectMember(ownerId, ProjectRole.Owner));
    }

    public long Id { get; private set; }
    public string Name { get; private set; } = null!;
    public string Slug { get; private set; } = null!;
    public long OwnerId { get; private set; }
    public int SchemaVersion { get; private set; }
    public ProjectStatus Status { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    public IReadOnlyCollection<ProjectMember> Members => _members.AsReadOnly();

    /// <summary>
    /// The project's physical MySQL database. Derived from <see cref="Id"/> (never stored, never user input),
    /// so it only exists after the project has been saved.
    /// </summary>
    public string DatabaseName => Id > 0
        ? DatabaseNameFor(Id)
        : throw new InvalidOperationException("The project has no Id yet; save it before using DatabaseName.");

    public static string DatabaseNameFor(long projectId) =>
        $"{DatabaseNamePrefix}{Guard.PositiveId(projectId, nameof(projectId))}";

    public void Rename(string name) => Name = Guard.NotBlank(name, nameof(Name), NameMaxLength);

    public void MarkActive() => Transition(ProjectStatus.Active, ProjectStatus.Provisioning);

    public void MarkProvisioningFailed() => Transition(ProjectStatus.Failed, ProjectStatus.Provisioning);

    public void RetryProvisioning() => Transition(ProjectStatus.Provisioning, ProjectStatus.Failed);

    public void MarkDeleting() =>
        Transition(ProjectStatus.Deleting, ProjectStatus.Active, ProjectStatus.Failed, ProjectStatus.Provisioning);

    /// <summary>Called once per successful schema apply.</summary>
    public int BumpSchemaVersion() => ++SchemaVersion;

    private void Transition(ProjectStatus to, params ProjectStatus[] allowedFrom)
    {
        if (!allowedFrom.Contains(Status))
        {
            throw new DomainException($"A project can't go from {Status} to {to}.");
        }

        Status = to;
    }

    private static string ValidSlug(string slug)
    {
        var value = Guard.NotBlank(slug, nameof(Slug), SlugMaxLength);
        return SlugPattern().IsMatch(value)
            ? value
            : throw new DomainException("Slug may only contain lower-case letters, digits and single hyphens.");
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex SlugPattern();
}
