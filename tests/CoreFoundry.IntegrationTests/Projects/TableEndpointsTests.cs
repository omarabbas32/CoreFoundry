using System.Net;
using CoreFoundry.Api.Projects;
using CoreFoundry.Application.Projects;
using CoreFoundry.Application.Schema;
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

        responses.Count(response => response.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        responses.Count(response => response.StatusCode == HttpStatusCode.Conflict).ShouldBe(3);
        (await OkAsync<TableDto>(HttpMethod.Get, $"{Tables(project)}/{table.Id}", owner)).Columns.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Delete_without_a_version_is_400()
    {
        var (owner, project) = await OwnedProjectAsync();
        var table = await CreateAsync(project, owner, "books");

        (await _driver.SendAsync(HttpMethod.Delete, $"{Tables(project)}/{table.Id}", owner)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private async Task<(SignedIn Owner, ProjectDto Project)> OwnedProjectAsync()
    {
        var owner = await _driver.SignUpAsync();
        return (owner, await _driver.CreateProjectAsync(owner, "Bookshop"));
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
