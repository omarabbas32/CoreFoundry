using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CoreFoundry.Infrastructure.Persistence;

/// <summary>
/// Sets <c>CreatedAt</c> on insert and <c>UpdatedAt</c> on insert/update for any entity that has
/// those properties, so domain code never handles audit timestamps.
/// </summary>
internal sealed class TimestampsInterceptor(TimeProvider timeProvider) : SaveChangesInterceptor
{
    public const string CreatedAt = nameof(CreatedAt);
    public const string UpdatedAt = nameof(UpdatedAt);

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        Stamp(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Stamp(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void Stamp(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State == EntityState.Added && entry.Metadata.FindProperty(CreatedAt) is not null)
            {
                entry.Property(CreatedAt).CurrentValue = now;
            }

            if (entry.State is EntityState.Added or EntityState.Modified
                && entry.Metadata.FindProperty(UpdatedAt) is not null)
            {
                entry.Property(UpdatedAt).CurrentValue = now;
            }
        }
    }
}
