using CoreFoundry.Domain.Common;

namespace CoreFoundry.Domain.Schema;

/// <summary>
/// Journal row for one schema apply. MySQL commits every DDL statement immediately, so this row
/// (not a transaction) records how far an apply got.
/// </summary>
public sealed class SchemaMigration
{
    private SchemaMigration() { } // EF Core

    /// <summary>Starts a <see cref="MigrationStatus.Pending"/> apply of <paramref name="statementCount"/> statements.</summary>
    public SchemaMigration(long projectId, int version, string statementsJson, int statementCount, long? requestedBy)
    {
        ProjectId = Guard.PositiveId(projectId, nameof(ProjectId));
        Version = version > 0 ? version : throw new DomainException("Version must be positive.");
        StatementsJson = string.IsNullOrWhiteSpace(statementsJson)
            ? throw new DomainException("StatementsJson is required.")
            : statementsJson;
        StatementCount = statementCount > 0 ? statementCount : throw new DomainException("A migration needs at least one statement.");
        RequestedBy = requestedBy;
        Status = MigrationStatus.Pending;
    }

    public long Id { get; private set; }
    public long ProjectId { get; private set; }
    public int Version { get; private set; }
    public MigrationStatus Status { get; private set; }
    public string StatementsJson { get; private set; } = null!;
    public int StatementCount { get; private set; }
    public int StatementsApplied { get; private set; }
    public string? SnapshotJson { get; private set; }
    public string? Error { get; private set; }
    public long? RequestedBy { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }

    /// <summary>Records that statements 1..<paramref name="appliedCount"/> have succeeded.</summary>
    public void RecordProgress(int appliedCount)
    {
        EnsurePending();
        if (appliedCount <= StatementsApplied || appliedCount > StatementCount)
        {
            throw new DomainException(
                $"Progress must move forward within 1..{StatementCount} (currently {StatementsApplied}).");
        }

        StatementsApplied = appliedCount;
    }

    public void MarkApplied(string snapshotJson, DateTime utcNow)
    {
        EnsurePending();
        if (StatementsApplied != StatementCount)
        {
            throw new DomainException($"Only {StatementsApplied} of {StatementCount} statements were applied.");
        }

        SnapshotJson = string.IsNullOrWhiteSpace(snapshotJson)
            ? throw new DomainException("SnapshotJson is required.")
            : snapshotJson;
        Status = MigrationStatus.Applied;
        CompletedAt = Guard.Utc(utcNow, nameof(utcNow));
    }

    public void MarkFailed(string error, DateTime utcNow)
    {
        EnsurePending();
        Error = string.IsNullOrWhiteSpace(error) ? throw new DomainException("Error is required.") : error;
        Status = MigrationStatus.Failed;
        CompletedAt = Guard.Utc(utcNow, nameof(utcNow));
    }

    private void EnsurePending()
    {
        if (Status != MigrationStatus.Pending)
        {
            throw new DomainException($"This migration is already {Status}.");
        }
    }
}
