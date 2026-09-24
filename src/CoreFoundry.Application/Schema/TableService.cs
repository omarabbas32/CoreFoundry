using CoreFoundry.Application.Common;
using CoreFoundry.Application.Projects;
using CoreFoundry.Domain.Common;
using CoreFoundry.Domain.Projects;
using CoreFoundry.Domain.Schema;

namespace CoreFoundry.Application.Schema;

/// <summary>
/// The table designer: edits a project's <b>draft</b> schema in the metadata database. Nothing here
/// touches the project's MySQL database; the schema engine (M3) applies drafts.
/// </summary>
/// <remarks>
/// Every change to an existing table takes the <c>version</c> the caller last saw. If someone else
/// changed the table since, the change is refused with a conflict instead of overwriting theirs.
/// </remarks>
public sealed class TableService(IProjectRepository projects, ITableRepository tables, IUnitOfWork unitOfWork)
{
    public async Task<IReadOnlyList<TableSummaryDto>> ListAsync(long projectId, CancellationToken cancellationToken)
    {
        await EnsureProjectVisibleAsync(projectId, cancellationToken);
        return [.. (await tables.ListAsync(projectId, cancellationToken)).Select(TableDto.SummaryFrom)];
    }

    public async Task<TableDto> GetAsync(long projectId, long tableId, CancellationToken cancellationToken) =>
        TableDto.From(await FindAsync(projectId, tableId, cancellationToken));

    /// <summary>Creates a table, optionally with its first columns. Errors are keyed like <c>columns[2].length</c>.</summary>
    public async Task<TableDto> CreateAsync(
        long projectId, string name, IReadOnlyList<ColumnInput>? columns, CancellationToken cancellationToken)
    {
        await EnsureProjectVisibleAsync(projectId, cancellationToken);

        if (await tables.CountAsync(projectId, cancellationToken) >= SchemaLimits.MaxTablesPerProject)
        {
            throw new DomainException($"A project can have at most {SchemaLimits.MaxTablesPerProject} tables.");
        }

        var table = Validated(() => new ProjectTable(projectId, name));
        await EnsureNameFreeAsync(projectId, table.Name, exceptTableId: null, cancellationToken);

        var errors = new Dictionary<string, string[]>();
        for (var index = 0; index < (columns?.Count ?? 0); index++)
        {
            var input = columns![index];
            try
            {
                table.AddColumn(input.Name, Definition(input));
            }
            catch (DomainException ex)
            {
                errors[$"columns[{index}].{ex.Field ?? "name"}"] = [ex.Message];
            }
        }

        if (errors.Count > 0)
        {
            throw new ValidationFailedException(errors);
        }

        tables.Add(table);
        await SaveAsync(cancellationToken);
        return TableDto.From(table);
    }

    public Task<TableDto> RenameAsync(long projectId, long tableId, int version, string name, CancellationToken cancellationToken) =>
        ChangeAsync(projectId, tableId, version, async table =>
        {
            var normalized = Validated(() => IdentifierRules.Normalize(name, "Table name"));
            await EnsureNameFreeAsync(projectId, normalized, tableId, cancellationToken);
            Validated(() => table.Rename(normalized));
        }, cancellationToken);

    /// <summary>
    /// Deletes a never-applied table outright (returns null) or marks an applied one pending drop,
    /// which can be undone with <see cref="RestoreAsync"/> until the next apply.
    /// </summary>
    public async Task<TableDto?> DeleteAsync(long projectId, long tableId, int version, CancellationToken cancellationToken)
    {
        var table = await FindForChangeAsync(projectId, tableId, version, cancellationToken);
        if (!table.IsApplied)
        {
            tables.Remove(table);
            await SaveAsync(cancellationToken);
            return null;
        }

        table.MarkPendingDrop();
        await SaveAsync(cancellationToken);
        return TableDto.From(table);
    }

    public Task<TableDto> RestoreAsync(long projectId, long tableId, int version, CancellationToken cancellationToken) =>
        ChangeAsync(projectId, tableId, version, table =>
        {
            Validated(table.Restore);
            return Task.CompletedTask;
        }, cancellationToken);

    public Task<TableDto> AddColumnAsync(long projectId, long tableId, int version, ColumnInput input, CancellationToken cancellationToken) =>
        ChangeAsync(projectId, tableId, version, table =>
        {
            Validated(() => table.AddColumn(input.Name, Definition(input)));
            return Task.CompletedTask;
        }, cancellationToken);

    public Task<TableDto> UpdateColumnAsync(
        long projectId, long tableId, long columnId, int version, ColumnInput input, CancellationToken cancellationToken) =>
        ChangeAsync(projectId, tableId, version, table =>
        {
            RequireColumn(table, columnId);
            Validated(() => table.UpdateColumn(columnId, input.Name, Definition(input)));
            return Task.CompletedTask;
        }, cancellationToken);

    /// <summary>Removes a never-applied column; marks an applied one pending drop (undo with <see cref="RestoreColumnAsync"/>).</summary>
    public Task<TableDto> DeleteColumnAsync(long projectId, long tableId, long columnId, int version, CancellationToken cancellationToken) =>
        ChangeAsync(projectId, tableId, version, table =>
        {
            RequireColumn(table, columnId);
            Validated(() => table.DeleteColumn(columnId));
            return Task.CompletedTask;
        }, cancellationToken);

    public Task<TableDto> RestoreColumnAsync(long projectId, long tableId, long columnId, int version, CancellationToken cancellationToken) =>
        ChangeAsync(projectId, tableId, version, table =>
        {
            RequireColumn(table, columnId);
            Validated(() => table.RestoreColumn(columnId));
            return Task.CompletedTask;
        }, cancellationToken);

    public Task<TableDto> ReorderColumnsAsync(
        long projectId, long tableId, int version, IReadOnlyList<long> columnIds, CancellationToken cancellationToken) =>
        ChangeAsync(projectId, tableId, version, table =>
        {
            Validated(() => table.ReorderColumns(columnIds));
            return Task.CompletedTask;
        }, cancellationToken);

    private async Task<TableDto> ChangeAsync(
        long projectId, long tableId, int version, Func<ProjectTable, Task> change, CancellationToken cancellationToken)
    {
        var table = await FindForChangeAsync(projectId, tableId, version, cancellationToken);
        await change(table);
        await SaveAsync(cancellationToken);
        return TableDto.From(table);
    }

    private async Task<ProjectTable> FindForChangeAsync(long projectId, long tableId, int version, CancellationToken cancellationToken)
    {
        var table = await FindAsync(projectId, tableId, cancellationToken);
        return table.Version == version ? table : throw StaleVersion();
    }

    private async Task<ProjectTable> FindAsync(long projectId, long tableId, CancellationToken cancellationToken)
    {
        await EnsureProjectVisibleAsync(projectId, cancellationToken);
        return await tables.FindAsync(projectId, tableId, cancellationToken)
            ?? throw new NotFoundException("Table not found.");
    }

    private async Task EnsureProjectVisibleAsync(long projectId, CancellationToken cancellationToken)
    {
        var project = await projects.FindAsync(projectId, cancellationToken);
        if (project is null || project.Status == ProjectStatus.Deleting)
        {
            throw new NotFoundException("Project not found.");
        }
    }

    private async Task EnsureNameFreeAsync(long projectId, string name, long? exceptTableId, CancellationToken cancellationToken)
    {
        if (await tables.NameExistsAsync(projectId, name, exceptTableId, cancellationToken))
        {
            throw new ValidationFailedException("name", $"The project already has a table named \"{name}\".");
        }
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyConflictException ex)
        {
            throw StaleVersion(ex);
        }
    }

    private static void RequireColumn(ProjectTable table, long columnId)
    {
        if (table.FindColumn(columnId) is null)
        {
            throw new NotFoundException("Column not found.");
        }
    }

    private static ColumnDefinition Definition(ColumnInput input) =>
        ColumnDefinitionRules.Create(
            input.DataType, input.Length, input.Precision, input.Scale, input.IsNullable, input.IsUnique, input.DefaultValue);

    /// <summary>Runs a domain operation, turning a field-specific rule violation into a 400 keyed by that field.</summary>
    private static T Validated<T>(Func<T> operation)
    {
        try
        {
            return operation();
        }
        catch (DomainException ex) when (ex.Field is not null)
        {
            throw new ValidationFailedException(ex.Field, ex.Message);
        }
    }

    private static void Validated(Action operation) =>
        Validated(() =>
        {
            operation();
            return true;
        });

    private const string StaleVersionMessage = "This table was changed by someone else. Reload it to see the latest version.";

    private static ConflictException StaleVersion() => new(StaleVersionMessage);

    private static ConflictException StaleVersion(Exception inner) => new(StaleVersionMessage, inner);
}
