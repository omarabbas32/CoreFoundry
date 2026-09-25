using System.Text.Json;
using CoreFoundry.Application.Common;
using CoreFoundry.Application.Projects;
using CoreFoundry.Application.Schema;
using CoreFoundry.Domain.Schema;
using Microsoft.Extensions.Logging;

namespace CoreFoundry.Application.SchemaEngine;

public sealed record ApplyResultDto(long MigrationId, int Version, MigrationStatus Status, int Statements)
{
    /// <summary>Set when this apply also inserted a template's sample rows (see <see cref="Templates.SampleDataService"/>).</summary>
    public Templates.SampleDataResultDto? SampleData { get; init; }
}

/// <summary>
/// Applies a reviewed plan to the project database.
/// </summary>
/// <remarks>
/// MySQL commits every DDL statement on its own, so there's no transaction to roll back. Instead:
/// the project's lock (held by one dedicated session) keeps applies from overlapping; the plan is
/// rebuilt under the lock and must hash to what the user reviewed; a journal row records how many
/// statements ran; and the draft metadata changes only after every statement succeeded. After a
/// failure, planning again compares with the real database and proposes exactly what's left.
/// </remarks>
public sealed partial class SchemaApplier(
    IProjectRepository projects,
    ITableRepository tables,
    ISchemaMigrationRepository migrations,
    ISchemaEngine engine,
    ISqlRenderer renderer,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    ILogger<SchemaApplier> logger)
{
    /// <exception cref="ApplyInProgressException">Another apply holds the lock.</exception>
    /// <exception cref="PlanStaleException">The plan changed since it was reviewed.</exception>
    /// <exception cref="DestructiveNotAcknowledgedException">Data would be lost and the caller didn't confirm.</exception>
    /// <exception cref="ApplyFailedException">A statement failed; the journal row is Failed.</exception>
    public async Task<ApplyResultDto> ApplyAsync(
        long projectId, long userId, string planHash, bool acknowledgeDestructive, CancellationToken cancellationToken)
    {
        var project = await SchemaPlanService.ReadyProjectAsync(projects, projectId, cancellationToken);

        await using var session = await engine.TryLockAsync(projectId, cancellationToken) ?? throw new ApplyInProgressException();

        var draft = await tables.ListAsync(projectId, cancellationToken);
        var actual = await session.ReadSchemaAsync(project.DatabaseName, cancellationToken);
        var plan = SchemaPlan.Build(DraftSchema.From(draft), actual, project, renderer);

        if (!string.Equals(plan.Hash, planHash, StringComparison.Ordinal))
        {
            throw new PlanStaleException();
        }

        if (plan.Diff.IsEmpty)
        {
            throw new ConflictException("There is nothing to apply: the database already matches the draft.");
        }

        if (plan.Diff.HasDestructive && !acknowledgeDestructive)
        {
            throw new DestructiveNotAcknowledgedException();
        }

        var migration = new SchemaMigration(
            projectId, project.SchemaVersion + 1, JsonSerializer.Serialize(plan.Statements), plan.Statements.Count, userId);
        migrations.Add(migration);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        for (var index = 0; index < plan.Statements.Count; index++)
        {
            try
            {
                await session.ExecuteAsync(plan.Statements[index], cancellationToken);
            }
            catch (SchemaStatementFailedException ex)
            {
                migration.MarkFailed(ex.Message, timeProvider.GetUtcNow().UtcDateTime);
                await unitOfWork.SaveChangesAsync(CancellationToken.None);
                LogStatementFailed(logger, projectId, migration.Version, index + 1, ex.Message);
                throw new ApplyFailedException(migration.Id, index + 1, plan.Statements[index], ex.Message);
            }

            migration.RecordProgress(index + 1);
            await unitOfWork.SaveChangesAsync(CancellationToken.None);
        }

        // Every statement ran: the draft becomes the applied state, in one save.
        var after = await session.ReadSchemaAsync(project.DatabaseName, CancellationToken.None);
        foreach (var table in draft)
        {
            if (table.PendingDrop)
            {
                tables.Remove(table);
            }
            else
            {
                table.MarkApplied();
            }
        }

        project.BumpSchemaVersion();
        migration.MarkApplied(SchemaSnapshot.From(after).ToJson(), timeProvider.GetUtcNow().UtcDateTime);
        await unitOfWork.SaveChangesAsync(CancellationToken.None);

        LogApplied(logger, projectId, migration.Version, plan.Statements.Count);
        return new ApplyResultDto(migration.Id, migration.Version, migration.Status, plan.Statements.Count);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Project {ProjectId}: schema version {Version} applied ({Statements} statements)")]
    private static partial void LogApplied(ILogger logger, long projectId, int version, int statements);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Project {ProjectId}: schema version {Version} failed at statement {Statement}: {Error}")]
    private static partial void LogStatementFailed(ILogger logger, long projectId, int version, int statement, string error);
}
