using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CoreFoundry.Api.Projects;
using CoreFoundry.Application.Projects;
using CoreFoundry.Application.Schema;
using CoreFoundry.Application.SchemaEngine;
using CoreFoundry.Domain.Schema;
using CoreFoundry.IntegrationTests.Infrastructure;
using Shouldly;

namespace CoreFoundry.IntegrationTests.Export;

/// <summary>
/// End to end for phase-8 (access rules): a project's per-table Read/Write levels and the exported
/// backend's roles, built and run the same way as <see cref="ExportEndpointsTests"/> (shared helpers
/// in <see cref="ExportedBackendSupport"/>).
/// </summary>
[Collection(TestDatabaseGroup.Name)]
public sealed class AccessEndpointsTests : IDisposable
{
    private readonly TestDatabaseApi _api;
    private readonly HttpClient _client;
    private readonly ApiDriver _driver;

    public AccessEndpointsTests(TestDatabaseApi api)
    {
        api.SkipIfUnavailable();
        _api = api;
        _client = api.CreateHttpsClient();
        _driver = new ApiDriver(_client);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _client.Dispose();

    /// <summary>Slow (builds and runs the exported backend). Set CF_SKIP_EXPORT_BUILD=1 to skip it, as for <see cref="ExportEndpointsTests"/>.</summary>
    [Fact]
    public async Task The_exported_backend_enforces_access_levels_and_roles()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("CF_SKIP_EXPORT_BUILD") == "1", "CF_SKIP_EXPORT_BUILD=1");
        var shop = await LibraryAsync();
        var zip = await (await _driver.SendAsync(HttpMethod.Get, $"/api/projects/{shop.Project.Id}/export", shop.Owner)).Content.ReadAsByteArrayAsync(Ct);
        await ExportedBackendSupport.RunExportedBackendAsync(zip, "Library", _api.EngineConnectionString, (http, _) => UseAccessApiAsync(http));
    }

    /// <summary>
    /// Drives the exported API with <c>books</c> (Read Public, Write Admin) and <c>authors</c> (Read Admin,
    /// Write Admin): the checks from phase-8-access-rules.md §4, plus simultaneous first sign-ups, roles, the last-Admin
    /// guard and the OpenAPI document's per-operation lock.
    /// </summary>
    private static async Task UseAccessApiAsync(HttpClient http)
    {
        // No token: books can be read by anyone but not written; authors need a token even to read.
        (await http.GetAsync(new Uri("/api/books", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ExportedBackendSupport.PostAsync(http, "/api/books", new { title = "Dune" })).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await http.GetAsync(new Uri("/api/authors", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // Simultaneous first sign-ups: all succeed, and exactly one of them becomes Admin.
        var emails = Enumerable.Range(1, 5).Select(i => $"first{i}@example.com").ToList();
        var tokens = await Task.WhenAll(emails.Select(email => RegisterAsync(http, email)));
        var admins = tokens.Select((token, i) => (Token: token, Email: emails[i])).Where(account => RoleOf(account.Token) == "Admin").ToList();
        admins.Count.ShouldBe(1, string.Join(", ", tokens.Select(RoleOf)));
        tokens.Select(RoleOf).Count(role => role == "User").ShouldBe(4);
        var (adminToken, adminEmail) = admins[0];
        Authorize(http, adminToken);
        var accounts = await ExportedBackendSupport.Json(await http.GetAsync(new Uri("/api/auth/users", UriKind.Relative), Ct), HttpStatusCode.OK);
        accounts.EnumerateArray().Where(account => account.GetProperty("role").GetString() == "Admin")
            .Select(account => account.GetProperty("email").GetString()).ShouldBe([adminEmail]);

        // The Admin reads and writes both tables.
        (await http.GetAsync(new Uri("/api/books", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ExportedBackendSupport.PostAsync(http, "/api/books", new { title = "Dune" })).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await http.GetAsync(new Uri("/api/authors", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ExportedBackendSupport.PostAsync(http, "/api/authors", new { name = "Frank Herbert" })).StatusCode.ShouldBe(HttpStatusCode.Created);

        // The only Admin can't demote themselves.
        var adminId = await AccountIdAsync(http, adminEmail);
        (await http.PutAsJsonAsync(new Uri($"/api/auth/users/{adminId}/role", UriKind.Relative), new { role = "User" }, Ct)).StatusCode
            .ShouldBe(HttpStatusCode.Conflict);

        // A plain User: reads the public table, but writing books or reading/writing authors (both Admin) is forbidden.
        var memberToken = await RegisterAsync(http, "member@example.com");
        Authorize(http, memberToken);
        (await http.GetAsync(new Uri("/api/books", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ExportedBackendSupport.PostAsync(http, "/api/books", new { title = "Dune Messiah" })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await http.GetAsync(new Uri("/api/authors", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await http.GetAsync(new Uri("/api/auth/users", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // The Admin promotes the member; the token already issued keeps its old role, so they log in again.
        Authorize(http, adminToken);
        var memberId = await AccountIdAsync(http, "member@example.com");
        var promoted = await ExportedBackendSupport.Json(
            await http.PutAsJsonAsync(new Uri($"/api/auth/users/{memberId}/role", UriKind.Relative), new { role = "Admin" }, Ct), HttpStatusCode.OK);
        promoted.GetProperty("role").GetString().ShouldBe("Admin");

        var promotedToken = await LoginAsync(http, "member@example.com");
        Authorize(http, promotedToken);
        (await ExportedBackendSupport.PostAsync(http, "/api/books", new { title = "Dune Messiah" })).StatusCode.ShouldBe(HttpStatusCode.Created);

        // The OpenAPI document is public, whether or not Swagger UI is on, and locks only the operations that need a
        // token: reading books (Public) and registering are open; writing books and reading authors need the Bearer token.
        Authorize(http, null);
        var openApi = await ExportedBackendSupport.Json(await http.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative), Ct), HttpStatusCode.OK);
        var paths = openApi.GetProperty("paths");
        paths.GetProperty("/api/books").GetProperty("get").TryGetProperty("security", out _).ShouldBeFalse();
        paths.GetProperty("/api/books").GetProperty("post").GetProperty("security")[0].TryGetProperty("Bearer", out _).ShouldBeTrue();
        paths.GetProperty("/api/authors").GetProperty("get").TryGetProperty("security", out _).ShouldBeTrue();
        paths.GetProperty("/api/auth/register").GetProperty("post").TryGetProperty("security", out _).ShouldBeFalse();
    }

    private static async Task<string> RegisterAsync(HttpClient http, string email)
    {
        var body = await ExportedBackendSupport.Json(
            await ExportedBackendSupport.PostAsync(http, "/api/auth/register", new { email, password = "a long password" }), HttpStatusCode.Created);
        return body.GetProperty("accessToken").GetString()!;
    }

    private static async Task<string> LoginAsync(HttpClient http, string email)
    {
        var body = await ExportedBackendSupport.Json(
            await ExportedBackendSupport.PostAsync(http, "/api/auth/login", new { email, password = "a long password" }), HttpStatusCode.OK);
        return body.GetProperty("accessToken").GetString()!;
    }

    private static async Task<long> AccountIdAsync(HttpClient http, string email)
    {
        var accounts = await ExportedBackendSupport.Json(await http.GetAsync(new Uri("/api/auth/users", UriKind.Relative), Ct), HttpStatusCode.OK);
        return accounts.EnumerateArray().Single(account => account.GetProperty("email").GetString() == email).GetProperty("id").GetInt64();
    }

    /// <summary>The <c>role</c> claim of an access token (its payload is base64url JSON; the signature isn't checked here).</summary>
    private static string? RoleOf(string accessToken)
    {
        using var document = JsonDocument.Parse(Base64Url.DecodeFromChars(accessToken.Split('.')[1]));
        return document.RootElement.GetProperty("role").GetString();
    }

    private static void Authorize(HttpClient http, string? accessToken) =>
        http.DefaultRequestHeaders.Authorization = accessToken is null ? null : new AuthenticationHeaderValue("Bearer", accessToken);

    private sealed record Shop(SignedIn Owner, ProjectDto Project);

    /// <summary>A project with <c>books</c> (Read Public, Write Admin) and <c>authors</c> (Read Admin, Write Admin), applied.</summary>
    private async Task<Shop> LibraryAsync()
    {
        var owner = await _driver.SignUpAsync();
        var project = await _driver.CreateProjectAsync(owner, "Library");
        var books = await CreateTableAsync(project, owner, "books", Column("title"));
        var authors = await CreateTableAsync(project, owner, "authors", Column("name"));
        await SetAccessAsync(project, owner, books, AccessLevel.Public, AccessLevel.Admin);
        await SetAccessAsync(project, owner, authors, AccessLevel.Admin, AccessLevel.Admin);

        var plan = await _driver.OkAsync<SchemaPlanDto>(HttpMethod.Get, $"/api/projects/{project.Id}/schema/plan", owner);
        await _driver.OkAsync<ApplyResultDto>(HttpMethod.Post, $"/api/projects/{project.Id}/schema/apply", owner, new ApplyRequest(plan.PlanHash, false));
        return new Shop(owner, project);
    }

    private Task<TableDto> CreateTableAsync(ProjectDto project, SignedIn owner, string name, params ColumnRequest[] columns) =>
        _driver.OkAsync<TableDto>(HttpMethod.Post, $"/api/projects/{project.Id}/tables", owner, new CreateTableRequest(name, columns), HttpStatusCode.Created);

    private Task<TableDto> SetAccessAsync(ProjectDto project, SignedIn owner, TableDto table, AccessLevel read, AccessLevel write) =>
        _driver.OkAsync<TableDto>(HttpMethod.Put, $"/api/projects/{project.Id}/tables/{table.Id}/access", owner, new TableAccessRequest(table.Version, read, write));

    private static ColumnRequest Column(string name) => new(name, DataType.Text, null, null, null, true, false, null);
}
