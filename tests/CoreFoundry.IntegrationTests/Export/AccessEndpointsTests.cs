using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
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
        var folder = Directory.CreateTempSubdirectory("cf-access-");
        var database = $"cf_p_{1_900_000_000L + Random.Shared.NextInt64(99_999_999)}";
        Process? api = null;
        try
        {
            await ZipFile.ExtractToDirectoryAsync(new MemoryStream(zip), folder.FullName, Ct);
            var root = Path.Combine(folder.FullName, "library-backend");

            // It builds, without warnings, and matches its migration.
            var build = await ExportedBackendSupport.RunAsync("dotnet", "build Library.slnx -c Release -nologo", root, TimeSpan.FromMinutes(6));
            build.ExitCode.ShouldBe(0, build.Output);
            build.Output.ShouldContain(" 0 Warning(s)", Case.Sensitive, build.Output);
            if (ExportedBackendSupport.FindOnPath("dotnet-ef") is { } ef)
            {
                var check = await ExportedBackendSupport.RunAsync(ef,
                    "migrations has-pending-model-changes --project src/Library.Infrastructure --startup-project src/Library.Api --no-build --configuration Release",
                    root, TimeSpan.FromMinutes(3));
                check.ExitCode.ShouldBe(0, check.Output);
                check.Output.ShouldContain("No changes have been made to the model since the last migration.");
            }

            var port = ExportedBackendSupport.FreePort();
            var log = new StringBuilder();
            api = ExportedBackendSupport.Start(log, Path.Combine(root, "src/Library.Api/bin/Release/net10.0/Library.Api.dll"), new Dictionary<string, string>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Production",
                ["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}",
                ["ConnectionStrings__Default"] = $"{_api.EngineConnectionString.TrimEnd(';')};Database={database}",
                ["Jwt__SigningKey"] = "an-access-test-signing-key-of-sufficient-length",
                ["Database__MigrateOnStartup"] = "true",
                ["Swagger__Enabled"] = "true",
            });
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            try
            {
                await ExportedBackendSupport.WaitUntilHealthyAsync(http, api);
                await UseAccessApiAsync(http);
            }
            catch (ShouldAssertException ex)
            {
                string output;
                lock (log)
                {
                    output = log.ToString();
                }

                throw new ShouldAssertException($"{ex.Message}\n--- exported API log ---\n{output}", ex);
            }
        }
        finally
        {
            if (api is { HasExited: false })
            {
                api.Kill(entireProcessTree: true);
            }

            api?.Dispose();
            await ExportedBackendSupport.ExecuteAsync(_api.EngineConnectionString, $"DROP DATABASE IF EXISTS `{database}`");
            try
            {
                folder.Delete(recursive: true);
            }
            catch (IOException)
            {
                // A build server may still hold a file; the temp folder is cleaned up by the OS.
            }
        }
    }

    /// <summary>
    /// Drives the exported API with <c>books</c> (Read Public, Write Admin) and <c>authors</c> (Read Admin,
    /// Write Admin): the checks from phase-8-access-rules.md §4, plus roles and the last-Admin guard.
    /// </summary>
    private static async Task UseAccessApiAsync(HttpClient http)
    {
        // No token: books can be read by anyone but not written; authors need a token even to read.
        (await http.GetAsync(new Uri("/api/books", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ExportedBackendSupport.PostAsync(http, "/api/books", new { title = "Dune" })).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await http.GetAsync(new Uri("/api/authors", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // The first registered account is Admin: reads and writes on both tables work.
        var adminToken = await RegisterAsync(http, "admin@example.com");
        Authorize(http, adminToken);
        (await http.GetAsync(new Uri("/api/books", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ExportedBackendSupport.PostAsync(http, "/api/books", new { title = "Dune" })).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await http.GetAsync(new Uri("/api/authors", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ExportedBackendSupport.PostAsync(http, "/api/authors", new { name = "Frank Herbert" })).StatusCode.ShouldBe(HttpStatusCode.Created);

        // The only Admin can't demote themselves.
        var adminId = await AccountIdAsync(http, "admin@example.com");
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

        // The OpenAPI document is public, whether or not Swagger UI is on.
        Authorize(http, null);
        var openApi = await http.GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative), Ct);
        openApi.ShouldContain("/api/books");
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
