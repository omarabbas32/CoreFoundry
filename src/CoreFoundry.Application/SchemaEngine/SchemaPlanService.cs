using System.Text.Json;
using CoreFoundry.Application.Common;
using CoreFoundry.Application.Projects;
using CoreFoundry.Application.Schema;
using CoreFoundry.Domain.Projects;
using CoreFoundry.Domain.Schema;
using CoreFoundry.Domain.SchemaEngine;

namespace CoreFoundry.Application.SchemaEngine;

public sealed record PlanOperationDto(string Kind, string Table, string Description, OperationRisk Risk, string? RiskReason);

/// <param name="PlanHash">Send it back to apply exactly this plan.</param>
/// <param name="Warnings">Risky and destructive changes, with what the data says (e.g. how many NULLs).</param>
/// <param name="UnmanagedTables">Tables in the database that CoreFoundry doesn't manage; never dropped.</param>
public sealed record SchemaPlanDto(
    string PlanHash,
    int SchemaVersion,
    IReadOnlyList<PlanOperationDto> Operations,
    IReadOnlyList<string> Statements,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> UnmanagedTables,
    IReadOnlyList<string> UnmanagedColumns,
    bool HasDestructive);

public sealed record MigrationSummaryDto(
    long Id,
    int Version,
    MigrationStatus Status,
    int StatementCount,
    int StatementsApplied,
    long? RequestedBy,
    DateTime CreatedAt,
    DateTime? CompletedAt,
    string? Error);

public sealed record MigrationDto(
    long Id,
    int Version,
    MigrationStatus Status,
    int StatementCount,
    int StatementsApplied,
    IReadOnlyList<string> Statements,
    long? RequestedBy,
    DateTime CreatedAt,
    DateTime? CompletedAt,
    string? Error)
{
    /// <summary>1-based index of the statement that failed, when <see cref="Status"/> is Failed.</summary>
    public int? FailedStatement => Status == MigrationStatus.Failed ? StatementsApplied + 1 : null;
}

public sealed record MigrationPageDto(IReadOnlyList<MigrationSummaryDto> Items, int Total, int Page, int PageSize);

/// <param name="Differences">Changes made to the database outside CoreFoundry since the last apply.</param>
public sealed record DriftDto(IReadOnlyList<string> Differences, int? SinceVersion);

/// <summary>
/// Plans (diff the draft against the real database, render the SQL, hash it), and serves the
/// migration history and drift. Planning only reads the project database.
/// </summary>
public sealed class SchemaPlanService(
    IProjectRepository projects,
    ITableRepository tables,
    ISchemaMigrationRepository migrations,
    ISchemaIntrospector introspector,
    ISqlRenderer renderer)
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;

    public async Task<SchemaPlanDto> PlanAsync(long projectId, CancellationToken cancellationToken)
    {
        var project = await ReadyProjectAsync(projects, projectId, cancellationToken);
        var desired = DraftSchema.From(await tables.ListAsync(projectId, cancellationToken));
        var actual = await introspector.ReadAsync(project.DatabaseName, cancellationToken);
        var plan = SchemaPlan.Build(desired, actual, project, renderer);

        return new SchemaPlanDto(
            plan.Hash,
            project.SchemaVersion,
            [.. plan.Diff.Operations.Select(operation => new PlanOperationDto(
                operation.GetType().Name, operation.Table, operation.Describe(), operation.Risk, operation.RiskReason))],
            plan.Statements,
            await WarningsAsync(project.DatabaseName, plan.Diff, cancellationToken),
            plan.Diff.UnmanagedTables,
            plan.Diff.UnmanagedColumns,
            plan.Diff.HasDestructive);
    }

    public async Task<MigrationPageDto> ListMigrationsAsync(long projectId, int page, int pageSize, CancellationToken cancellationToken)
    {
        await VisibleProjectAsync(projects, projectId, cancellationToken);
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        var items = await migrations.ListAsync(projectId, (page - 1) * pageSize, pageSize, cancellationToken);
        return new MigrationPageDto(
            [.. items.Select(migration => new MigrationSummaryDto(
                migration.Id, migration.Version, migration.Status, migration.StatementCount, migration.StatementsApplied,
                migration.RequestedBy, migration.CreatedAt, migration.CompletedAt, migration.Error))],
            await migrations.CountAsync(projectId, cancellationToken),
            page,
            pageSize);
    }

    public async Task<MigrationDto> GetMigrationAsync(long projectId, long migrationId, CancellationToken cancellationToken)
    {
        await VisibleProjectAsync(projects, projectId, cancellationToken);
        var migration = await migrations.FindAsync(projectId, migrationId, cancellationToken)
            ?? throw new NotFoundException("Migration not found.");
        return new MigrationDto(
            migration.Id, migration.Version, migration.Status, migration.StatementCount, migration.StatementsApplied,
            JsonSerializer.Deserialize<string[]>(migration.StatementsJson) ?? [],
            migration.RequestedBy, migration.CreatedAt, migration.CompletedAt, migration.Error);
    }

    /// <summary>Compares the database now with the snapshot taken after the last successful apply.</summary>
    public async Task<DriftDto> DriftAsync(long projectId, CancellationToken cancellationToken)
    {
        var project = await ReadyProjectAsync(projects, projectId, cancellationToken);
        var latest = await migrations.LatestAppliedAsync(projectId, cancellationToken);
        var now = SchemaSnapshot.From(await introspector.ReadAsync(project.DatabaseName, cancellationToken));
        return new DriftDto(SchemaSnapshot.FromJson(latest?.SnapshotJson).DifferencesTo(now), latest?.Version);
    }

    /// <summary>Turns each risk into a sentence, checking the data where that makes the warning precise.</summary>
    private async Task<IReadOnlyList<string>> WarningsAsync(string database, SchemaDiff diff, CancellationToken cancellationToken)
    {
        // Operations after the rename phase use the new table names; the data is still under the old ones.
        var physical = diff.Operations.OfType<RenameTable>().ToDictionary(rename => rename.NewName, rename => rename.Table, StringComparer.Ordinal);
        string Physical(string table) => physical.GetValueOrDefault(table, table);
        var created = diff.Operations.OfType<CreateTable>().Select(create => create.Table).ToHashSet(StringComparer.Ordinal);

        var warnings = new List<string>();
        foreach (var operation in diff.Operations.Where(operation => operation.Risk != OperationRisk.Safe))
        {
            switch (operation)
            {
                case ModifyColumn { BecomesNotNull: true, Risk: OperationRisk.Risky } modify:
                    var nulls = await introspector.CountNullsAsync(database, Physical(modify.Table), modify.Column, cancellationToken);
                    warnings.Add(nulls == 0
                        ? $"{modify.Table}.{modify.Desired.Name} becomes NOT NULL; it has no NULLs now, so this will work."
                        : $"{modify.Table}.{modify.Desired.Name} becomes NOT NULL but {nulls} row(s) contain NULL: the apply will fail until they're filled in.");
                    break;
                case AddColumn add when !created.Contains(add.Table)
                    && !await introspector.HasRowsAsync(database, Physical(add.Table), cancellationToken):
                    break; // Empty table: nothing to fill in.
                default:
                    warnings.Add(operation.RiskReason ?? operation.Describe());
                    break;
            }
        }

        return warnings;
    }

    internal static async Task<Project> VisibleProjectAsync(IProjectRepository projects, long projectId, CancellationToken cancellationToken)
    {
        var project = await projects.FindAsync(projectId, cancellationToken);
        return project is null || project.Status == ProjectStatus.Deleting
            ? throw new NotFoundException("Project not found.")
            : project;
    }

    /// <summary>The project must have its database to plan or apply.</summary>
    internal static async Task<Project> ReadyProjectAsync(IProjectRepository projects, long projectId, CancellationToken cancellationToken)
    {
        var project = await VisibleProjectAsync(projects, projectId, cancellationToken);
        return project.Status == ProjectStatus.Active
            ? project
            : throw new ConflictException("The project's database isn't ready yet.");
    }
}

/// <summary>A diff, its SQL and its hash: what the plan shows and what the apply re-checks.</summary>
internal sealed record SchemaPlan(SchemaDiff Diff, IReadOnlyList<string> Statements, string Hash)
{
    public static SchemaPlan Build(SchemaModel desired, SchemaModel actual, Project project, ISqlRenderer renderer)
    {
        var diff = SchemaDiffer.Diff(desired, actual);
        var statements = renderer.Render(project.DatabaseName, diff.Operations);
        return new SchemaPlan(diff, statements, PlanHash.Compute(project.SchemaVersion, statements));
    }
}
