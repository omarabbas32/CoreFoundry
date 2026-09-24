using CoreFoundry.Domain.Schema;
using CoreFoundry.Domain.SchemaEngine;

namespace CoreFoundry.Application.SchemaEngine;

/// <summary>Turns schema operations into SQL statements for one project database. No I/O.</summary>
public interface ISqlRenderer
{
    IReadOnlyList<string> Render(string databaseName, IReadOnlyList<SchemaOperation> operations);
}

/// <summary>Reads the real schema of a project database (as <c>cf_engine</c>).</summary>
public interface ISchemaIntrospector
{
    /// <summary>The database's tables and columns as a <see cref="SchemaModel"/> (without the system <c>id</c>).</summary>
    Task<SchemaModel> ReadAsync(string databaseName, CancellationToken cancellationToken);

    /// <summary>How many rows of <paramref name="table"/> have NULL in <paramref name="column"/>.</summary>
    Task<long> CountNullsAsync(string databaseName, string table, string column, CancellationToken cancellationToken);

    Task<bool> HasRowsAsync(string databaseName, string table, CancellationToken cancellationToken);
}

/// <summary>Runs applies: one dedicated <c>cf_engine</c> session per apply, holding the project's lock.</summary>
public interface ISchemaEngine
{
    /// <summary>
    /// Opens a session and takes the project's apply lock without waiting. Null if another apply holds it.
    /// The lock belongs to the session, so everything in the apply runs on it; disposing releases it.
    /// </summary>
    Task<IApplySession?> TryLockAsync(long projectId, CancellationToken cancellationToken);
}

public interface IApplySession : IAsyncDisposable
{
    Task<SchemaModel> ReadSchemaAsync(string databaseName, CancellationToken cancellationToken);

    /// <exception cref="SchemaStatementFailedException">MySQL refused the statement.</exception>
    Task ExecuteAsync(string sql, CancellationToken cancellationToken);
}

/// <summary>The <c>SchemaMigrations</c> journal of a project.</summary>
public interface ISchemaMigrationRepository
{
    void Add(SchemaMigration migration);

    Task<SchemaMigration?> FindAsync(long projectId, long migrationId, CancellationToken cancellationToken);

    /// <summary>Newest first.</summary>
    Task<IReadOnlyList<SchemaMigration>> ListAsync(long projectId, int skip, int take, CancellationToken cancellationToken);

    Task<int> CountAsync(long projectId, CancellationToken cancellationToken);

    /// <summary>The most recent migration with status Applied, or null.</summary>
    Task<SchemaMigration?> LatestAppliedAsync(long projectId, CancellationToken cancellationToken);
}

/// <summary>A DDL statement failed in MySQL. The message is MySQL's.</summary>
public sealed class SchemaStatementFailedException : Exception
{
    public SchemaStatementFailedException(string message, Exception innerException) : base(message, innerException) { }

    public SchemaStatementFailedException(string message) : base(message) { }

    public SchemaStatementFailedException() { }
}
