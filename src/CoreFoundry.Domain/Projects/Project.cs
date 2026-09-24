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

    /// <summary>Longest slug <see cref="SlugFrom"/> produces, leaving room for a "-&lt;n&gt;" collision suffix.</summary>
    public const int SlugBaseMaxLength = SlugMaxLength - 8;

    /// <summary>
    /// A URL-safe slug from a display name: lower-case ASCII letters and digits joined by single hyphens.
    /// Names with no usable characters (e.g. only Arabic script or emoji) fall back to "project".
    /// </summary>
    public static string SlugFrom(string name)
    {
        var slug = new System.Text.StringBuilder(SlugBaseMaxLength);
        var pendingHyphen = false;
        foreach (var ch in (name ?? string.Empty).ToLowerInvariant())
        {
            if (char.IsAsciiLetterLower(ch) || char.IsAsciiDigit(ch))
            {
                if (pendingHyphen && slug.Length > 0)
                {
                    slug.Append('-');
                }

                slug.Append(ch);
                pendingHyphen = false;
            }
            else
            {
                pendingHyphen = true;
            }

            if (slug.Length >= SlugBaseMaxLength)
            {
                break;
            }
        }

        var result = slug.ToString(0, Math.Min(slug.Length, SlugBaseMaxLength)).TrimEnd('-');
        return result.Length > 0 ? result : "project";
    }

    /// <summary>The slug to try for the <paramref name="attempt"/>-th collision: "shop", "shop-2", "shop-3", …</summary>
    public static string SlugCandidate(string baseSlug, int attempt) =>
        attempt <= 1 ? baseSlug : $"{baseSlug}-{attempt}";

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
