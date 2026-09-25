using CoreFoundry.Domain.Common;

namespace CoreFoundry.Domain.Schema;

/// <summary>
/// Rules about references between tables. A <see cref="ProjectTable"/> only sees its own columns, so
/// these take the other tables of the project as input. Callers pass only tables of the same project.
/// </summary>
public static class ReferenceRules
{
    /// <summary>The table a new or changed reference points at must exist in the project and not be pending drop.</summary>
    /// <param name="target">The referenced table as found in the same project, or null if it isn't there.</param>
    /// <exception cref="DomainException">Field <c>referencesTableId</c>.</exception>
    public static void EnsureValidTarget(ProjectTable? target)
    {
        if (target is null)
        {
            throw new DomainException("The referenced table doesn't exist in this project.") { Field = "referencesTableId" };
        }

        if (target.PendingDrop)
        {
            throw new DomainException(
                $"\"{target.Name}\" is marked for deletion. Undo that before referencing it.") { Field = "referencesTableId" };
        }
    }

    /// <summary>
    /// A table can't be deleted while live columns of <b>other</b> tables reference it (a table's
    /// references to itself go with it). The message names every such column.
    /// </summary>
    /// <param name="projectTables">All tables of the project.</param>
    public static void EnsureNotReferenced(ProjectTable table, IEnumerable<ProjectTable> projectTables)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(projectTables);

        var referencing = projectTables
            .Where(other => other.Id != table.Id)
            .SelectMany(other => other.Columns
                .Where(column => column.ReferencesTableId == table.Id && !other.IsColumnPendingDrop(column))
                .Select(column => $"{other.Name}.{column.Name}"))
            .Order(StringComparer.Ordinal)
            .ToList();

        if (referencing.Count > 0)
        {
            throw new DomainException(
                $"\"{table.Name}\" is referenced by {string.Join(", ", referencing)}. " +
                "Delete those columns or point them elsewhere first.");
        }
    }

    /// <summary>
    /// Restoring columns (or a whole table) brings their references back, so none of them may point at
    /// a table that is pending drop.
    /// </summary>
    /// <param name="findTable">Looks up a table of the same project by id.</param>
    public static void EnsureTargetsLive(IEnumerable<ProjectColumn> columns, Func<long, ProjectTable?> findTable)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(findTable);

        foreach (var column in columns)
        {
            if (column.ReferencesTableId is long targetId && findTable(targetId) is { PendingDrop: true } target)
            {
                throw new DomainException(
                    $"\"{column.Name}\" references \"{target.Name}\", which is marked for deletion. Undo that first.");
            }
        }
    }
}
