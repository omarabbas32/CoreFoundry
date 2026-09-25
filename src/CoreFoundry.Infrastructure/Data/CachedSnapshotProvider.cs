using CoreFoundry.Application.Data;
using CoreFoundry.Application.Schema;
using CoreFoundry.Application.SchemaEngine;
using Microsoft.Extensions.Caching.Memory;

namespace CoreFoundry.Infrastructure.Data;

/// <summary>
/// Reads the last applied snapshot once per project and schema version. The key contains the
/// version, and every apply bumps it, so a new apply is picked up at once without evicting
/// anything; entries for old versions simply expire.
/// </summary>
/// <remarks>
/// Which tables and columns CoreFoundry manages comes from the draft's applied names. Those only
/// change when an apply succeeds, which also bumps the version, so they share the cache entry.
/// </remarks>
public sealed class CachedSnapshotProvider(ISchemaMigrationRepository migrations, ITableRepository tables, IMemoryCache cache)
    : ISnapshotProvider
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    public static string CacheKey(long projectId, int schemaVersion) => $"snapshot:{projectId}:{schemaVersion}";

    public async Task<DataSchema> GetAsync(long projectId, int schemaVersion, CancellationToken cancellationToken)
    {
        var key = CacheKey(projectId, schemaVersion);
        if (cache.TryGetValue(key, out DataSchema? cached) && cached is not null)
        {
            return cached;
        }

        var schema = DataSchema.Empty(schemaVersion);
        if (await migrations.LatestAppliedAsync(projectId, cancellationToken) is { } latest)
        {
            var managed = (await tables.ListAsync(projectId, cancellationToken))
                .Where(table => table.AppliedName is not null)
                .ToDictionary(
                    table => table.AppliedName!,
                    table => (IReadOnlySet<string>)table.Columns
                        .Where(column => column.AppliedName is not null)
                        .Select(column => column.AppliedName!)
                        .ToHashSet(StringComparer.Ordinal),
                    StringComparer.Ordinal);
            schema = DataSchema.From(SchemaSnapshot.FromJson(latest.SnapshotJson), schemaVersion, managed);
        }

        // An apply that finished between reading the project and here would be read under the old
        // version's key. Harmless: requests that see the new version use the new key.
        cache.Set(key, schema, Lifetime);
        return schema;
    }
}
