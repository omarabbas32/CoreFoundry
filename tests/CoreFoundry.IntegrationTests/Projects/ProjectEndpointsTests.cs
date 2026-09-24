using System.Net;
using CoreFoundry.Api.Projects;
using CoreFoundry.Application.Projects;
using CoreFoundry.Domain.Projects;
using CoreFoundry.IntegrationTests.Infrastructure;
using Shouldly;
using static CoreFoundry.IntegrationTests.Infrastructure.ApiDriver;

namespace CoreFoundry.IntegrationTests.Projects;

[Collection(TestDatabaseGroup.Name)]
public sealed class ProjectEndpointsTests : IDisposable
{
    private readonly TestDatabaseApi _api;
    private readonly HttpClient _client;
    private readonly ApiDriver _driver;

    public ProjectEndpointsTests(TestDatabaseApi api)
    {
        _api = api;
        _api.SkipIfUnavailable();
        _client = api.CreateHttpsClient();
        _driver = new ApiDriver(_client);
    }

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task Create_provisions_a_real_database_in_the_test_id_range()
    {
        var owner = await _driver.SignUpAsync();

        var response = await _driver.SendAsync(HttpMethod.Post, "/api/projects", owner, new CreateProjectRequest("Book Shop"));

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
        var owner = await _driver.SignUpAsync();
        var created = await _driver.CreateProjectAsync(owner, "Timestamps");

        var reread = await ReadAsync<ProjectDto>(await _driver.SendAsync(HttpMethod.Get, $"/api/projects/{created.Id}", owner));

        reread.CreatedAt.ShouldBe(created.CreatedAt);
    }

    [Fact]
    public async Task Same_name_gets_a_suffixed_slug()
    {
        var owner = await _driver.SignUpAsync();
        var name = $"Shop {Guid.NewGuid():N}";

        var first = await _driver.CreateProjectAsync(owner, name);
        var second = await _driver.CreateProjectAsync(owner, name);

        second.Slug.ShouldBe($"{first.Slug}-2");
    }

    [Fact]
    public async Task List_shows_only_my_projects()
    {
        var me = await _driver.SignUpAsync();
        var someoneElse = await _driver.SignUpAsync();
        var mine = await _driver.CreateProjectAsync(me, "Mine");
        await _driver.CreateProjectAsync(someoneElse, "Theirs");

        var list = await ReadAsync<List<ProjectDto>>(await _driver.SendAsync(HttpMethod.Get, "/api/projects", me));

        list.ShouldHaveSingleItem().Id.ShouldBe(mine.Id);
    }

    [Fact]
    public async Task Non_members_get_404_exactly_like_a_missing_project()
    {
        var owner = await _driver.SignUpAsync();
        var outsider = await _driver.SignUpAsync();
        var project = await _driver.CreateProjectAsync(owner, "Private");

        var existing = await _driver.SendAsync(HttpMethod.Get, $"/api/projects/{project.Id}", outsider);
        var missing = await _driver.SendAsync(HttpMethod.Get, "/api/projects/999999999", outsider);

        existing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_developer_can_read_but_not_rename_or_delete()
    {
        var owner = await _driver.SignUpAsync();
        var developer = await _driver.SignUpAsync();
        var project = await _driver.CreateProjectAsync(owner, "Team");
        await _driver.AddMemberAsync(project.Id, owner, developer, ProjectRole.Developer);

        (await _driver.SendAsync(HttpMethod.Get, $"/api/projects/{project.Id}", developer)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _driver.SendAsync(HttpMethod.Patch, $"/api/projects/{project.Id}", developer, new RenameProjectRequest("Mine now")))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _driver.SendAsync(HttpMethod.Delete, $"/api/projects/{project.Id}", developer)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task An_admin_can_rename_but_not_delete()
    {
        var owner = await _driver.SignUpAsync();
        var admin = await _driver.SignUpAsync();
        var project = await _driver.CreateProjectAsync(owner, "Team");
        await _driver.AddMemberAsync(project.Id, owner, admin, ProjectRole.Admin);

        var renamed = await _driver.SendAsync(HttpMethod.Patch, $"/api/projects/{project.Id}", admin, new RenameProjectRequest("Renamed"));

        renamed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadAsync<ProjectDto>(renamed)).Name.ShouldBe("Renamed");
        (await _driver.SendAsync(HttpMethod.Delete, $"/api/projects/{project.Id}", admin)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Owner_delete_drops_the_database_and_the_project()
    {
        var owner = await _driver.SignUpAsync();
        var project = await _driver.CreateProjectAsync(owner, "Short lived");

        (await _driver.SendAsync(HttpMethod.Delete, $"/api/projects/{project.Id}", owner)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await _api.ProjectDatabaseExistsAsync(project.DatabaseName)).ShouldBeFalse();
        (await _driver.SendAsync(HttpMethod.Get, $"/api/projects/{project.Id}", owner)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Retry_provisioning_of_an_active_project_is_rejected()
    {
        var owner = await _driver.SignUpAsync();
        var project = await _driver.CreateProjectAsync(owner, "Healthy");

        (await _driver.SendAsync(HttpMethod.Post, $"/api/projects/{project.Id}/retry-provisioning", owner))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Blank_name_is_400()
    {
        var owner = await _driver.SignUpAsync();

        (await _driver.SendAsync(HttpMethod.Post, "/api/projects", owner, new CreateProjectRequest("  ")))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Projects_require_a_signed_in_user() =>
        (await _driver.SendAsync(HttpMethod.Get, "/api/projects", user: null)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
}
