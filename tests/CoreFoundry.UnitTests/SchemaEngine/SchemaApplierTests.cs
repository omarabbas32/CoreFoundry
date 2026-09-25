using System.Reflection;
using CoreFoundry.Application.Common;
using CoreFoundry.Application.Schema;
using CoreFoundry.Application.SchemaEngine;
using CoreFoundry.Domain.Projects;
using CoreFoundry.Domain.Schema;
using CoreFoundry.Domain.SchemaEngine;
using CoreFoundry.Infrastructure.Engine;
using CoreFoundry.UnitTests.Projects;
using CoreFoundry.UnitTests.Schema;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CoreFoundry.UnitTests.SchemaEngine;

public class SchemaApplierTests
{
    private const long ProjectId = 7;
    private readonly FakeProjects _projects = new();
    private readonly FakeTables _tables = new();
    private readonly FakeMigrations _migrations = new();
    private readonly FakeEngine _engine = new();
    private readonly MySqlSqlRenderer _renderer = new();
    private readonly Project _project;

    public SchemaApplierTests()
    {
        _project = Set(new Project("Bookshop", "bookshop", ownerId: 1), "Id", ProjectId);
        _project.MarkActive();
        _projects.Add(_project);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private SchemaApplier Applier => new(
        _projects, _tables, _migrations, _engine, _renderer, new UnitOfWork(_migrations, _tables),
        TimeProvider.System, NullLogger<SchemaApplier>.Instance);

    private SchemaPlanService Planner => new(_projects, _tables, _migrations, _engine, _renderer);

    [Fact]
    public async Task A_successful_apply_marks_the_draft_applied_and_journals_it()
    {
        var books = AddTable("books", "title");
        var plan = await Planner.PlanAsync(ProjectId, Ct);

        var result = await Applier.ApplyAsync(ProjectId, 1, plan.PlanHash, false, Ct);

        (result.Version, result.Status, result.Statements).ShouldBe((1, MigrationStatus.Applied, 1));
        _engine.Executed.ShouldHaveSingleItem().ShouldStartWith("CREATE TABLE `cf_p_7`.`books`");
        books.AppliedName.ShouldBe("books");
        books.Columns.ShouldAllBe(column => column.AppliedName == column.Name);
        _project.SchemaVersion.ShouldBe(1);
        var migration = _migrations.All.ShouldHaveSingleItem();
        (migration.StatementsApplied, migration.SnapshotJson).ShouldBe((1, SchemaSnapshot.From(_engine.Actual).ToJson()));
        _engine.LockHeld.ShouldBeFalse();
    }

    [Fact]
    public async Task Pending_drops_are_removed_from_the_draft_after_apply()
    {
        var books = AddTable("books", "title", "notes");
        var drafts = AddTable("drafts");
        Applied(books);
        Applied(drafts);
        _engine.Actual = new SchemaModel(
        [
            new TableModel("books", [Real("title"), Real("notes")]),
            new TableModel("drafts", []),
        ]);
        books.DeleteColumn(books.Columns.Single(column => column.Name == "notes").Id);
        drafts.MarkPendingDrop();

        var plan = await Planner.PlanAsync(ProjectId, Ct);
        plan.HasDestructive.ShouldBeTrue();
        await Applier.ApplyAsync(ProjectId, 1, plan.PlanHash, acknowledgeDestructive: true, Ct);

        _tables.All.ShouldBe([books]);
        books.Columns.Select(column => column.Name).ShouldBe(["title"]);
    }

    [Fact]
    public async Task A_held_lock_is_apply_in_progress()
    {
        AddTable("books", "title");
        var plan = await Planner.PlanAsync(ProjectId, Ct);
        _engine.LockHeld = true;

        await Should.ThrowAsync<ApplyInProgressException>(() => Applier.ApplyAsync(ProjectId, 1, plan.PlanHash, false, Ct));
        _migrations.All.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_changed_draft_makes_the_plan_stale()
    {
        var books = AddTable("books", "title");
        var plan = await Planner.PlanAsync(ProjectId, Ct);
        books.AddColumn("isbn", Int());

        await Should.ThrowAsync<PlanStaleException>(() => Applier.ApplyAsync(ProjectId, 1, plan.PlanHash, false, Ct));
        _engine.Executed.ShouldBeEmpty();
        _engine.LockHeld.ShouldBeFalse();
    }

    [Fact]
    public async Task Destructive_plans_need_an_acknowledgement()
    {
        var books = AddTable("books");
        Applied(books);
        _engine.Actual = new SchemaModel([new TableModel("books", [])]);
        books.MarkPendingDrop();
        var plan = await Planner.PlanAsync(ProjectId, Ct);

        await Should.ThrowAsync<DestructiveNotAcknowledgedException>(() => Applier.ApplyAsync(ProjectId, 1, plan.PlanHash, false, Ct));
        _engine.Executed.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_empty_plan_has_nothing_to_apply()
    {
        var plan = await Planner.PlanAsync(ProjectId, Ct);

        await Should.ThrowAsync<ConflictException>(() => Applier.ApplyAsync(ProjectId, 1, plan.PlanHash, false, Ct));
    }

    [Fact]
    public async Task A_failing_statement_marks_the_migration_failed_and_leaves_the_draft_alone()
    {
        var authors = AddTable("authors", "name");
        var books = AddTable("books", "title");
        var plan = await Planner.PlanAsync(ProjectId, Ct);
        _engine.FailAt = 2;

        var ex = await Should.ThrowAsync<ApplyFailedException>(() => Applier.ApplyAsync(ProjectId, 1, plan.PlanHash, false, Ct));

        (ex.FailedStatement, ex.Error).ShouldBe((2, "boom"));
        ex.Statement.ShouldStartWith("CREATE TABLE `cf_p_7`.`books`");
        var migration = _migrations.All.ShouldHaveSingleItem();
        (migration.Status, migration.StatementsApplied, migration.Error, ex.MigrationId).ShouldBe((MigrationStatus.Failed, 1, "boom", migration.Id));
        (authors.AppliedName, books.AppliedName, _project.SchemaVersion).ShouldBe((null, null, 0));
        _engine.LockHeld.ShouldBeFalse();
    }

    [Fact]
    public async Task A_project_without_its_database_cannot_be_planned()
    {
        var pending = Set(new Project("Other", "other", 1), "Id", 8L);
        _projects.Add(pending);

        await Should.ThrowAsync<ConflictException>(() => Planner.PlanAsync(8, Ct));
    }

    [Fact]
    public async Task Plan_warns_with_the_null_count_before_not_null()
    {
        var books = AddTable("books", "title");
        Applied(books);
        _engine.Actual = new SchemaModel([new TableModel("books", [Real("title")])]);
        var title = books.Columns.Single();
        books.UpdateColumn(title.Id, "title", ColumnDefinitionRules.Create(DataType.Int, null, null, null, false, false, null));
        _engine.Nulls = 3;

        var plan = await Planner.PlanAsync(ProjectId, Ct);

        plan.Warnings.ShouldHaveSingleItem().ShouldContain("3 row(s) contain NULL");
    }

    [Fact]
    public async Task The_plan_hash_is_stable_and_depends_on_the_schema_version()
    {
        AddTable("books", "title");

        var first = await Planner.PlanAsync(ProjectId, Ct);
        (await Planner.PlanAsync(ProjectId, Ct)).PlanHash.ShouldBe(first.PlanHash);
        _project.BumpSchemaVersion();
        (await Planner.PlanAsync(ProjectId, Ct)).PlanHash.ShouldNotBe(first.PlanHash);
    }

    [Fact]
    public async Task Changed_state_compares_the_draft_with_the_last_snapshot()
    {
        var books = AddTable("books", "title");
        _engine.AfterApply = new SchemaModel([new TableModel("books", [Real("title")])]);
        await Applier.ApplyAsync(ProjectId, 1, (await Planner.PlanAsync(ProjectId, Ct)).PlanHash, false, Ct);
        var tableService = new TableService(_projects, _tables, _migrations, new UnitOfWork(_migrations, _tables));

        (await tableService.GetAsync(ProjectId, books.Id, Ct)).State.ShouldBe(SchemaObjectState.Applied);

        var title = books.Columns.Single();
        books.UpdateColumn(title.Id, "headline", title.Definition);
        var dto = await tableService.GetAsync(ProjectId, books.Id, Ct);
        dto.State.ShouldBe(SchemaObjectState.Changed);
        dto.Columns.Single().State.ShouldBe(SchemaObjectState.Changed);
    }

    private ProjectTable AddTable(string name, params string[] columns)
    {
        var table = Set(new ProjectTable(ProjectId, name), "Id", _tables.All.Count + 100L);
        foreach (var column in columns)
        {
            Set(table.AddColumn(column, Int()), "Id", table.Id * 100 + table.Columns.Count);
        }

        _tables.Add(table);
        return table;
    }

    /// <summary>Stands in for an earlier apply.</summary>
    private static void Applied(ProjectTable table)
    {
        Set(table, nameof(ProjectTable.AppliedName), table.Name);
        foreach (var column in table.Columns)
        {
            Set(column, nameof(ProjectColumn.AppliedName), column.Name);
        }
    }

    private static ColumnModel Real(string name) => new(name, new ColumnType(DataType.Int), true, false, null);

    private static ColumnDefinition Int() => ColumnDefinitionRules.Create(DataType.Int, null, null, null, true, false, null);

    private static T Set<T>(T entity, string property, object value) where T : class
    {
        entity.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public)!.SetValue(entity, value);
        return entity;
    }

    /// <summary>A database whose schema the test sets; statements are recorded, and one can be made to fail.</summary>
    private sealed class FakeEngine : ISchemaEngine, ISchemaIntrospector
    {
        public SchemaModel Actual { get; set; } = SchemaModel.Empty;

        /// <summary>What the database looks like once statements have run (unchanged if null).</summary>
        public SchemaModel? AfterApply { get; set; }

        public List<string> Executed { get; } = [];

        public bool LockHeld { get; set; }

        public int? FailAt { get; set; }

        public long Nulls { get; set; }

        public Task<IApplySession?> TryLockAsync(long projectId, CancellationToken cancellationToken)
        {
            if (LockHeld)
            {
                return Task.FromResult<IApplySession?>(null);
            }

            LockHeld = true;
            return Task.FromResult<IApplySession?>(new Session(this));
        }

        public Task<SchemaModel> ReadAsync(string databaseName, CancellationToken cancellationToken) => Task.FromResult(Actual);

        public Task<long> CountNullsAsync(string databaseName, string table, string column, CancellationToken cancellationToken) =>
            Task.FromResult(Nulls);

        public Task<bool> HasRowsAsync(string databaseName, string table, CancellationToken cancellationToken) => Task.FromResult(true);

        private sealed class Session(FakeEngine engine) : IApplySession
        {
            public Task<SchemaModel> ReadSchemaAsync(string databaseName, CancellationToken cancellationToken) =>
                Task.FromResult(engine.Actual);

            public Task ExecuteAsync(string sql, CancellationToken cancellationToken)
            {
                if (engine.Executed.Count + 1 == engine.FailAt)
                {
                    throw new SchemaStatementFailedException("boom");
                }

                engine.Executed.Add(sql);
                engine.Actual = engine.AfterApply ?? engine.Actual;
                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync()
            {
                engine.LockHeld = false;
                return ValueTask.CompletedTask;
            }
        }
    }

    /// <summary>Assigns ids like the database would.</summary>
    private sealed class UnitOfWork(FakeMigrations migrations, FakeTables tables) : IUnitOfWork
    {
        private long _nextId = 1000;

        public Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            foreach (var entity in migrations.All.Cast<object>().Concat(tables.All).Concat(tables.All.SelectMany(table => table.Columns)))
            {
                var id = entity.GetType().GetProperty("Id")!;
                if ((long)id.GetValue(entity)! == 0)
                {
                    id.SetValue(entity, _nextId++);
                }
            }

            return Task.CompletedTask;
        }
    }
}
