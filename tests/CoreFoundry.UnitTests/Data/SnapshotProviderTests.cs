using CoreFoundry.Application.Data;
using CoreFoundry.Application.SchemaEngine;
using CoreFoundry.Domain.Schema;
using CoreFoundry.Domain.SchemaEngine;
using CoreFoundry.Infrastructure.Data;
using CoreFoundry.UnitTests.Schema;
using Microsoft.Extensions.Caching.Memory;
using Shouldly;

namespace CoreFoundry.UnitTests.Data;

public sealed class SnapshotProviderTests : IDisposable
{
    private const long ProjectId = 7;
    private readonly FakeMigrations _migrations = new();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private CachedSnapshotProvider Provider => new(_migrations, _cache);

    public void Dispose() => _cache.Dispose();

    [Fact]
    public async Task No_applied_migration_means_no_tables()
    {
        _migrations.Add(new SchemaMigration(ProjectId, 1, "[]", 1, requestedBy: null)); // still Pending

        var schema = await Provider.GetAsync(ProjectId, 0, Ct);

        schema.Tables.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_last_applied_snapshot_becomes_typed_tables()
    {
        Applied(1, new TableSnapshot("books",
        [
            new ColumnSnapshot("title", "Varchar(200)", false, true, null, null, null),
            new ColumnSnapshot("price_usd", "Decimal(10,2)", false, false, "0.00", null, null),
            new ColumnSnapshot("author_id", "BigInt", true, false, null, "authors", "Cascade"),
            new ColumnSnapshot("legacy", "mediumint(9)", true, false, null, null, null),
        ]));

        var books = (await Provider.GetAsync(ProjectId, 1, Ct)).FindTable("books").ShouldNotBeNull();

        books.Columns.Select(column => column.Name).ShouldBe(["title", "price_usd", "author_id", "legacy"]);
        books.FindColumn("title")!.Type.ShouldBe(new ColumnType(DataType.Varchar, 200));
        books.FindColumn("price_usd")!.Type.ShouldBe(new ColumnType(DataType.Decimal, null, 10, 2));
        books.FindColumn("author_id")!.References.ShouldBe("authors");
        var legacy = books.FindColumn("legacy")!;
        (legacy.Type, legacy.RawType, legacy.IsWritable).ShouldBe((null, "mediumint(9)", false));
    }

    [Fact]
    public async Task Table_names_match_exactly()
    {
        Applied(1, new TableSnapshot("books", []));

        var schema = await Provider.GetAsync(ProjectId, 1, Ct);

        schema.FindTable("Books").ShouldBeNull();
        schema.FindTable("books`; DROP TABLE books; --").ShouldBeNull();
    }

    [Fact]
    public async Task The_same_version_is_served_from_the_cache()
    {
        Applied(1, new TableSnapshot("books", []));
        var first = await Provider.GetAsync(ProjectId, 1, Ct);

        _migrations.All.Clear();
        var second = await Provider.GetAsync(ProjectId, 1, Ct);

        second.ShouldBeSameAs(first);
    }

    [Fact]
    public async Task A_new_version_reads_the_new_snapshot()
    {
        Applied(1, new TableSnapshot("books", [new ColumnSnapshot("price", "Decimal(10,2)", false, false, null, null, null)]));
        (await Provider.GetAsync(ProjectId, 1, Ct)).FindTable("books")!.FindColumn("price").ShouldNotBeNull();

        Applied(2, new TableSnapshot("books", [new ColumnSnapshot("price_usd", "Decimal(10,2)", false, false, null, null, null)]));
        var books = (await Provider.GetAsync(ProjectId, 2, Ct)).FindTable("books")!;

        books.FindColumn("price").ShouldBeNull();
        books.FindColumn("price_usd").ShouldNotBeNull();
    }

    private void Applied(int version, params TableSnapshot[] tables)
    {
        var migration = new SchemaMigration(ProjectId, version, "[\"-- sql\"]", 1, requestedBy: null);
        migration.RecordProgress(1);
        migration.MarkApplied(new SchemaSnapshot(tables).ToJson(), DateTime.UtcNow);
        _migrations.Add(migration);
    }
}
