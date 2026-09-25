namespace CoreFoundry.Application.SchemaEngine;

/// <summary>The draft or the database changed since the plan was made (the hash differs). Maps to 409 plan-stale.</summary>
public sealed class PlanStaleException : Exception
{
    public PlanStaleException() : base("The draft or the database changed since you reviewed the plan. Review it again.") { }

    public PlanStaleException(string message) : base(message) { }

    public PlanStaleException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>Another apply holds the project's lock. Maps to 409 apply-in-progress.</summary>
public sealed class ApplyInProgressException : Exception
{
    public ApplyInProgressException() : base("Someone else is applying changes to this project. Try again in a moment.") { }

    public ApplyInProgressException(string message) : base(message) { }

    public ApplyInProgressException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>The plan drops or narrows data and the caller didn't confirm it. Maps to 422.</summary>
public sealed class DestructiveNotAcknowledgedException : Exception
{
    public DestructiveNotAcknowledgedException()
        : base("This plan deletes or may truncate data. Confirm that you understand before applying.") { }

    public DestructiveNotAcknowledgedException(string message) : base(message) { }

    public DestructiveNotAcknowledgedException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// A statement failed during apply. Statements before it are applied (MySQL commits DDL at once);
/// the journal says how far it got, and planning again proposes only what's left. Maps to 500 apply-failed.
/// </summary>
public sealed class ApplyFailedException : Exception
{
    public ApplyFailedException(long migrationId, int failedStatement, string statement, string error)
        : base($"Statement {failedStatement} failed: {error}")
    {
        MigrationId = migrationId;
        FailedStatement = failedStatement;
        Statement = statement;
        Error = error;
    }

    public ApplyFailedException() : this(0, 0, string.Empty, string.Empty) { }

    public ApplyFailedException(string message) : base(message) => (Statement, Error) = (string.Empty, message);

    public ApplyFailedException(string message, Exception innerException)
        : base(message, innerException) => (Statement, Error) = (string.Empty, message);

    public long MigrationId { get; }

    /// <summary>1-based position of the statement that failed.</summary>
    public int FailedStatement { get; }

    public string Statement { get; }

    public string Error { get; }
}
