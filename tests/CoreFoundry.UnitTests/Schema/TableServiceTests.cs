using System.Reflection;
using CoreFoundry.Application.Common;
using CoreFoundry.Application.Schema;
using CoreFoundry.Domain.Common;
using CoreFoundry.Domain.Projects;
using CoreFoundry.Domain.Schema;
using CoreFoundry.UnitTests.Projects;
using Shouldly;

namespace CoreFoundry.UnitTests.Schema;

public class TableServiceTests
{
    private const long ProjectId = 7;
    private readonly FakeProjects _projects = new();
    private readonly FakeTables _tables = new();
    private readonly FakeTablesUnitOfWork _unitOfWork;
    private readonly TableService _service;

    public TableServiceTests()
    {
        _projects.Add(WithId(new Project("Bookshop", "bookshop", ownerId: 1), ProjectId));
        _unitOfWork = new FakeTablesUnitOfWork(_tables);
        _service = new TableService(_projects, _tables, _unitOfWork);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Create_saves_a_new_table_with_its_columns()
    {
        var table = await _service.CreateAsync(ProjectId, "Books", [Varchar("Title", 200), Int("pages")], Ct);

        table.Name.ShouldBe("books");
        table.State.ShouldBe(SchemaObjectState.New);
        table.Version.ShouldBe(3);
        table.Columns.Select(column => (column.Name, column.OrdinalPosition, column.State))
            .ShouldBe([("title", 0, SchemaObjectState.New), ("pages", 1, SchemaObjectState.New)]);
        _tables.All.ShouldHaveSingleItem().Id.ShouldBe(table.Id);
    }

    [Fact]
    public async Task Create_reports_every_invalid_column_keyed_by_index_and_field()
    {
        var ex = await Should.ThrowAsync<ValidationFailedException>(() => _service.CreateAsync(
            ProjectId,
            "books",
            [Varchar("title", 200), Varchar("select", 10), Int("pages") with { Length = 5 }, Varchar("price", 0)],
            Ct));

        ex.Errors.Keys.ShouldBe(["columns[1].name", "columns[2].length", "columns[3].length"], ignoreOrder: true);
        _tables.All.ShouldBeEmpty();
    }

    [Fact]
    public async Task Create_rejects_an_invalid_table_name_on_the_name_field()
    {
        var ex = await Should.ThrowAsync<ValidationFailedException>(() => _service.CreateAsync(ProjectId, "a;drop", null, Ct));

        ex.Errors.Keys.ShouldBe(["name"]);
    }

    [Fact]
    public async Task Table_names_are_unique_within_the_project()
    {
        await _service.CreateAsync(ProjectId, "books", null, Ct);

        var ex = await Should.ThrowAsync<ValidationFailedException>(() => _service.CreateAsync(ProjectId, "BOOKS", null, Ct));
        ex.Errors["name"].ShouldHaveSingleItem().ShouldContain("already has a table");
    }

    [Fact]
    public async Task A_project_holds_at_most_50_tables()
    {
        for (var i = 0; i < SchemaLimits.MaxTablesPerProject; i++)
        {
            await _service.CreateAsync(ProjectId, $"t{i}", null, Ct);
        }

        await Should.ThrowAsync<DomainException>(() => _service.CreateAsync(ProjectId, "one_more", null, Ct));
    }

    [Fact]
    public async Task A_project_being_deleted_is_not_found()
    {
        _projects.All.Single().MarkDeleting();

        await Should.ThrowAsync<NotFoundException>(() => _service.ListAsync(ProjectId, Ct));
    }

    [Fact]
    public async Task A_table_of_another_project_is_not_found()
    {
        _projects.Add(WithId(new Project("Other", "other", ownerId: 1), 8));
        var other = await _service.CreateAsync(8, "books", null, Ct);

        await Should.ThrowAsync<NotFoundException>(() => _service.GetAsync(ProjectId, other.Id, Ct));
        await Should.ThrowAsync<NotFoundException>(() => _service.RenameAsync(ProjectId, other.Id, other.Version, "x", Ct));
    }

    [Fact]
    public async Task A_stale_version_is_a_conflict()
    {
        var table = await _service.CreateAsync(ProjectId, "books", null, Ct);
        await _service.AddColumnAsync(ProjectId, table.Id, table.Version, Int("pages"), Ct);

        await Should.ThrowAsync<ConflictException>(
            () => _service.AddColumnAsync(ProjectId, table.Id, table.Version, Int("year"), Ct));
    }

    [Fact]
    public async Task Losing_a_save_race_is_a_conflict()
    {
        var table = await _service.CreateAsync(ProjectId, "books", null, Ct);
        _unitOfWork.FailNextWithConcurrencyConflict = true;

        await Should.ThrowAsync<ConflictException>(() => _service.RenameAsync(ProjectId, table.Id, table.Version, "novels", Ct));
    }

    [Fact]
    public async Task Rename_can_keep_the_same_name_and_rejects_another_tables_name()
    {
        var books = await _service.CreateAsync(ProjectId, "books", null, Ct);
        await _service.CreateAsync(ProjectId, "authors", null, Ct);

        (await _service.RenameAsync(ProjectId, books.Id, books.Version, "Books", Ct)).Version.ShouldBe(books.Version);
        await Should.ThrowAsync<ValidationFailedException>(() => _service.RenameAsync(ProjectId, books.Id, books.Version, "authors", Ct));
    }

    [Fact]
    public async Task Deleting_a_never_applied_table_removes_it()
    {
        var table = await _service.CreateAsync(ProjectId, "books", [Int("pages")], Ct);

        (await _service.DeleteAsync(ProjectId, table.Id, table.Version, Ct)).ShouldBeNull();

        _tables.All.ShouldBeEmpty();
    }

    [Fact]
    public async Task Deleting_an_applied_table_marks_it_and_its_columns_pending_drop_until_restored()
    {
        var created = await _service.CreateAsync(ProjectId, "books", [Int("pages")], Ct);
        MarkApplied(_tables.All.Single());

        var deleted = (await _service.DeleteAsync(ProjectId, created.Id, created.Version, Ct))!;
        deleted.State.ShouldBe(SchemaObjectState.PendingDrop);
        deleted.Columns.ShouldAllBe(column => column.State == SchemaObjectState.PendingDrop);

        var restored = await _service.RestoreAsync(ProjectId, created.Id, deleted.Version, Ct);
        restored.State.ShouldBe(SchemaObjectState.Applied);
        restored.Columns.ShouldAllBe(column => column.State == SchemaObjectState.Applied);
    }

    [Fact]
    public async Task Deleting_an_applied_column_is_reversible()
    {
        var table = await _service.CreateAsync(ProjectId, "books", [Int("pages")], Ct);
        MarkApplied(_tables.All.Single());
        var pages = table.Columns.Single();

        var deleted = await _service.DeleteColumnAsync(ProjectId, table.Id, pages.Id, table.Version, Ct);
        deleted.Columns.Single().State.ShouldBe(SchemaObjectState.PendingDrop);

        var restored = await _service.RestoreColumnAsync(ProjectId, table.Id, pages.Id, deleted.Version, Ct);
        restored.Columns.Single().State.ShouldBe(SchemaObjectState.Applied);
    }

    [Fact]
    public async Task Column_errors_are_keyed_by_field()
    {
        var table = await _service.CreateAsync(ProjectId, "books", null, Ct);

        var ex = await Should.ThrowAsync<ValidationFailedException>(() => _service.AddColumnAsync(
            ProjectId, table.Id, table.Version, Int("pages") with { DefaultValue = "many" }, Ct));

        ex.Errors.Keys.ShouldBe(["defaultValue"]);
    }

    [Fact]
    public async Task A_column_of_another_table_is_not_found()
    {
        var books = await _service.CreateAsync(ProjectId, "books", [Int("pages")], Ct);
        var authors = await _service.CreateAsync(ProjectId, "authors", null, Ct);

        await Should.ThrowAsync<NotFoundException>(() => _service.DeleteColumnAsync(
            ProjectId, authors.Id, books.Columns.Single().Id, authors.Version, Ct));
    }

    [Fact]
    public async Task Update_and_reorder_columns()
    {
        var table = await _service.CreateAsync(ProjectId, "books", [Varchar("title", 100), Int("pages")], Ct);
        var (title, pages) = (table.Columns[0], table.Columns[1]);

        table = await _service.UpdateColumnAsync(
            ProjectId, table.Id, title.Id, table.Version, Varchar("headline", 150) with { IsNullable = false, DefaultValue = "Untitled" }, Ct);
        table = await _service.ReorderColumnsAsync(ProjectId, table.Id, table.Version, [pages.Id, title.Id], Ct);

        table.Columns.Select(column => column.Name).ShouldBe(["pages", "headline"]);
        var headline = table.Columns[1];
        (headline.Length, headline.IsNullable, headline.DefaultValue).ShouldBe((150, false, "Untitled"));
    }

    // ---- References ----------------------------------------------------------------------------------

    [Fact]
    public async Task A_column_references_another_table_and_shows_its_name()
    {
        var authors = await _service.CreateAsync(ProjectId, "authors", null, Ct);
        var books = await _service.CreateAsync(ProjectId, "books", null, Ct);

        books = await _service.AddColumnAsync(ProjectId, books.Id, books.Version, Ref("author_id", authors.Id, ReferenceAction.Cascade), Ct);

        var authorId = books.Columns.Single();
        (authorId.ReferencesTableId, authorId.ReferencesTableName, authorId.OnDelete).ShouldBe((authors.Id, "authors", ReferenceAction.Cascade));
    }

    [Fact]
    public async Task A_table_can_reference_itself()
    {
        var employees = await _service.CreateAsync(ProjectId, "employees", null, Ct);

        employees = await _service.AddColumnAsync(
            ProjectId, employees.Id, employees.Version, Ref("manager_id", employees.Id, ReferenceAction.SetNull), Ct);

        employees.Columns.Single().ReferencesTableName.ShouldBe("employees");
    }

    [Fact]
    public async Task A_table_of_another_project_cannot_be_referenced()
    {
        _projects.Add(WithId(new Project("Other", "other", ownerId: 1), 8));
        var foreign = await _service.CreateAsync(8, "secrets", null, Ct);
        var books = await _service.CreateAsync(ProjectId, "books", null, Ct);

        var ex = await Should.ThrowAsync<ValidationFailedException>(() => _service.AddColumnAsync(
            ProjectId, books.Id, books.Version, Ref("secret_id", foreign.Id, ReferenceAction.Restrict), Ct));

        ex.Errors.Keys.ShouldBe(["referencesTableId"]);
    }

    [Fact]
    public async Task Create_with_columns_checks_references_by_index()
    {
        var ex = await Should.ThrowAsync<ValidationFailedException>(() => _service.CreateAsync(
            ProjectId, "books", [Int("pages"), Ref("author_id", 999, ReferenceAction.Restrict)], Ct));

        ex.Errors.Keys.ShouldBe(["columns[1].referencesTableId"]);
    }

    [Fact]
    public async Task A_referenced_table_cannot_be_deleted_until_the_reference_is_gone()
    {
        var authors = await _service.CreateAsync(ProjectId, "authors", null, Ct);
        var books = await _service.CreateAsync(ProjectId, "books", [Ref("author_id", authors.Id, ReferenceAction.Restrict)], Ct);

        (await Should.ThrowAsync<DomainException>(() => _service.DeleteAsync(ProjectId, authors.Id, authors.Version, Ct)))
            .Message.ShouldContain("books.author_id");

        await _service.DeleteColumnAsync(ProjectId, books.Id, books.Columns.Single().Id, books.Version, Ct);
        (await _service.DeleteAsync(ProjectId, authors.Id, authors.Version, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task A_reference_to_a_table_pending_drop_cannot_be_added_or_restored()
    {
        var authors = await _service.CreateAsync(ProjectId, "authors", null, Ct);
        var books = await _service.CreateAsync(ProjectId, "books", [Ref("author_id", authors.Id, ReferenceAction.Restrict)], Ct);
        foreach (var table in _tables.All)
        {
            MarkApplied(table);
        }

        // Drop the reference (pending), then its target (allowed now), then try to bring the reference back.
        books = await _service.DeleteColumnAsync(ProjectId, books.Id, books.Columns.Single().Id, books.Version, Ct);
        await _service.DeleteAsync(ProjectId, authors.Id, authors.Version, Ct);

        (await Should.ThrowAsync<DomainException>(() => _service.RestoreColumnAsync(
            ProjectId, books.Id, books.Columns.Single().Id, books.Version, Ct))).Message.ShouldContain("marked for deletion");
        (await Should.ThrowAsync<ValidationFailedException>(() => _service.AddColumnAsync(
            ProjectId, books.Id, books.Version, Ref("writer_id", authors.Id, ReferenceAction.Restrict), Ct))).Errors.Keys.ShouldBe(["referencesTableId"]);
    }

    [Fact]
    public async Task Schema_returns_every_table_with_its_columns()
    {
        var authors = await _service.CreateAsync(ProjectId, "authors", [Varchar("name", 100)], Ct);
        await _service.CreateAsync(ProjectId, "books", [Ref("author_id", authors.Id, ReferenceAction.Cascade)], Ct);

        var schema = await _service.GetSchemaAsync(ProjectId, Ct);

        schema.Select(table => table.Name).ShouldBe(["authors", "books"]);
        schema[1].Columns.Single().ReferencesTableName.ShouldBe("authors");
    }

    private static ColumnInput Ref(string name, long tableId, ReferenceAction onDelete) =>
        new(name, DataType.BigInt, null, null, null, true, false, null, tableId, onDelete);

    private static ColumnInput Int(string name) => new(name, DataType.Int, null, null, null, true, false, null);

    private static ColumnInput Varchar(string name, int length) => new(name, DataType.Varchar, length, null, null, true, false, null);

    private static void MarkApplied(ProjectTable table)
    {
        SetProperty(table, nameof(ProjectTable.AppliedName), table.Name);
        foreach (var column in table.Columns)
        {
            SetProperty(column, nameof(ProjectColumn.AppliedName), column.Name);
        }
    }

    private static T WithId<T>(T entity, long id) where T : class => SetProperty(entity, "Id", id);

    private static T SetProperty<T>(T entity, string name, object value) where T : class
    {
        entity.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public)!.SetValue(entity, value);
        return entity;
    }
}

internal sealed class FakeTables : ITableRepository
{
    public List<ProjectTable> All { get; } = [];

    public Task<IReadOnlyList<ProjectTable>> ListAsync(long projectId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ProjectTable>>([.. All.Where(table => table.ProjectId == projectId).OrderBy(table => table.Name)]);

    public Task<ProjectTable?> FindAsync(long projectId, long tableId, CancellationToken cancellationToken) =>
        Task.FromResult(All.SingleOrDefault(table => table.ProjectId == projectId && table.Id == tableId));

    public Task<int> CountAsync(long projectId, CancellationToken cancellationToken) =>
        Task.FromResult(All.Count(table => table.ProjectId == projectId));

    public Task<IReadOnlyDictionary<long, string>> ListNamesAsync(long projectId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<long, string>>(
            All.Where(table => table.ProjectId == projectId).ToDictionary(table => table.Id, table => table.Name));

    public Task<bool> NameExistsAsync(long projectId, string name, long? exceptTableId, CancellationToken cancellationToken) =>
        Task.FromResult(All.Any(table => table.ProjectId == projectId && table.Name == name && table.Id != exceptTableId));

    public void Add(ProjectTable table) => All.Add(table);

    public void Remove(ProjectTable table) => All.Remove(table);
}

/// <summary>Assigns ids like the database would, and can simulate losing an optimistic-concurrency race.</summary>
internal sealed class FakeTablesUnitOfWork(FakeTables tables) : IUnitOfWork
{
    private long _nextId = 1;

    public bool FailNextWithConcurrencyConflict { get; set; }

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        if (FailNextWithConcurrencyConflict)
        {
            FailNextWithConcurrencyConflict = false;
            throw new ConcurrencyConflictException("Row changed.");
        }

        foreach (var table in tables.All)
        {
            AssignId(table);
            foreach (var column in table.Columns)
            {
                AssignId(column);
            }
        }

        return Task.CompletedTask;
    }

    private void AssignId(object entity)
    {
        var id = entity.GetType().GetProperty("Id")!;
        if ((long)id.GetValue(entity)! == 0)
        {
            id.SetValue(entity, _nextId++);
        }
    }
}
