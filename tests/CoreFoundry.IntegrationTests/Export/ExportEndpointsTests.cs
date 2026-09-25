using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CoreFoundry.Api.Projects;
using CoreFoundry.Application.Projects;
using CoreFoundry.Application.Schema;
using CoreFoundry.Application.SchemaEngine;
using CoreFoundry.Domain.Projects;
using CoreFoundry.Domain.Schema;
using CoreFoundry.Infrastructure.Engine;
using CoreFoundry.IntegrationTests.Infrastructure;
using Shouldly;

namespace CoreFoundry.IntegrationTests.Export;

/// <summary>
/// Code export end to end: the zip's content, and the exported backend itself (built, its migration
/// checked by EF, run against MySQL, used over HTTP, and its tables compared with CoreFoundry's).
/// </summary>
[Collection(TestDatabaseGroup.Name)]
public sealed class ExportEndpointsTests : IDisposable
{
    private readonly TestDatabaseApi _api;
    private readonly HttpClient _client;
    private readonly ApiDriver _driver;

    public ExportEndpointsTests(TestDatabaseApi api)
    {
        api.SkipIfUnavailable();
        _api = api;
        _client = api.CreateHttpsClient();
        _driver = new ApiDriver(_client);
    }

    private static readonly string[] Tags = ["sf"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task The_zip_holds_the_solution_under_one_folder()
    {
        var shop = await BookStoreAsync();

        var response = await _driver.SendAsync(HttpMethod.Get, $"/api/projects/{shop.Project.Id}/export", shop.Owner);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/zip");
        response.Content.Headers.ContentDisposition!.FileNameStar.ShouldBe("bookstore-backend.zip");
        using var zip = new ZipArchive(await response.Content.ReadAsStreamAsync(Ct));
        var names = zip.Entries.Select(entry => entry.FullName).ToList();
        names.ShouldAllBe(name => name.StartsWith("bookstore-backend/", StringComparison.Ordinal));
        names.ShouldContain("bookstore-backend/BookStore.slnx");
        names.ShouldContain("bookstore-backend/src/BookStore.Domain/Entities/Book.cs");
        names.ShouldContain("bookstore-backend/docker-compose.yml");
        names.ShouldContain(name => name.EndsWith("_InitialCreate.cs", StringComparison.Ordinal));

        using var reader = new StreamReader(zip.GetEntry("bookstore-backend/README.md")!.Open());
        (await reader.ReadToEndAsync(Ct)).ShouldContain("### `books`");
    }

    [Fact]
    public async Task Nothing_applied_is_409_and_non_members_get_404()
    {
        var owner = await _driver.SignUpAsync();
        var project = await _driver.CreateProjectAsync(owner, "Empty");
        await CreateTableAsync(project, owner, "books", Column("title", DataType.Text));
        var stranger = await _driver.SignUpAsync();

        (await _driver.SendAsync(HttpMethod.Get, $"/api/projects/{project.Id}/export", owner)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await _driver.SendAsync(HttpMethod.Get, $"/api/projects/{project.Id}/export", stranger)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// Slow (builds a .NET solution, ~1 minute). Set CF_SKIP_EXPORT_BUILD=1 to skip it. Needs the .NET 10 SDK and NuGet access;
    /// the EF check also needs <c>dotnet-ef</c> on the PATH (it is skipped without it).
    /// </summary>
    [Fact]
    public async Task The_exported_backend_builds_matches_its_migration_and_serves_the_same_tables()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("CF_SKIP_EXPORT_BUILD") == "1", "CF_SKIP_EXPORT_BUILD=1");
        var shop = await BookStoreAsync();
        var zip = await (await _driver.SendAsync(HttpMethod.Get, $"/api/projects/{shop.Project.Id}/export", shop.Owner)).Content.ReadAsByteArrayAsync(Ct);
        await ExportedBackendSupport.RunExportedBackendAsync(zip, "BookStore", _api.EngineConnectionString, async (http, database) =>
        {
            // Auth and CRUD work over HTTP.
            await UseTheApiAsync(http);

            // Its tables are the ones CoreFoundry applied (plus its accounts table and EF's migrations history).
            var introspector = new MySqlSchemaIntrospector(_api.EngineConnectionString);
            var applied = SchemaSnapshot.From(await introspector.ReadAsync(shop.Project.DatabaseName, Ct));
            var exported = SchemaSnapshot.From(await introspector.ReadAsync(database, Ct));
            exported.FindTable("cf_users").ShouldNotBeNull();
            // Table names come back as MySQL stores them: lower-cased on Windows (lower_case_table_names=1),
            // as written on Linux. Compare without case so this holds on both.
            var withoutAccounts = new SchemaSnapshot([.. exported.Tables.Where(table =>
                !table.Name.Equals("cf_users", StringComparison.OrdinalIgnoreCase) &&
                !table.Name.Equals("__EFMigrationsHistory", StringComparison.OrdinalIgnoreCase))]);
            applied.DifferencesTo(withoutAccounts).ShouldBeEmpty();
        });
    }

    /// <summary>What a client of the exported API does, with the answers the README promises.</summary>
    private static async Task UseTheApiAsync(HttpClient http)
    {
        (await http.GetAsync(new Uri("/api/books", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var credentials = new { email = "Reader@Example.com", password = "a long password" };
        (await ExportedBackendSupport.PostAsync(http, "/api/auth/register", credentials)).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await ExportedBackendSupport.PostAsync(http, "/api/auth/register", credentials)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ExportedBackendSupport.PostAsync(http, "/api/auth/login", new { email = "reader@example.com", password = "wrong password" })).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized);
        var login = await ExportedBackendSupport.Json(
            await ExportedBackendSupport.PostAsync(http, "/api/auth/login", new { email = "reader@example.com", password = "a long password" }), HttpStatusCode.OK);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.GetProperty("accessToken").GetString());

        var ann = (await ExportedBackendSupport.Json(await ExportedBackendSupport.PostAsync(http, "/api/authors", new { name = "Ann" }), HttpStatusCode.Created)).GetProperty("id").GetInt64();
        var bob = (await ExportedBackendSupport.Json(await ExportedBackendSupport.PostAsync(http, "/api/authors", new { name = "Bob", mentor_id = ann }), HttpStatusCode.Created)).GetProperty("id").GetInt64();
        (await ExportedBackendSupport.PostAsync(http, "/api/authors", new { name = "Ann" })).StatusCode.ShouldBe(HttpStatusCode.Conflict); // unique

        var book = await ExportedBackendSupport.Json(await ExportedBackendSupport.PostAsync(http, "/api/books", new
        {
            title = "Dune",
            price_usd = "19.9",
            copies = 3,
            added_at = "2024-02-29T13:45:07.123456+02:00",
            metadata = new { tags = Tags },
            author_id = ann,
            editor_id = bob,
        }), HttpStatusCode.Created);
        var id = book.GetProperty("id").GetInt64();
        book.GetProperty("price_usd").GetString().ShouldBe("19.90");
        book.GetProperty("in_stock").GetBoolean().ShouldBeTrue(); // column default
        book.GetProperty("added_at").GetString().ShouldBe("2024-02-29T11:45:07.123456"); // offset → UTC
        Guid.Parse(book.GetProperty("public_id").GetString()!).ShouldNotBe(Guid.Empty); // (UUID()) default
        book.GetProperty("metadata").GetProperty("tags")[0].GetString().ShouldBe("sf");

        var missingParent = await ExportedBackendSupport.Json(await ExportedBackendSupport.PostAsync(http, "/api/books", new { title = "x", copies = 1, author_id = 999_999 }), HttpStatusCode.BadRequest);
        missingParent.GetProperty("errors").TryGetProperty("author_id", out _).ShouldBeTrue();
        var invalid = await ExportedBackendSupport.Json(await ExportedBackendSupport.PostAsync(http, "/api/books", new { title = "x", copies = 1, author_id = ann, price_usd = 1.999 }), HttpStatusCode.BadRequest);
        invalid.GetProperty("errors").TryGetProperty("price_usd", out _).ShouldBeTrue();
        (await ExportedBackendSupport.PostAsync(http, "/api/books", new { title = "x", copies = 1, author_id = ann, nope = 1 })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var page = await ExportedBackendSupport.Json(await http.GetAsync(new Uri("/api/books?sort=-price_usd&pageSize=10", UriKind.Relative), Ct), HttpStatusCode.OK);
        (page.GetProperty("total").GetInt64(), page.GetProperty("items").GetArrayLength()).ShouldBe((1L, 1));
        (await http.GetAsync(new Uri("/api/books?sort=nope", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // Full replace: fields left out get their default.
        var replaced = await ExportedBackendSupport.Json(await http.PutAsJsonAsync(new Uri($"/api/books/{id}", UriKind.Relative),
            new { title = "Dune Messiah", copies = 5, author_id = ann, editor_id = bob }, Ct), HttpStatusCode.OK);
        replaced.GetProperty("price_usd").GetString().ShouldBe("7.50");

        // Bob edits a book (Restrict): he can't be deleted until the book is gone.
        (await http.DeleteAsync(new Uri($"/api/authors/{bob}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await http.DeleteAsync(new Uri($"/api/books/{id}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await http.GetAsync(new Uri($"/api/books/{id}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await http.DeleteAsync(new Uri($"/api/authors/{bob}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var openApi = await http.GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative), Ct);
        openApi.ShouldContain("/api/books");
        openApi.ShouldContain("bearer", Case.Insensitive);
        (await http.GetAsync(new Uri("/swagger/index.html", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private sealed record Shop(SignedIn Owner, ProjectDto Project);

    /// <summary>BookStore with every type, defaults of every kind, unique keys and all three on-delete rules (one a self-reference), applied.</summary>
    private async Task<Shop> BookStoreAsync()
    {
        var owner = await _driver.SignUpAsync();
        var project = await _driver.CreateProjectAsync(owner, "BookStore");
        var authors = await CreateTableAsync(project, owner, "authors",
            Column("name", DataType.Varchar, length: 120, nullable: false, unique: true),
            Column("bio", DataType.Text),
            Column("born_on", DataType.Date, defaultValue: "1970-01-01"));
        await _driver.OkAsync<TableDto>(HttpMethod.Post, $"/api/projects/{project.Id}/tables/{authors.Id}/columns", owner,
            new SaveColumnRequest(authors.Version, "mentor_id", DataType.BigInt, null, null, null, true, false, null, authors.Id, ReferenceAction.SetNull));
        await CreateTableAsync(project, owner, "books",
            Column("title", DataType.Varchar, length: 200, nullable: false, defaultValue: @"O'Reilly \ ""guide"""),
            Column("isbn", DataType.Varchar, length: 20, unique: true),
            Column("price_usd", DataType.Decimal, precision: 10, scale: 2, nullable: false, defaultValue: "7.50"),
            Column("pages", DataType.Int, defaultValue: "-1"),
            Column("copies", DataType.BigInt, nullable: false),
            Column("in_stock", DataType.Bool, nullable: false, defaultValue: "true"),
            Column("added_at", DataType.DateTime, nullable: false, defaultValue: "CURRENT_TIMESTAMP"),
            Column("public_id", DataType.Uuid, nullable: false, defaultValue: "UUID()"),
            Column("metadata", DataType.Json),
            Column("author_id", DataType.BigInt, nullable: false) with { ReferencesTableId = authors.Id, OnDelete = ReferenceAction.Cascade },
            Column("editor_id", DataType.BigInt) with { ReferencesTableId = authors.Id, OnDelete = ReferenceAction.Restrict });

        var plan = await _driver.OkAsync<SchemaPlanDto>(HttpMethod.Get, $"/api/projects/{project.Id}/schema/plan", owner);
        await _driver.OkAsync<ApplyResultDto>(HttpMethod.Post, $"/api/projects/{project.Id}/schema/apply", owner, new ApplyRequest(plan.PlanHash, false));
        return new Shop(owner, project);
    }

    private Task<TableDto> CreateTableAsync(ProjectDto project, SignedIn owner, string name, params ColumnRequest[] columns) =>
        _driver.OkAsync<TableDto>(HttpMethod.Post, $"/api/projects/{project.Id}/tables", owner, new CreateTableRequest(name, columns), HttpStatusCode.Created);

    private static ColumnRequest Column(
        string name, DataType type, int? length = null, int? precision = null, int? scale = null,
        bool nullable = true, bool unique = false, string? defaultValue = null) =>
        new(name, type, length, precision, scale, nullable, unique, defaultValue);
}
