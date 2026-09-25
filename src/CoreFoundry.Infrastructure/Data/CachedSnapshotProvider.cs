using CoreFoundry.Application.Data;
using CoreFoundry.Application.SchemaEngine;
using Microsoft.Extensions.Caching.Memory;

namespace CoreFoundry.Infrastructure.Data;

/// <summary>
/// Reads the last applied snapshot once per project and schema version. The key contains the
/// version, and every apply bumps it, so a new apply is picked up at once without evicting
/// anything; entries for old versions simply expire.
/// </summary>
public sealed class CachedSnapshotProvider(ISchemaMigrationRepository migrations, IMemoryCache cache) : ISnapshotProvider
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

        var latest = await migrations.LatestAppliedAsync(projectId, cancellationToken);
        var schema = latest is null
            ? DataSchema.Empty(schemaVersion)
            : DataSchema.From(SchemaSnapshot.FromJson(latest.SnapshotJson), schemaVersion);

        // An apply that finished between reading the project and here would be read under the old
        // version's key. Harmless: requests that see the new version use the new key.
        cache.Set(key, schema, Lifetime);
        return schema;
    }
}
