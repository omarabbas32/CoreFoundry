namespace CoreFoundry.Application.Common;

/// <summary>Input failed validation. Maps to 400 with one message list per field.</summary>
public sealed class ValidationFailedException : Exception
{
    public ValidationFailedException(string field, string message)
        : this(new Dictionary<string, string[]> { [field] = [message] }) { }

    public ValidationFailedException(IReadOnlyDictionary<string, string[]> errors)
        : base("One or more fields are invalid.") => Errors = errors;

    public ValidationFailedException() : this(new Dictionary<string, string[]>()) { }

    public ValidationFailedException(string message, Exception innerException)
        : base(message, innerException) => Errors = new Dictionary<string, string[]>();

    public IReadOnlyDictionary<string, string[]> Errors { get; }
}

/// <summary>Credentials or token rejected. Maps to 401; the message never says which part was wrong.</summary>
public sealed class AuthenticationFailedException : Exception
{
    public AuthenticationFailedException() : base("Invalid credentials.") { }

    public AuthenticationFailedException(string message) : base(message) { }

    public AuthenticationFailedException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>The request conflicts with existing state (e.g. email taken). Maps to 409.</summary>
public sealed class ConflictException : Exception
{
    public ConflictException(string message) : base(message) { }

    public ConflictException() { }

    public ConflictException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>The caller is known but not allowed to do this. Maps to 403.</summary>
public sealed class ForbiddenException : Exception
{
    public ForbiddenException(string message) : base(message) { }

    public ForbiddenException() { }

    public ForbiddenException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>The resource doesn't exist or isn't visible to the caller. Maps to 404.</summary>
public sealed class NotFoundException : Exception
{
    public NotFoundException(string message) : base(message) { }

    public NotFoundException() { }

    public NotFoundException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Creating or dropping a project's physical database failed. The project is left in a state the
/// startup recovery (or a retry) can finish. Maps to 503.
/// </summary>
public sealed class DatabaseProvisioningException : Exception
{
    public DatabaseProvisioningException(string message) : base(message) { }

    public DatabaseProvisioningException() { }

    public DatabaseProvisioningException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// A save lost an optimistic-concurrency race (another request changed the same row first).
/// Thrown by <see cref="IUnitOfWork"/> implementations.
/// </summary>
public sealed class ConcurrencyConflictException : Exception
{
    public ConcurrencyConflictException() { }

    public ConcurrencyConflictException(string message) : base(message) { }

    public ConcurrencyConflictException(string message, Exception innerException) : base(message, innerException) { }
}
