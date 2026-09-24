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
