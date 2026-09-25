using System.Net;
using CoreFoundry.Api.Projects;
using CoreFoundry.Application.Projects;
using CoreFoundry.Application.Schema;
using CoreFoundry.Application.SchemaEngine;
using CoreFoundry.Domain.Projects;
using CoreFoundry.Domain.Schema;
using CoreFoundry.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Shouldly;
using static CoreFoundry.IntegrationTests.Infrastructure.ApiDriver;

namespace CoreFoundry.IntegrationTests.Projects;

[Collection(TestDatabaseGroup.Name)]
public sealed class TableEndpointsTests : IDisposable
{
    private readonly TestDatabaseApi _api;
    private readonly HttpClient _client;
    private readonly ApiDriver _driver;

    public TableEndpointsTests(TestDatabaseApi api)
    {
        api.SkipIfUnavailable();
        _api = api;
        _client = api.CreateHttpsClient();
        _driver = new ApiDriver(_client);
    }

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task A_developer_designs_tables_without_touching_the_project_database()
    {
        var (owner, project) = await OwnedProjectAsync();
        var developer = await _driver.SignUpAsync();
        await _driver.AddMemberAsync(project.Id, owner, developer, ProjectRole.Developer);

        var authors = await CreateAsync(project, developer, "Authors", Column("name", DataType.Varchar, length: 120, nullable: false));
        var books = await CreateAsync(
            project,
            developer,
            "books",
            Column("title", DataType.Varchar, length: 200, nullable: false),
            Column("price", DataType.Decimal, precision: 10, scale: 2, defaultValue: "0.00"),
            Column("published_on", DataType.Date));
        books = await OkAsync<TableDto>(HttpMethod.Post, $"{Tables(project)}/{books.Id}/columns", developer,
            new SaveColumnRequest(books.Version, "created_at", DataType.DateTime, null, null, null, false, false, "CURRENT_TIMESTAMP"));
        books = await OkAsync<TableDto>(HttpMethod.Put, $"{Tables(project)}/{books.Id}/columns/order", developer,
            new ReorderColumnsRequest(books.Version, [.. books.Columns.Select(column => column.Id).Reverse()]));

        books.Columns.Select(column => column.Name).ShouldBe(["created_at", "published_on", "price", "title"]);
        books.Columns.ShouldAllBe(column => column.State == SchemaObjectState.New);
        authors.Name.ShouldBe("authors");

        var list = await OkAsync<List<TableSummaryDto>>(HttpMethod.Get, Tables(project), developer);
        list.Select(table => (table.Name, table.ColumnCount, table.State))
            .ShouldBe([("authors", 1, SchemaObjectState.New), ("books", 4, SchemaObjectState.New)]);

        (await _api.CountTablesInAsync(project.DatabaseName)).ShouldBe(0);
    }

    [Theory]
    [InlineData("1abc", "only letters")]
    [InlineData("a-b", "only letters")]
    [InlineData("a`b", "only letters")]
    [InlineData("a;drop", "only letters")]
    [InlineData("select", "reserved word")]
    [InlineData("order", "reserved word")]
    [InlineData("id", "primary key")]
    [InlineData("cf_x", "reserved by CoreFoundry")]
    [InlineData("a0123456789012345678901234567890123456789012345678901234567890123", "at most 64")]
    public async Task Invalid_names_are_rejected_with_a_clear_message(string name, string message)
    {
        var (owner, project) = await OwnedProjectAsync();

        var tableProblem = await ValidationProblemAsync(
            await _driver.SendAsync(HttpMethod.Post, Tables(project), owner, new CreateTableRequest(name, null)));
        tableProblem.Errors["name"].ShouldHaveSingleItem().ShouldContain(message);

        var columnProblem = await ValidationProblemAsync(await _driver.SendAsync(HttpMethod.Post, Tables(project), owner,
            new CreateTableRequest("books", [Column("title", DataType.Text), Column(name, DataType.Int)])));
        columnProblem.Errors["columns[1].name"].ShouldHaveSingleItem().ShouldContain(message);
    }

    [Fact]
    public async Task A_64_character_upper_case_name_is_accepted_in_lower_case()
    {
        var (owner, project) = await OwnedProjectAsync();
        var name = "B" + new string('x', 63);

        (await CreateAsync(project, owner, name)).Name.ShouldBe(name.ToLowerInvariant());
    }

    [Fact]
    public async Task Column_errors_are_keyed_by_index_and_field()
    {
        var (owner, project) = await OwnedProjectAsync();

        var problem = await ValidationProblemAsync(await _driver.SendAsync(HttpMethod.Post, Tables(project), owner,
            new CreateTableRequest("books",
            [
                Column("title", DataType.Varchar, length: 100),
                Column("pages", DataType.Int, length: 10),
                Column("price", DataType.Decimal, precision: 5, scale: 6),
                Column("notes", DataType.Json, defaultValue: "{}"),
            ])));

        problem.Errors.Keys.ShouldBe(["columns[1].length", "columns[2].scale", "columns[3].defaultValue"], ignoreOrder: true);
    }

    [Fact]
    public async Task Deleting_a_never_applied_column_removes_it()
    {
        var (owner, project) = await OwnedProjectAsync();
        var table = await CreateAsync(project, owner, "books", Column("title", DataType.Text), Column("pages", DataType.Int));

        var after = await OkAsync<TableDto>(
            HttpMethod.Delete, $"{Tables(project)}/{table.Id}/columns/{table.Columns[1].Id}?version={table.Version}", owner);

        after.Columns.Select(column => column.Name).ShouldBe(["title"]);
    }

    [Fact]
    public async Task Deleting_an_applied_column_marks_it_pending_drop_until_undone()
    {
        var (owner, project) = await OwnedProjectAsync();
        var table = await CreateAsync(project, owner, "books", Column("title", DataType.Text), Column("pages", DataType.Int));
        await _api.MarkAppliedAsync(table.Id);
        var pages = table.Columns[1];

        var deleted = await OkAsync<TableDto>(
            HttpMethod.Delete, $"{Tables(project)}/{table.Id}/columns/{pages.Id}?version={table.Version}", owner);
        deleted.Columns.Single(column => column.Id == pages.Id).State.ShouldBe(SchemaObjectState.PendingDrop);

        // The name stays taken while the drop is pending.
        var reuse = await _driver.SendAsync(HttpMethod.Post, $"{Tables(project)}/{table.Id}/columns", owner,
            new SaveColumnRequest(deleted.Version, "pages", DataType.BigInt, null, null, null, true, false, null));
        (await ValidationProblemAsync(reuse)).Errors.Keys.ShouldBe(["name"]);

        var restored = await OkAsync<TableDto>(HttpMethod.Post, $"{Tables(project)}/{table.Id}/columns/{pages.Id}/restore", owner,
            new VersionRequest(deleted.Version));
        restored.Columns.ShouldAllBe(column => column.State == SchemaObjectState.Applied);
    }

    [Fact]
    public async Task Deleting_tables_hard_deletes_new_ones_and_marks_applied_ones()
    {
        var (owner, project) = await OwnedProjectAsync();
        var draft = await CreateAsync(project, owner, "drafts");
        var applied = await CreateAsync(project, owner, "books", Column("title", DataType.Text));
        await _api.MarkAppliedAsync(applied.Id);

        (await _driver.SendAsync(HttpMethod.Delete, $"{Tables(project)}/{draft.Id}?version={draft.Version}", owner))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var marked = await OkAsync<TableDto>(HttpMethod.Delete, $"{Tables(project)}/{applied.Id}?version={applied.Version}", owner);

        marked.State.ShouldBe(SchemaObjectState.PendingDrop);
        marked.Columns.ShouldAllBe(column => column.State == SchemaObjectState.PendingDrop);
        (await OkAsync<List<TableSummaryDto>>(HttpMethod.Get, Tables(project), owner)).Select(table => table.Name).ShouldBe(["books"]);

        var restored = await OkAsync<TableDto>(HttpMethod.Post, $"{Tables(project)}/{applied.Id}/restore", owner, new VersionRequest(marked.Version));
        restored.State.ShouldBe(SchemaObjectState.Applied);
    }

    [Fact]
    public async Task A_table_from_another_project_is_404_even_though_its_id_exists()
    {
        var (owner, mine) = await OwnedProjectAsync();
        var other = await _driver.CreateProjectAsync(owner, "Other");
        var foreign = await CreateAsync(other, owner, "secrets", Column("value", DataType.Text));

        (await _driver.SendAsync(HttpMethod.Get, $"{Tables(mine)}/{foreign.Id}", owner)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _driver.SendAsync(HttpMethod.Put, $"{Tables(mine)}/{foreign.Id}", owner, new RenameTableRequest(foreign.Version, "x")))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _driver.SendAsync(HttpMethod.Delete, $"{Tables(mine)}/{foreign.Id}/columns/{foreign.Columns[0].Id}?version={foreign.Version}", owner))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_column_from_another_table_is_404()
    {
        var (owner, project) = await OwnedProjectAsync();
        var books = await CreateAsync(project, owner, "books", Column("title", DataType.Text));
        var authors = await CreateAsync(project, owner, "authors");

        (await _driver.SendAsync(HttpMethod.Delete, $"{Tables(project)}/{authors.Id}/columns/{books.Columns[0].Id}?version={authors.Version}", owner))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Non_members_get_404()
    {
        var (_, project) = await OwnedProjectAsync();
        var stranger = await _driver.SignUpAsync();

        (await _driver.SendAsync(HttpMethod.Get, Tables(project), stranger)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Editing_with_a_stale_version_is_409()
    {
        var (owner, project) = await OwnedProjectAsync();
        var table = await CreateAsync(project, owner, "books");
        await OkAsync<TableDto>(HttpMethod.Put, $"{Tables(project)}/{table.Id}", owner, new RenameTableRequest(table.Version, "novels"));

        (await _driver.SendAsync(HttpMethod.Post, $"{Tables(project)}/{table.Id}/columns", owner,
            new SaveColumnRequest(table.Version, "title", DataType.Text, null, null, null, true, false, null)))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Concurrent_edits_of_the_same_version_let_exactly_one_win()
    {
        var (owner, project) = await OwnedProjectAsync();
        var table = await CreateAsync(project, owner, "books");

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => _driver.SendAsync(
            HttpMethod.Post, $"{Tables(project)}/{table.Id}/columns", owner,
            new SaveColumnRequest(table.Version, $"c{i}", DataType.Int, null, null, null, true, false, null))));

        var outcomes = await Task.WhenAll(responses.Select(async response =>
            $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}"));
        string.Join(",", outcomes.Select(outcome => outcome[..3]).Order()).ShouldBe("200,409,409,409", string.Join(" | ", outcomes));
        (await OkAsync<TableDto>(HttpMethod.Get, $"{Tables(project)}/{table.Id}", owner)).Columns.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Delete_without_a_version_is_400()
    {
        var (owner, project) = await OwnedProjectAsync();
        var table = await CreateAsync(project, owner, "books");

        (await _driver.SendAsync(HttpMethod.Delete, $"{Tables(project)}/{table.Id}", owner)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    // ---- Access (M8) ---------------------------------------------------------------------------------

    [Fact]
    public async Task New_tables_default_to_signed_in_read_and_write_in_list_and_single()
    {
        var (owner, project) = await OwnedProjectAsync("AccessDefaults");
        var table = await CreateAsync(project, owner, "books");

        (table.ReadAccess, table.WriteAccess).ShouldBe((AccessLevel.SignedIn, AccessLevel.SignedIn));

        var fetched = await OkAsync<TableDto>(HttpMethod.Get, $"{Tables(project)}/{table.Id}", owner);
        (fetched.ReadAccess, fetched.WriteAccess).ShouldBe((AccessLevel.SignedIn, AccessLevel.SignedIn));

        var list = await OkAsync<List<TableSummaryDto>>(HttpMethod.Get, Tables(project), owner);
        list.Single().ReadAccess.ShouldBe(AccessLevel.SignedIn);
        list.Single().WriteAccess.ShouldBe(AccessLevel.SignedIn);
    }

    [Fact]
    public async Task Setting_access_updates_it_and_returns_the_new_version()
    {
        var (owner, project) = await OwnedProjectAsync("AccessUpdate");
        var table = await CreateAsync(project, owner, "books");

        var updated = await OkAsync<TableDto>(HttpMethod.Put, $"{Tables(project)}/{table.Id}/access", owner,
            new TableAccessRequest(table.Version, AccessLevel.Public, AccessLevel.Admin));

        (updated.ReadAccess, updated.WriteAccess).ShouldBe((AccessLevel.Public, AccessLevel.Admin));
        updated.Version.ShouldBeGreaterThan(table.Version);
    }

    [Fact]
    public async Task Setting_access_with_a_stale_version_is_409()
    {
        var (owner, project) = await OwnedProjectAsync("AccessStale");
        var table = await CreateAsync(project, owner, "books");
        await OkAsync<TableDto>(HttpMethod.Put, $"{Tables(project)}/{table.Id}/access", owner,
            new TableAccessRequest(table.Version, AccessLevel.Public, AccessLevel.Public));

        (await _driver.SendAsync(HttpMethod.Put, $"{Tables(project)}/{table.Id}/access", owner,
            new TableAccessRequest(table.Version, AccessLevel.Admin, AccessLevel.Admin)))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Setting_access_for_a_non_member_is_404()
    {
        var (_, project) = await OwnedProjectAsync("AccessStranger");
        var stranger = await _driver.SignUpAsync();

        (await _driver.SendAsync(HttpMethod.Put, $"{Tables(project)}/1/access", stranger,
            new TableAccessRequest(1, AccessLevel.Public, AccessLevel.Public)))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Write_wider_than_read_is_400()
    {
        var (owner, project) = await OwnedProjectAsync("AccessInvalid");
        var table = await CreateAsync(project, owner, "books");

        var problem = await ValidationProblemAsync(await _driver.SendAsync(HttpMethod.Put, $"{Tables(project)}/{table.Id}/access", owner,
            new TableAccessRequest(table.Version, AccessLevel.Admin, AccessLevel.Public)));

        problem.Errors.Keys.ShouldBe(["write"]);
    }

    [Fact]
    public async Task Changing_access_does_not_appear_in_the_schema_plan()
    {
        var (owner, project) = await OwnedProjectAsync("AccessPlan");
        var table = await CreateAsync(project, owner, "books", Column("title", DataType.Text));

        var before = await OkAsync<SchemaPlanDto>(HttpMethod.Get, $"/api/projects/{project.Id}/schema/plan", owner);
        await OkAsync<TableDto>(HttpMethod.Put, $"{Tables(project)}/{table.Id}/access", owner,
            new TableAccessRequest(table.Version, AccessLevel.Public, AccessLevel.Admin));
        var after = await OkAsync<SchemaPlanDto>(HttpMethod.Get, $"/api/projects/{project.Id}/schema/plan", owner);

        after.PlanHash.ShouldBe(before.PlanHash);
        after.Statements.ShouldBe(before.Statements);
    }

    // ---- References (M2.5) -------------------------------------------------------------------------

    [Fact]
    public async Task A_reference_is_saved_and_the_schema_shows_it_without_touching_the_project_database()
    {
        var (owner, project) = await OwnedProjectAsync();
        var authors = await CreateAsync(project, owner, "authors", Column("name", DataType.Varchar, length: 100));
        await CreateAsync(project, owner, "books", Reference("author_id", authors.Id, ReferenceAction.Cascade, nullable: false));

        var schema = await OkAsync<List<TableDto>>(HttpMethod.Get, $"/api/projects/{project.Id}/schema", owner);

        schema.Select(table => table.Name).ShouldBe(["authors", "books"]);
        var authorId = schema[1].Columns.Single();
        (authorId.ReferencesTableId, authorId.ReferencesTableName, authorId.OnDelete, authorId.DataType)
            .ShouldBe((authors.Id, "authors", ReferenceAction.Cascade, DataType.BigInt));
        (await _api.CountTablesInAsync(project.DatabaseName)).ShouldBe(0);
    }

    [Fact]
    public async Task Invalid_references_are_400_on_the_right_field()
    {
        var (owner, project) = await OwnedProjectAsync();
        var other = await _driver.CreateProjectAsync(owner, "Other");
        var foreign = await CreateAsync(other, owner, "secrets");
        var authors = await CreateAsync(project, owner, "authors");

        var problem = await ValidationProblemAsync(await _driver.SendAsync(HttpMethod.Post, Tables(project), owner,
            new CreateTableRequest("books",
            [
                Column("author_id", DataType.Int) with { ReferencesTableId = authors.Id, OnDelete = ReferenceAction.Restrict },
                Reference("editor_id", authors.Id, ReferenceAction.SetNull, nullable: false),
                Reference("secret_id", foreign.Id, ReferenceAction.Restrict),
            ])));

        problem.Errors.Keys.ShouldBe(["columns[0].dataType", "columns[1].onDelete", "columns[2].referencesTableId"], ignoreOrder: true);
    }

    [Fact]
    public async Task A_referenced_table_cannot_be_deleted_until_the_reference_is_removed()
    {
        var (owner, project) = await OwnedProjectAsync();
        var authors = await CreateAsync(project, owner, "authors");
        var books = await CreateAsync(project, owner, "books", Reference("author_id", authors.Id, ReferenceAction.Restrict));

        var refused = await _driver.SendAsync(HttpMethod.Delete, $"{Tables(project)}/{authors.Id}?version={authors.Version}", owner);
        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadAsync<ProblemDetails>(refused)).Detail.ShouldNotBeNull().ShouldContain("books.author_id");

        await OkAsync<TableDto>(HttpMethod.Delete, $"{Tables(project)}/{books.Id}/columns/{books.Columns[0].Id}?version={books.Version}", owner);
        (await _driver.SendAsync(HttpMethod.Delete, $"{Tables(project)}/{authors.Id}?version={authors.Version}", owner))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Deleting_a_project_with_references_between_its_tables_works()
    {
        var (owner, project) = await OwnedProjectAsync();
        var authors = await CreateAsync(project, owner, "authors");
        var employees = await CreateAsync(project, owner, "employees");
        await OkAsync<TableDto>(HttpMethod.Post, $"{Tables(project)}/{employees.Id}/columns", owner,
            new SaveColumnRequest(employees.Version, "manager_id", DataType.BigInt, null, null, null, true, false, null, employees.Id, ReferenceAction.SetNull));
        await CreateAsync(project, owner, "books", Reference("author_id", authors.Id, ReferenceAction.Cascade));

        (await _driver.SendAsync(HttpMethod.Delete, $"/api/projects/{project.Id}", owner)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await _driver.SendAsync(HttpMethod.Get, $"/api/projects/{project.Id}", owner)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private static ColumnRequest Reference(string name, long tableId, ReferenceAction onDelete, bool nullable = true) =>
        new(name, DataType.BigInt, null, null, null, nullable, false, null, tableId, onDelete);

    private async Task<(SignedIn Owner, ProjectDto Project)> OwnedProjectAsync(string name = "Bookshop")
    {
        var owner = await _driver.SignUpAsync();
        return (owner, await _driver.CreateProjectAsync(owner, name));
    }

    private async Task<TableDto> CreateAsync(ProjectDto project, SignedIn user, string name, params ColumnRequest[] columns)
    {
        var response = await _driver.SendAsync(HttpMethod.Post, Tables(project), user, new CreateTableRequest(name, columns));
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return await ReadAsync<TableDto>(response);
    }

    private async Task<T> OkAsync<T>(HttpMethod method, string path, SignedIn user, object? body = null)
    {
        var response = await _driver.SendAsync(method, path, user, body);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return await ReadAsync<T>(response);
    }

    private static async Task<ValidationProblemDetails> ValidationProblemAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        return await ReadAsync<ValidationProblemDetails>(response);
    }

    private static ColumnRequest Column(
        string name, DataType type, int? length = null, int? precision = null, int? scale = null,
        bool nullable = true, string? defaultValue = null) =>
        new(name, type, length, precision, scale, nullable, false, defaultValue);

    private static string Tables(ProjectDto project) => $"/api/projects/{project.Id}/tables";
}
