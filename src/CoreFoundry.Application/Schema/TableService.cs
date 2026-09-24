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
        await ToDtoAsync(await FindAsync(projectId, tableId, cancellationToken), cancellationToken);

    /// <summary>Every table of the project with its columns: the whole draft schema (for the diagram).</summary>
    public async Task<IReadOnlyList<TableDto>> GetSchemaAsync(long projectId, CancellationToken cancellationToken)
    {
        await EnsureProjectVisibleAsync(projectId, cancellationToken);
        var all = await tables.ListAsync(projectId, cancellationToken);
        var names = all.ToDictionary(table => table.Id, table => table.Name);
        return [.. all.Select(table => TableDto.From(table, names))];
    }

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
                var definition = Definition(input);
                await EnsureReferenceTargetAsync(projectId, table, definition, cancellationToken);
                table.AddColumn(input.Name, definition);
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
        return await ToDtoAsync(table, cancellationToken);
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
        ReferenceRules.EnsureNotReferenced(table, await tables.ListAsync(projectId, cancellationToken));

        if (!table.IsApplied)
        {
            tables.Remove(table);
            await SaveAsync(cancellationToken);
            return null;
        }

        table.MarkPendingDrop();
        await SaveAsync(cancellationToken);
        return await ToDtoAsync(table, cancellationToken);
    }

    /// <summary>Undoes a pending drop. Refused while one of its columns references a table that is itself pending drop.</summary>
    public Task<TableDto> RestoreAsync(long projectId, long tableId, int version, CancellationToken cancellationToken) =>
        ChangeAsync(projectId, tableId, version, async table =>
        {
            var lookup = await TableLookupAsync(projectId, cancellationToken);
            // References to the table itself come back with it.
            ReferenceRules.EnsureTargetsLive(
                table.Columns.Where(column => !column.PendingDrop && column.ReferencesTableId != table.Id), lookup);
            Validated(table.Restore);
        }, cancellationToken);

    public Task<TableDto> AddColumnAsync(long projectId, long tableId, int version, ColumnInput input, CancellationToken cancellationToken) =>
        ChangeAsync(projectId, tableId, version, async table =>
        {
            var definition = Validated(() => Definition(input));
            await ValidatedAsync(() => EnsureReferenceTargetAsync(projectId, table, definition, cancellationToken));
            Validated(() => table.AddColumn(input.Name, definition));
        }, cancellationToken);

    public Task<TableDto> UpdateColumnAsync(
        long projectId, long tableId, long columnId, int version, ColumnInput input, CancellationToken cancellationToken) =>
        ChangeAsync(projectId, tableId, version, async table =>
        {
            RequireColumn(table, columnId);
            var definition = Validated(() => Definition(input));
            await ValidatedAsync(() => EnsureReferenceTargetAsync(projectId, table, definition, cancellationToken));
            Validated(() => table.UpdateColumn(columnId, input.Name, definition));
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
        ChangeAsync(projectId, tableId, version, async table =>
        {
            RequireColumn(table, columnId);
            ReferenceRules.EnsureTargetsLive([table.FindColumn(columnId)!], await TableLookupAsync(projectId, cancellationToken));
            Validated(() => table.RestoreColumn(columnId));
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
        return await ToDtoAsync(table, cancellationToken);
    }

    private async Task<TableDto> ToDtoAsync(ProjectTable table, CancellationToken cancellationToken) =>
        TableDto.From(table, await tables.ListNamesAsync(table.ProjectId, cancellationToken));

    /// <summary>The referenced table must be in this project (a self-reference is the table itself) and not pending drop.</summary>
    private async Task EnsureReferenceTargetAsync(
        long projectId, ProjectTable table, ColumnDefinition definition, CancellationToken cancellationToken)
    {
        if (definition.ReferencesTableId is not long targetId)
        {
            return;
        }

        var target = table.Id != 0 && targetId == table.Id
            ? table
            : await tables.FindAsync(projectId, targetId, cancellationToken);
        ReferenceRules.EnsureValidTarget(target);
    }

    private async Task<Func<long, ProjectTable?>> TableLookupAsync(long projectId, CancellationToken cancellationToken)
    {
        var byId = (await tables.ListAsync(projectId, cancellationToken)).ToDictionary(table => table.Id);
        return id => byId.GetValueOrDefault(id);
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
            input.DataType, input.Length, input.Precision, input.Scale, input.IsNullable, input.IsUnique, input.DefaultValue,
            input.ReferencesTableId, input.OnDelete);

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

    private static async Task ValidatedAsync(Func<Task> operation)
    {
        try
        {
            await operation();
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
