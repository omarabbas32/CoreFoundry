using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using CoreFoundry.Api.Projects;
using CoreFoundry.Application.Projects;
using CoreFoundry.Application.Schema;
using CoreFoundry.Application.SchemaEngine;
using CoreFoundry.Domain.Schema;
using CoreFoundry.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Shouldly;

namespace CoreFoundry.IntegrationTests.Export;

/// <summary>
/// End to end for phase-9 (realtime in the exported backend): SignalR clients against the running exported API's
/// <c>/hubs/realtime</c>, with the access levels from <see cref="AccessEndpointsTests"/> (shared helpers in
/// <see cref="ExportedBackendSupport"/>).
/// </summary>
[Collection(TestDatabaseGroup.Name)]
public sealed class RealtimeEndpointsTests : IDisposable
{
    /// <summary>How long a client waits for an expected <c>change</c> event before the test fails.</summary>
    private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long a client waits to be sure that nothing was published.</summary>
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(1);

    private readonly TestDatabaseApi _api;
    private readonly HttpClient _client;
    private readonly ApiDriver _driver;

    public RealtimeEndpointsTests(TestDatabaseApi api)
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
    public async Task The_exported_backend_publishes_changes_to_the_subscribers_allowed_to_read_them()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("CF_SKIP_EXPORT_BUILD") == "1", "CF_SKIP_EXPORT_BUILD=1");
        var shop = await BookshopAsync();
        var zip = await (await _driver.SendAsync(HttpMethod.Get, $"/api/projects/{shop.Project.Id}/export", shop.Owner)).Content.ReadAsByteArrayAsync(Ct);
        await ExportedBackendSupport.RunExportedBackendAsync(
            zip, "Bookshop", _api.EngineConnectionString, (http, _) => UseRealtimeAsync(http), swagger: false);
    }

    /// <summary>
    /// Drives the hub with <c>books</c> (Read Public, Write Admin), <c>authors</c> (Read Admin, Write Admin) and
    /// <c>categories</c> (the Signed-in default): the checks from phase-9-realtime-export.md §4. Writes go through the
    /// REST API as the Admin.
    /// </summary>
    private static async Task UseRealtimeAsync(HttpClient http)
    {
        var adminToken = await RegisterAsync(http, "admin@example.com"); // the first account is Admin
        var memberToken = await RegisterAsync(http, "member@example.com");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        await using var admin = await ConnectAsync(http.BaseAddress!, adminToken);
        await using var anonymous = await ConnectAsync(http.BaseAddress!, null);
        await using var member = await ConnectAsync(http.BaseAddress!, memberToken);
        var adminChanges = new ChangeCollector(admin);
        var anonymousChanges = new ChangeCollector(anonymous);
        var memberChanges = new ChangeCollector(member);

        // The token in the URL is read for the hubs only: on the REST API it counts for nothing.
        using (var bare = new HttpClient { BaseAddress = http.BaseAddress })
        {
            (await bare.GetAsync(new Uri($"/api/authors?access_token={Uri.EscapeDataString(adminToken)}", UriKind.Relative), Ct))
                .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        // Anyone may subscribe to the public table; categories needs a sign-in; authors needs a sign-in, then the Admin
        // role; unknown tables are refused.
        await anonymous.InvokeAsync("Subscribe", "books", Ct);
        (await Should.ThrowAsync<HubException>(() => anonymous.InvokeAsync("Subscribe", "categories", Ct)))
            .Message.ShouldContain("Sign in to subscribe to categories.");
        await member.InvokeAsync("Subscribe", "categories", Ct);
        (await Should.ThrowAsync<HubException>(() => anonymous.InvokeAsync("Subscribe", "authors", Ct)))
            .Message.ShouldContain("Sign in to subscribe to authors.");
        (await Should.ThrowAsync<HubException>(() => member.InvokeAsync("Subscribe", "authors", Ct)))
            .Message.ShouldContain("Only admins can subscribe to authors.");
        (await Should.ThrowAsync<HubException>(() => anonymous.InvokeAsync("Subscribe", "nope", Ct)))
            .Message.ShouldContain("There is no table nope to subscribe to.");
        await admin.InvokeAsync("Subscribe", "books", Ct);
        await admin.InvokeAsync("Subscribe", "authors", Ct);

        // Insert, replace and delete each send one event with the table, the operation and the row's id.
        var dune = await CreateAsync(http, "/api/books", new { title = "Dune", isbn = "978-0441013593" });
        (await adminChanges.NextAsync()).ShouldBe(new Change("books", "insert", dune));
        (await anonymousChanges.NextAsync()).ShouldBe(new Change("books", "insert", dune));

        (await http.PutAsJsonAsync(new Uri($"/api/books/{dune}", UriKind.Relative), new { title = "Dune Messiah", isbn = "978-0441013593" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await adminChanges.NextAsync()).ShouldBe(new Change("books", "update", dune));
        (await anonymousChanges.NextAsync()).ShouldBe(new Change("books", "update", dune));

        var herbert = await CreateAsync(http, "/api/authors", new { name = "Frank Herbert" });
        (await adminChanges.NextAsync()).ShouldBe(new Change("authors", "insert", herbert));

        // A signed-in User subscribed to the Signed-in table gets its changes.
        var scienceFiction = await CreateAsync(http, "/api/categories", new { name = "Science fiction" });
        (await memberChanges.NextAsync()).ShouldBe(new Change("categories", "insert", scienceFiction));

        (await http.DeleteAsync(new Uri($"/api/books/{dune}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await adminChanges.NextAsync()).ShouldBe(new Change("books", "delete", dune));
        (await anonymousChanges.NextAsync()).ShouldBe(new Change("books", "delete", dune));

        // A save that fails (a duplicate unique isbn, 409) publishes nothing.
        var emma = await CreateAsync(http, "/api/books", new { title = "Emma", isbn = "978-0141439587" });
        (await adminChanges.NextAsync()).ShouldBe(new Change("books", "insert", emma));
        (await anonymousChanges.NextAsync()).ShouldBe(new Change("books", "insert", emma));
        (await ExportedBackendSupport.PostAsync(http, "/api/books", new { title = "Emma again", isbn = "978-0141439587" }))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await Task.Delay(QuietPeriod, Ct);
        adminChanges.ShouldBeEmpty(); // nor did the categories insert reach the Admin, who didn't subscribe to it
        anonymousChanges.ShouldBeEmpty(); // nor did the authors or categories inserts reach a client subscribed to books only
        memberChanges.ShouldBeEmpty(); // nor did the books changes reach a client subscribed to categories only

        // Reconnect: the restarted connection is a new one, and the server has forgotten the old one's subscriptions, so a
        // write before re-subscribing doesn't reach it (the anonymous client's copy shows it was published). Once
        // re-subscribed, the next write arrives.
        await admin.StopAsync(Ct);
        await admin.StartAsync(Ct);
        var persuasion = await CreateAsync(http, "/api/books", new { title = "Persuasion", isbn = "978-0141439686" });
        (await anonymousChanges.NextAsync()).ShouldBe(new Change("books", "insert", persuasion));
        await Task.Delay(QuietPeriod, Ct);
        adminChanges.ShouldBeEmpty();
        await admin.InvokeAsync("Subscribe", "books", Ct);
        (await http.DeleteAsync(new Uri($"/api/books/{emma}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await adminChanges.NextAsync()).ShouldBe(new Change("books", "delete", emma));
    }

    /// <summary>
    /// A started connection to the hub over WebSockets. A token goes in the URL as <c>?access_token=</c>, the way browsers
    /// send it: outside a browser the .NET client would send an <c>AccessTokenProvider</c> token as a header instead.
    /// </summary>
    private static async Task<HubConnection> ConnectAsync(Uri baseAddress, string? accessToken)
    {
        var url = accessToken is null
            ? new Uri(baseAddress, "/hubs/realtime")
            : new Uri(baseAddress, $"/hubs/realtime?access_token={Uri.EscapeDataString(accessToken)}");
        var connection = new HubConnectionBuilder().WithUrl(url, HttpTransportType.WebSockets).Build();
        await connection.StartAsync(Ct);
        return connection;
    }

    private static async Task<long> CreateAsync(HttpClient http, string path, object body) =>
        (await ExportedBackendSupport.Json(await ExportedBackendSupport.PostAsync(http, path, body), HttpStatusCode.Created)).GetProperty("id").GetInt64();

    private static async Task<string> RegisterAsync(HttpClient http, string email)
    {
        var body = await ExportedBackendSupport.Json(
            await ExportedBackendSupport.PostAsync(http, "/api/auth/register", new { email, password = "a long password" }), HttpStatusCode.Created);
        return body.GetProperty("accessToken").GetString()!;
    }

    /// <summary>A <c>change</c> event as sent: <c>{ table, operation, id }</c>.</summary>
    private sealed record Change(string Table, string Operation, long Id);

    /// <summary>Collects one connection's <c>change</c> events; waiting for one fails after <see cref="EventTimeout"/>.</summary>
    private sealed class ChangeCollector
    {
        private readonly Channel<JsonElement> _events = Channel.CreateUnbounded<JsonElement>();

        public ChangeCollector(HubConnection connection) =>
            connection.On<JsonElement>("change", payload => _events.Writer.TryWrite(payload.Clone()));

        public async Task<Change> NextAsync()
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            timeout.CancelAfter(EventTimeout);
            JsonElement payload;
            try
            {
                payload = await _events.Reader.ReadAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
            {
                throw new ShouldAssertException($"No change event arrived within {EventTimeout.TotalSeconds} seconds.");
            }

            return new Change(payload.GetProperty("table").GetString()!, payload.GetProperty("operation").GetString()!, payload.GetProperty("id").GetInt64());
        }

        public void ShouldBeEmpty()
        {
            if (_events.Reader.TryRead(out var unexpected))
            {
                throw new ShouldAssertException($"Expected no change event, got {unexpected.GetRawText()}.");
            }
        }
    }

    private sealed record Shop(SignedIn Owner, ProjectDto Project);

    /// <summary>
    /// A project with <c>books</c> (Read Public, Write Admin; a unique <c>isbn</c>), <c>authors</c> (Read Admin,
    /// Write Admin) and <c>categories</c> (left at Signed-in for both), applied.
    /// </summary>
    private async Task<Shop> BookshopAsync()
    {
        var owner = await _driver.SignUpAsync();
        var project = await _driver.CreateProjectAsync(owner, "Bookshop");
        var books = await CreateTableAsync(project, owner, "books",
            new ColumnRequest("title", DataType.Text, null, null, null, true, false, null),
            new ColumnRequest("isbn", DataType.Varchar, 20, null, null, true, true, null));
        var authors = await CreateTableAsync(project, owner, "authors", new ColumnRequest("name", DataType.Text, null, null, null, true, false, null));
        await CreateTableAsync(project, owner, "categories", new ColumnRequest("name", DataType.Text, null, null, null, true, false, null));
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
}
