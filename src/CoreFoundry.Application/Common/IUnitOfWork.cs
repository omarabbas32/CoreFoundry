namespace CoreFoundry.Application.Common;

public interface IUnitOfWork
{
    /// <summary>Persists all tracked changes atomically.</summary>
    /// <exception cref="ConcurrencyConflictException">Another request changed the same row first.</exception>
    Task SaveChangesAsync(CancellationToken cancellationToken);
}
