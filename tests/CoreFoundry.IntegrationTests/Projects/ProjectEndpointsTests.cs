using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CoreFoundry.Api.Auth;
using CoreFoundry.Api.Projects;
using CoreFoundry.Application.Projects;
using CoreFoundry.Domain.Projects;
using CoreFoundry.IntegrationTests.Infrastructure;
using Shouldly;

namespace CoreFoundry.IntegrationTests.Projects;

[Collection(TestDatabaseGroup.Name)]
public sealed class ProjectEndpointsTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly TestDatabaseApi _api;
    private readonly HttpClient _client;

    public ProjectEndpointsTests(TestDatabaseApi api)
    {
        _api = api;
        _api.SkipIfUnavailable();
        _client = api.CreateHttpsClient();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task Create_provisions_a_real_database_in_the_test_id_range()
    {
        var owner = await SignUpAsync();

        var response = await SendAsync(HttpMethod.Post, "/api/projects", owner, new CreateProjectRequest("Book Shop"));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var project = await ReadAsync<ProjectDto>(response);
        project.Status.ShouldBe(ProjectStatus.Active);
        project.Role.ShouldBe(ProjectRole.Owner);
        project.Id.ShouldBeGreaterThanOrEqualTo(TestDatabaseApi.FirstProjectId);
        project.DatabaseName.ShouldBe($"cf_p_{project.Id}");
        (await _api.ProjectDatabaseExistsAsync(project.DatabaseName)).ShouldBeTrue();
        response.Headers.Location!.ToString().ShouldEndWith($"/api/projects/{project.Id}");
    }

    [Fact]
    public async Task Created_at_in_the_create_response_matches_what_is_stored()
    {
        var owner = await SignUpAsync();
        var created = await CreateAsync(owner, "Timestamps");

        var reread = await ReadAsync<ProjectDto>(await SendAsync(HttpMethod.Get, $"/api/projects/{created.Id}", owner));

        reread.CreatedAt.ShouldBe(created.CreatedAt);
    }

    [Fact]
    public async Task Same_name_gets_a_suffixed_slug()
    {
        var owner = await SignUpAsync();
        var name = $"Shop {Guid.NewGuid():N}";

        var first = await CreateAsync(owner, name);
        var second = await CreateAsync(owner, name);

        second.Slug.ShouldBe($"{first.Slug}-2");
    }

    [Fact]
    public async Task List_shows_only_my_projects()
    {
        var me = await SignUpAsync();
        var someoneElse = await SignUpAsync();
        var mine = await CreateAsync(me, "Mine");
        await CreateAsync(someoneElse, "Theirs");

        var list = await ReadAsync<List<ProjectDto>>(await SendAsync(HttpMethod.Get, "/api/projects", me));

        list.ShouldHaveSingleItem().Id.ShouldBe(mine.Id);
    }

    [Fact]
    public async Task Non_members_get_404_exactly_like_a_missing_project()
    {
        var owner = await SignUpAsync();
        var outsider = await SignUpAsync();
        var project = await CreateAsync(owner, "Private");

        var existing = await SendAsync(HttpMethod.Get, $"/api/projects/{project.Id}", outsider);
        var missing = await SendAsync(HttpMethod.Get, "/api/projects/999999999", outsider);

        existing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_developer_can_read_but_not_rename_or_delete()
    {
        var owner = await SignUpAsync();
        var developer = await SignUpAsync();
        var project = await CreateAsync(owner, "Team");
        await AddMemberAsync(project.Id, developer.UserId, ProjectRole.Developer);

        (await SendAsync(HttpMethod.Get, $"/api/projects/{project.Id}", developer)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SendAsync(HttpMethod.Patch, $"/api/projects/{project.Id}", developer, new RenameProjectRequest("Mine now")))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Delete, $"/api/projects/{project.Id}", developer)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task An_admin_can_rename_but_not_delete()
    {
        var owner = await SignUpAsync();
        var admin = await SignUpAsync();
        var project = await CreateAsync(owner, "Team");
        await AddMemberAsync(project.Id, admin.UserId, ProjectRole.Admin);

        var renamed = await SendAsync(HttpMethod.Patch, $"/api/projects/{project.Id}", admin, new RenameProjectRequest("Renamed"));

        renamed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadAsync<ProjectDto>(renamed)).Name.ShouldBe("Renamed");
        (await SendAsync(HttpMethod.Delete, $"/api/projects/{project.Id}", admin)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Owner_delete_drops_the_database_and_the_project()
    {
        var owner = await SignUpAsync();
        var project = await CreateAsync(owner, "Short lived");

        (await SendAsync(HttpMethod.Delete, $"/api/projects/{project.Id}", owner)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await _api.ProjectDatabaseExistsAsync(project.DatabaseName)).ShouldBeFalse();
        (await SendAsync(HttpMethod.Get, $"/api/projects/{project.Id}", owner)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Retry_provisioning_of_an_active_project_is_rejected()
    {
        var owner = await SignUpAsync();
        var project = await CreateAsync(owner, "Healthy");

        (await SendAsync(HttpMethod.Post, $"/api/projects/{project.Id}/retry-provisioning", owner))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Blank_name_is_400()
    {
        var owner = await SignUpAsync();

        (await SendAsync(HttpMethod.Post, "/api/projects", owner, new CreateProjectRequest("  ")))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Projects_require_a_signed_in_user() =>
        (await _client.GetAsync(new Uri("/api/projects", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

    private sealed record SignedIn(long UserId, string AccessToken);

    private async Task<SignedIn> SignUpAsync()
    {
        var response = await _client.PostAsJsonAsync(
            new Uri("/api/auth/register", UriKind.Relative),
            new RegisterRequest($"user-{Guid.NewGuid():N}@test.dev", "correct horse battery"),
            Ct);
        response.EnsureSuccessStatusCode();
        var body = await ReadAsync<AuthResponse>(response);
        return new SignedIn(body.User.Id, body.AccessToken);
    }

    private async Task<ProjectDto> CreateAsync(SignedIn owner, string name)
    {
        var response = await SendAsync(HttpMethod.Post, "/api/projects", owner, new CreateProjectRequest(name));
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return await ReadAsync<ProjectDto>(response);
    }

    /// <summary>Until the members API exists, memberships are inserted directly.</summary>
    private Task AddMemberAsync(long projectId, long userId, ProjectRole role) =>
        _api.ExecuteMetadataSqlAsync(
            $"INSERT INTO ProjectMembers (ProjectId, UserId, Role, CreatedAt) VALUES ({projectId}, {userId}, {(byte)role}, UTC_TIMESTAMP(6))");

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, SignedIn user, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", user.AccessToken);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType(), options: Json);
        }

        return await _client.SendAsync(request, Ct);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>(Json, Ct))!;
}
