using CoreFoundry.Domain.Schema;

namespace CoreFoundry.Application.Schema;

/// <summary>Draft tables of a project. Every lookup is scoped by project id, so ids from another project are never found.</summary>
public interface ITableRepository
{
    /// <summary>All tables of the project (including ones pending drop) with their columns, by name.</summary>
    Task<IReadOnlyList<ProjectTable>> ListAsync(long projectId, CancellationToken cancellationToken);

    /// <summary>The table with its columns, or null if it doesn't exist in this project.</summary>
    Task<ProjectTable?> FindAsync(long projectId, long tableId, CancellationToken cancellationToken);

    Task<int> CountAsync(long projectId, CancellationToken cancellationToken);

    /// <summary>Id → name of every table of the project (for showing what columns reference).</summary>
    Task<IReadOnlyDictionary<long, string>> ListNamesAsync(long projectId, CancellationToken cancellationToken);

    /// <summary>True if another table of the project (pending drop included) already has this name.</summary>
    Task<bool> NameExistsAsync(long projectId, string name, long? exceptTableId, CancellationToken cancellationToken);

    void Add(ProjectTable table);

    void Remove(ProjectTable table);
}
