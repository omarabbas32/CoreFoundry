using CoreFoundry.Domain.SchemaEngine;

namespace CoreFoundry.Application.SchemaEngine;

/// <summary>Turns schema operations into SQL statements for one project database. No I/O.</summary>
public interface ISqlRenderer
{
    IReadOnlyList<string> Render(string databaseName, IReadOnlyList<SchemaOperation> operations);
}
