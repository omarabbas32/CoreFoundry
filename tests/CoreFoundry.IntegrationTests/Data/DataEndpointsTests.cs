using System.Net;
using System.Text.Json;
using CoreFoundry.Api.Projects;
using CoreFoundry.Application.Data;
using CoreFoundry.Application.Projects;
using CoreFoundry.Application.Schema;
using CoreFoundry.Application.SchemaEngine;
using CoreFoundry.Domain.Projects;
using CoreFoundry.Domain.Schema;
using CoreFoundry.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using Shouldly;
using static CoreFoundry.IntegrationTests.Infrastructure.ApiDriver;

namespace CoreFoundry.IntegrationTests.Data;

/// <summary>The Data API end to end: design, apply, then rows through <c>/api/projects/{id}/data</c>.</summary>
[Collection(TestDatabaseGroup.Name)]
public sealed class DataEndpointsTests : IDisposable
{
    private readonly TestDatabaseApi _api;
    private readonly HttpClient _client;
    private readonly ApiDriver _driver;

    public DataEndpointsTests(TestDatabaseApi api)
    {
        api.SkipIfUnavailable();
        _api = api;
        _client = api.CreateHttpsClient();
        _driver = new ApiDriver(_client);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task Crud_round_trip_on_an_applied_table()
    {
        var shop = await BookshopAsync();
        var author = await CreateAsync(shop, "authors", """{ "name": "Frank Herbert" }""");

        var response = await SendAsync(shop, HttpMethod.Post, "books", $$"""{ "title": "Dune", "price": 19.9, "author_id": {{author.GetProperty("id")}} }""");
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        var created = await ReadAsync<JsonElement>(response);
        var id = created.GetProperty("id").GetInt64();
        response.Headers.Location!.ToString().ShouldBe($"/api/projects/{shop.Project.Id}/data/books/{id}");
        created.GetProperty("price").GetString().ShouldBe("19.90"); // decimals are strings
        created.GetProperty("id").ValueKind.ShouldBe(JsonValueKind.Number);
        created.GetProperty("isbn").ValueKind.ShouldBe(JsonValueKind.Null);

        var read = await OkAsync<JsonElement>(shop, HttpMethod.Get, $"books/{id}");
        read.GetProperty("title").GetString().ShouldBe("Dune");

        var replaced = await OkAsync<JsonElement>(shop, HttpMethod.Put, $"books/{id}", """{ "title": "Dune Messiah", "isbn": "978-0" }""");
        (replaced.GetProperty("title").GetString(), replaced.GetProperty("price").GetString(), replaced.GetProperty("author_id").ValueKind)
            .ShouldBe(("Dune Messiah", "0.00", JsonValueKind.Null)); // full replace: default and NULL for what was left out

        (await SendAsync(shop, HttpMethod.Delete, $"books/{id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await SendAsync(shop, HttpMethod.Get, $"books/{id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await SendAsync(shop, HttpMethod.Delete, $"books/{id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await SendAsync(shop, HttpMethod.Put, $"books/{id}", """{ "title": "x" }""")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Pages_sort_stably_and_bad_paging_is_400()
    {
        var shop = await BookshopAsync();
        foreach (var price in new[] { "5", "2", "9", "2", "2", "7" })
        {
            await CreateAsync(shop, "books", $$"""{ "title": "t{{price}}", "price": "{{price}}" }""");
        }

        var first = await OkAsync<DataPage>(shop, HttpMethod.Get, "books?sort=price&pageSize=4");
        var second = await OkAsync<DataPage>(shop, HttpMethod.Get, "books?sort=price&pageSize=4&page=2");

        (first.Total, first.Page, first.PageSize, second.Items.Count).ShouldBe((6, 1, 4, 2));
        first.Items.Concat(second.Items).Select(row => row.GetProperty("price").GetString())
            .ShouldBe(["2.00", "2.00", "2.00", "5.00", "7.00", "9.00"]);
        var twos = first.Items.Take(3).Select(row => row.GetProperty("id").GetInt64()).ToList();
        twos.ShouldBe([.. twos.Order()]); // equal prices come back by id

        (await OkAsync<DataPage>(shop, HttpMethod.Get, "books?sort=-price&pageSize=1")).Items.Single().GetProperty("price").GetString().ShouldBe("9.00");

        var bad = await ValidationAsync(await SendAsync(shop, HttpMethod.Get, "books?page=0&pageSize=101&sort=nope"));
        bad.Errors.Keys.ShouldBe(["page", "pageSize", "sort"], ignoreOrder: true);
    }

    [Fact]
    public async Task Errors_name_the_field_unique_is_409_and_references_are_checked()
    {
        var shop = await BookshopAsync();
        await CreateAsync(shop, "books", """{ "title": "a", "isbn": "978" }""");

        var duplicate = await SendAsync(shop, HttpMethod.Post, "books", """{ "title": "b", "isbn": "978" }""");
        duplicate.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadAsync<ProblemDetails>(duplicate)).Detail.ShouldBe("isbn must be unique; 978 already exists.");

        var invalid = await ValidationAsync(await SendAsync(shop, HttpMethod.Post, "books", """{ "price": 1.999, "pages": "12", "id": 3 }"""));
        invalid.Errors.Keys.ShouldBe(["title", "price", "pages", "id"], ignoreOrder: true);

        var missingParent = await ValidationAsync(await SendAsync(shop, HttpMethod.Post, "books", """{ "title": "c", "author_id": 999999 }"""));
        missingParent.Errors["author_id"].ShouldBe(["No authors row with id 999999."]);
    }

    [Fact]
    public async Task Deleting_a_row_other_rows_reference_is_409_with_restrict()
    {
        var shop = await BookshopAsync(onDelete: ReferenceAction.Restrict);
        var author = (await CreateAsync(shop, "authors", """{ "name": "Ann" }""")).GetProperty("id").GetInt64();
        await CreateAsync(shop, "books", $$"""{ "title": "a", "author_id": {{author}} }""");

        var response = await SendAsync(shop, HttpMethod.Delete, $"authors/{author}");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadAsync<ProblemDetails>(response)).Detail.ShouldNotBeNull().ShouldContain("books.author_id");
    }

    [Fact]
    public async Task Draft_only_columns_and_tables_do_not_exist_until_applied_and_renames_apply_at_once()
    {
        var shop = await BookshopAsync();
        await CreateAsync(shop, "books", """{ "title": "Dune", "price": "12.34" }""");

        // Draft changes: rename price → price_usd, add subtitle, add a table. Not applied yet.
        var books = await _driver.OkAsync<TableDto>(HttpMethod.Get, $"/api/projects/{shop.Project.Id}/tables/{shop.BooksId}", shop.Owner);
        var price = books.Columns.Single(column => column.Name == "price");
        books = await _driver.OkAsync<TableDto>(HttpMethod.Put, $"/api/projects/{shop.Project.Id}/tables/{books.Id}/columns/{price.Id}", shop.Owner,
            new SaveColumnRequest(books.Version, "price_usd", DataType.Decimal, null, 10, 2, false, false, "0.00"));
        await _driver.OkAsync<TableDto>(HttpMethod.Post, $"/api/projects/{shop.Project.Id}/tables/{books.Id}/columns", shop.Owner,
            new SaveColumnRequest(books.Version, "subtitle", DataType.Varchar, 50, null, null, true, false, null));
        await CreateTableAsync(shop, "reviews", Column("body", DataType.Text));

        var unknown = await ValidationAsync(await SendAsync(shop, HttpMethod.Post, "books", """{ "title": "x", "subtitle": "y", "price_usd": 1 }"""));
        unknown.Errors.Keys.ShouldBe(["subtitle", "price_usd"], ignoreOrder: true);
        (await SendAsync(shop, HttpMethod.Get, "reviews")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        await ApplyAsync(shop);

        var row = (await OkAsync<DataPage>(shop, HttpMethod.Get, "books?sort=-price_usd")).Items.Single();
        row.GetProperty("price_usd").GetString().ShouldBe("12.34");
        row.TryGetProperty("price", out _).ShouldBeFalse();
        (await ValidationAsync(await SendAsync(shop, HttpMethod.Get, "books?sort=price"))).Errors.Keys.ShouldBe(["sort"]);
        (await OkAsync<DataPage>(shop, HttpMethod.Get, "reviews")).Total.ShouldBe(0);
    }

    [Theory]
    [InlineData("books`; DROP TABLE books; --")]
    [InlineData("BOOKS")]
    [InlineData("books ")]
    [InlineData("information_schema.tables")]
    [InlineData("..%2Fauthors")]
    public async Task Crafted_table_names_are_404(string table)
    {
        var shop = await BookshopAsync();

        (await SendAsync(shop, HttpMethod.Get, Uri.EscapeDataString(table))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _api.CountTablesInAsync(shop.Project.DatabaseName)).ShouldBe(2);
    }

    [Fact]
    public async Task Crafted_sort_and_keys_are_400_and_never_reach_sql()
    {
        var shop = await BookshopAsync();

        (await ValidationAsync(await SendAsync(shop, HttpMethod.Get, "books?sort=" + Uri.EscapeDataString("title; DROP TABLE books"))))
            .Errors.Keys.ShouldBe(["sort"]);
        var keys = await ValidationAsync(await SendAsync(shop, HttpMethod.Post, "books",
            """{ "title": "x", "title`) VALUES (1); DROP TABLE books; --": 1 }"""));
        keys.Errors.Keys.ShouldBe(["title`) VALUES (1); DROP TABLE books; --"]);

        (await _api.CountTablesInAsync(shop.Project.DatabaseName)).ShouldBe(2);
        (await OkAsync<DataPage>(shop, HttpMethod.Get, "books")).Total.ShouldBe(0);
    }

    [Fact]
    public async Task Tables_created_outside_CoreFoundry_are_not_served()
    {
        var shop = await BookshopAsync();
        await ExecuteAsync($"CREATE TABLE `{shop.Project.DatabaseName}`.`manual` (`x` INT)");
        await ExecuteAsync($"ALTER TABLE `{shop.Project.DatabaseName}`.`books` ADD COLUMN `legacy` INT");

        (await SendAsync(shop, HttpMethod.Get, "manual")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var schema = await OkAsync<DataSchemaDto>(shop, HttpMethod.Get, "");
        schema.Tables.Select(table => table.Name).ShouldBe(["authors", "books"]);
        schema.Tables[1].Columns.Select(column => column.Name).ShouldNotContain("legacy");
    }

    [Fact]
    public async Task The_tables_endpoint_describes_columns_for_forms()
    {
        var shop = await BookshopAsync();

        var schema = await OkAsync<DataSchemaDto>(shop, HttpMethod.Get, "");

        schema.SchemaVersion.ShouldBe(1);
        var books = schema.Tables.Single(table => table.Name == "books");
        books.LabelColumn.ShouldBe("title");
        books.Columns.Single(column => column.Name == "price").ShouldBe(
            new DataColumnDto("price", "Decimal(10,2)", "Decimal", null, 10, 2, false, false, "0.00", null, true));
        books.Columns.Single(column => column.Name == "author_id").References.ShouldBe("authors");
    }

    [Fact]
    public async Task Lookup_lists_ids_and_labels_for_reference_pickers()
    {
        var shop = await BookshopAsync();
        var ids = new List<long>();
        foreach (var name in new[] { "Ursula", "Frank", "%_real" })
        {
            ids.Add((await CreateAsync(shop, "authors", $$"""{ "name": "{{name}}" }""")).GetProperty("id").GetInt64());
        }

        (await OkAsync<List<LookupItem>>(shop, HttpMethod.Get, "authors/lookup")).Select(item => item.Label).ShouldBe(["%_real", "Frank", "Ursula"]);
        (await OkAsync<List<LookupItem>>(shop, HttpMethod.Get, "authors/lookup?q=ank")).ShouldBe([new LookupItem(ids[1], "Frank")]);
        (await OkAsync<List<LookupItem>>(shop, HttpMethod.Get, "authors/lookup?q=%25_")).Select(item => item.Label).ShouldBe(["%_real"]);
        (await OkAsync<List<LookupItem>>(shop, HttpMethod.Get, $"authors/lookup?q={ids[0]}")).Select(item => item.Id).ShouldBe([ids[0]]);
        (await OkAsync<List<LookupItem>>(shop, HttpMethod.Get, "authors/lookup?limit=1")).Count.ShouldBe(1);
        (await ValidationAsync(await SendAsync(shop, HttpMethod.Get, "authors/lookup?limit=0"))).Errors.Keys.ShouldBe(["limit"]);
    }

    [Fact]
    public async Task Developers_can_write_rows_and_non_members_see_nothing()
    {
        var shop = await BookshopAsync();
        var developer = await _driver.SignUpAsync();
        await _driver.AddMemberAsync(shop.Project.Id, shop.Owner, developer, ProjectRole.Developer);
        var stranger = await _driver.SignUpAsync();

        (await _driver.SendAsync(HttpMethod.Post, $"/api/projects/{shop.Project.Id}/data/authors", developer, Json("""{ "name": "Dev" }""")))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        (await _driver.SendAsync(HttpMethod.Get, $"/api/projects/{shop.Project.Id}/data/authors", stranger))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_project_with_nothing_applied_has_no_tables()
    {
        var owner = await _driver.SignUpAsync();
        var project = await _driver.CreateProjectAsync(owner, "Empty");
        await _driver.SendAsync(HttpMethod.Post, $"/api/projects/{project.Id}/tables", owner, new CreateTableRequest("books", []));

        (await _driver.OkAsync<DataSchemaDto>(HttpMethod.Get, $"/api/projects/{project.Id}/data", owner)).Tables.ShouldBeEmpty();
        (await _driver.SendAsync(HttpMethod.Get, $"/api/projects/{project.Id}/data/books", owner)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private sealed record Shop(SignedIn Owner, ProjectDto Project, long AuthorsId, long BooksId);

    private sealed record DataPage(List<JsonElement> Items, int Page, int PageSize, long Total);

    /// <summary>Bookshop with authors and books (title, unique isbn, price with a default, pages, author_id), applied.</summary>
    private async Task<Shop> BookshopAsync(ReferenceAction onDelete = ReferenceAction.Cascade)
    {
        var owner = await _driver.SignUpAsync();
        var project = await _driver.CreateProjectAsync(owner, "Bookshop");
        var shop = new Shop(owner, project, 0, 0);
        var authors = await CreateTableAsync(shop, "authors", Column("name", DataType.Varchar, length: 120, nullable: false));
        var books = await CreateTableAsync(shop, "books",
            Column("title", DataType.Varchar, length: 200, nullable: false),
            Column("isbn", DataType.Varchar, length: 20, unique: true),
            Column("price", DataType.Decimal, precision: 10, scale: 2, nullable: false, defaultValue: "0.00"),
            Column("pages", DataType.Int),
            Column("author_id", DataType.BigInt) with { ReferencesTableId = authors.Id, OnDelete = onDelete });
        shop = shop with { AuthorsId = authors.Id, BooksId = books.Id };
        await ApplyAsync(shop);
        return shop;
    }

    private async Task ApplyAsync(Shop shop)
    {
        var plan = await _driver.OkAsync<SchemaPlanDto>(HttpMethod.Get, $"/api/projects/{shop.Project.Id}/schema/plan", shop.Owner);
        await _driver.OkAsync<ApplyResultDto>(HttpMethod.Post, $"/api/projects/{shop.Project.Id}/schema/apply", shop.Owner, new ApplyRequest(plan.PlanHash, false));
    }

    private Task<TableDto> CreateTableAsync(Shop shop, string name, params ColumnRequest[] columns) =>
        _driver.OkAsync<TableDto>(HttpMethod.Post, $"/api/projects/{shop.Project.Id}/tables", shop.Owner, new CreateTableRequest(name, columns), HttpStatusCode.Created);

    private async Task<JsonElement> CreateAsync(Shop shop, string table, string json)
    {
        var response = await SendAsync(shop, HttpMethod.Post, table, json);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return await ReadAsync<JsonElement>(response);
    }

    private Task<HttpResponseMessage> SendAsync(Shop shop, HttpMethod method, string path, string? json = null) =>
        _driver.SendAsync(method, Path(shop, path), shop.Owner, json is null ? null : Json(json));

    private Task<T> OkAsync<T>(Shop shop, HttpMethod method, string path, string? json = null) =>
        _driver.OkAsync<T>(method, Path(shop, path), shop.Owner, json is null ? null : Json(json));

    private static string Path(Shop shop, string path) =>
        $"/api/projects/{shop.Project.Id}/data" + (path.Length == 0 ? "" : "/" + path);

    private static async Task<ValidationProblemDetails> ValidationAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync(Ct));
        return await ReadAsync<ValidationProblemDetails>(response);
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new MySqlConnection(_api.EngineConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new MySqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static ColumnRequest Column(
        string name, DataType type, int? length = null, int? precision = null, int? scale = null,
        bool nullable = true, bool unique = false, string? defaultValue = null) =>
        new(name, type, length, precision, scale, nullable, unique, defaultValue);
}
