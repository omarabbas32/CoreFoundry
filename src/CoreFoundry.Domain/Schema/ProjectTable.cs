using CoreFoundry.Domain.Common;

namespace CoreFoundry.Domain.Schema;

/// <summary>
/// A table in a project's <b>draft</b> schema, and the aggregate root for its columns.
/// <see cref="AppliedName"/> is its name in the real database, or null if it has never been applied.
/// </summary>
/// <remarks>
/// Every change to the table or its columns increments <see cref="Version"/>, the concurrency token,
/// so two people editing the same table can't silently overwrite each other.
/// Table-name uniqueness within the project is checked by the caller, which can see the other tables.
/// </remarks>
public sealed class ProjectTable
{
    public const int NameMaxLength = IdentifierRules.MaxLength;

    private const string TableName = "Table name";
    private const string ColumnName = "Column name";

    private readonly List<ProjectColumn> _columns = [];

    private ProjectTable() { } // EF Core

    public ProjectTable(long projectId, string name)
    {
        ProjectId = Guard.PositiveId(projectId, nameof(ProjectId));
        Name = IdentifierRules.Normalize(name, TableName);
        Version = 1;
    }

    public long Id { get; private set; }
    public long ProjectId { get; private set; }
    public string Name { get; private set; } = null!;
    public string? AppliedName { get; private set; }
    public bool PendingDrop { get; private set; }
    public int Version { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    /// <summary>Who may read this table in the exported API. Metadata only: no DDL, never marks the table Changed.</summary>
    public AccessLevel ReadAccess { get; private set; } = AccessLevel.SignedIn;

    /// <summary>Who may write this table in the exported API. Never wider than <see cref="ReadAccess"/>.</summary>
    public AccessLevel WriteAccess { get; private set; } = AccessLevel.SignedIn;

    /// <summary>All columns, including ones pending drop, in <see cref="ProjectColumn.OrdinalPosition"/> order.</summary>
    public IReadOnlyList<ProjectColumn> Columns => [.. _columns.OrderBy(column => column.OrdinalPosition).ThenBy(column => column.Id)];

    public bool IsApplied => AppliedName is not null;

    public void Rename(string name)
    {
        EnsureEditable();
        var normalized = IdentifierRules.Normalize(name, TableName);
        if (normalized != Name)
        {
            Name = normalized;
            Touch();
        }
    }

    /// <summary>Adds a column after the existing ones.</summary>
    /// <exception cref="DomainException">The name is invalid or taken, the table is full, or the row would be too large.</exception>
    public ProjectColumn AddColumn(string name, ColumnDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        EnsureEditable();

        if (_columns.Count >= SchemaLimits.MaxColumnsPerTable)
        {
            throw new DomainException($"A table can have at most {SchemaLimits.MaxColumnsPerTable} columns.");
        }

        var normalized = NormalizeColumnName(name, except: null);
        EnsureRowFits(definition, except: null);

        var ordinal = _columns.Count == 0 ? 0 : _columns.Max(column => column.OrdinalPosition) + 1;
        var column = new ProjectColumn(normalized, definition, ordinal);
        _columns.Add(column);
        Touch();
        return column;
    }

    /// <summary>Renames and/or redefines a column. For an applied column the schema engine turns this into a rename/modify.</summary>
    public ProjectColumn UpdateColumn(long columnId, string name, ColumnDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        EnsureEditable();

        var column = RequireColumn(columnId);
        if (column.PendingDrop)
        {
            throw new DomainException("This column is marked for deletion. Undo the delete before editing it.");
        }

        var normalized = NormalizeColumnName(name, except: column);
        EnsureRowFits(definition, except: column);

        column.Rename(normalized);
        column.Redefine(definition);
        Touch();
        return column;
    }

    /// <summary>
    /// Deletes a column. A never-applied column is removed outright; an applied one is only marked
    /// <see cref="ProjectColumn.PendingDrop"/> so the delete can be undone until the next apply.
    /// </summary>
    /// <returns>True if the column was removed, false if it was marked pending drop.</returns>
    public bool DeleteColumn(long columnId)
    {
        EnsureEditable();
        var column = RequireColumn(columnId);

        if (!column.IsApplied)
        {
            _columns.Remove(column);
            Touch();
            return true;
        }

        if (!column.PendingDrop)
        {
            column.SetPendingDrop(true);
            Touch();
        }

        return false;
    }

    /// <summary>Undoes <see cref="DeleteColumn"/> for an applied column.</summary>
    public ProjectColumn RestoreColumn(long columnId)
    {
        EnsureEditable();
        var column = RequireColumn(columnId);
        if (!column.PendingDrop)
        {
            throw new DomainException("This column isn't marked for deletion.");
        }

        // Its bytes count again once it's back.
        EnsureRowFits(column.Definition, except: column);
        column.SetPendingDrop(false);
        Touch();
        return column;
    }

    /// <summary>Sets the column order. <paramref name="columnIds"/> must list every column (including pending drops) exactly once.</summary>
    public void ReorderColumns(IReadOnlyList<long> columnIds)
    {
        ArgumentNullException.ThrowIfNull(columnIds);
        EnsureEditable();

        if (columnIds.Count != _columns.Count
            || columnIds.Distinct().Count() != columnIds.Count
            || !columnIds.All(id => _columns.Any(column => column.Id == id)))
        {
            throw new DomainException("The new order must list every column of the table exactly once.") { Field = "columnIds" };
        }

        for (var position = 0; position < columnIds.Count; position++)
        {
            RequireColumn(columnIds[position]).MoveTo(position);
        }

        Touch();
    }

    /// <summary>Marks an applied table for deletion. Never-applied tables are deleted outright by the caller instead.</summary>
    public void MarkPendingDrop()
    {
        if (!IsApplied)
        {
            throw new DomainException("A table that was never applied is deleted, not marked for deletion.");
        }

        if (!PendingDrop)
        {
            PendingDrop = true;
            Touch();
        }
    }

    /// <summary>
    /// Records a successful apply: the table and its columns now exist under their current names,
    /// and columns that were pending drop are gone. (A table pending drop is deleted by the caller.)
    /// </summary>
    public void MarkApplied()
    {
        if (PendingDrop)
        {
            throw new DomainException("A table pending drop is removed after an apply, not marked applied.");
        }

        AppliedName = Name;
        _columns.RemoveAll(column => column.PendingDrop);
        foreach (var column in _columns)
        {
            column.MarkApplied();
        }

        Touch();
    }

    public void Restore()
    {
        if (!PendingDrop)
        {
            throw new DomainException("This table isn't marked for deletion.");
        }

        PendingDrop = false;
        Touch();
    }

    /// <summary>
    /// Sets who may read and write this table in the exported API. Write can never be wider (more open)
    /// than read. This is metadata only: no DDL, and it never marks the table's schema state Changed —
    /// it only bumps <see cref="Version"/>.
    /// </summary>
    /// <exception cref="DomainException">An undefined level, or write wider than read.</exception>
    public void SetAccess(AccessLevel read, AccessLevel write)
    {
        if (!Enum.IsDefined(read))
        {
            throw new DomainException("Read access is not a valid level.") { Field = "read" };
        }

        if (!Enum.IsDefined(write))
        {
            throw new DomainException("Write access is not a valid level.") { Field = "write" };
        }

        if (write < read)
        {
            throw new DomainException("Write access can't be wider than read access.") { Field = "write" };
        }

        ReadAccess = read;
        WriteAccess = write;
        Touch();
    }

    /// <summary>True if the column will be dropped at the next apply (on its own or with its table).</summary>
    public bool IsColumnPendingDrop(ProjectColumn column)
    {
        ArgumentNullException.ThrowIfNull(column);
        return PendingDrop || column.PendingDrop;
    }

    public ProjectColumn? FindColumn(long columnId) => _columns.Find(column => column.Id == columnId);

    private ProjectColumn RequireColumn(long columnId) =>
        FindColumn(columnId) ?? throw new DomainException("The column doesn't belong to this table.");

    /// <summary>Unique among all columns, including ones pending drop: dropping `x` and adding a new `x` in one apply isn't supported.</summary>
    private string NormalizeColumnName(string name, ProjectColumn? except)
    {
        var normalized = IdentifierRules.Normalize(name, ColumnName);
        if (_columns.Any(column => column != except && column.Name == normalized))
        {
            throw new DomainException($"The table already has a column named \"{normalized}\".") { Field = "name" };
        }

        return normalized;
    }

    /// <summary>Row-size check over the columns that will exist after the change (pending drops don't count).</summary>
    private void EnsureRowFits(ColumnDefinition changed, ProjectColumn? except) =>
        ColumnDefinitionRules.EnsureRowFits(
            _columns.Where(column => column != except && !column.PendingDrop)
                .Select(column => column.Definition)
                .Append(changed));

    private void EnsureEditable()
    {
        if (PendingDrop)
        {
            throw new DomainException("This table is marked for deletion. Undo the delete before editing it.");
        }
    }

    private void Touch() => Version++;
}
