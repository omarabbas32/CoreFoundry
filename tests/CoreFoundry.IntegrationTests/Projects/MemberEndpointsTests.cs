using System.Net;
using CoreFoundry.Api.Projects;
using CoreFoundry.Application.Projects;
using CoreFoundry.Domain.Projects;
using CoreFoundry.IntegrationTests.Infrastructure;
using Shouldly;
using static CoreFoundry.IntegrationTests.Infrastructure.ApiDriver;

namespace CoreFoundry.IntegrationTests.Projects;

[Collection(TestDatabaseGroup.Name)]
public sealed class MemberEndpointsTests : IDisposable
{
    private readonly HttpClient _client;
    private readonly ApiDriver _driver;

    public MemberEndpointsTests(TestDatabaseApi api)
    {
        api.SkipIfUnavailable();
        _client = api.CreateHttpsClient();
        _driver = new ApiDriver(_client);
    }

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task Owner_adds_members_and_everyone_can_list_them()
    {
        var (owner, project) = await OwnedProjectAsync();
        var developer = await _driver.SignUpAsync();
        await _driver.AddMemberAsync(project.Id, owner, developer, ProjectRole.Developer);

        var list = await ReadAsync<List<MemberDto>>(await _driver.SendAsync(HttpMethod.Get, Members(project), developer));

        list.Select(member => (member.Email, member.Role)).ShouldBe(
            [(owner.Email, ProjectRole.Owner), (developer.Email, ProjectRole.Developer)]);
    }

    [Fact]
    public async Task A_new_member_sees_the_project_in_their_list_with_their_role()
    {
        var (owner, project) = await OwnedProjectAsync();
        var admin = await _driver.SignUpAsync();
        await _driver.AddMemberAsync(project.Id, owner, admin, ProjectRole.Admin);

        var list = await ReadAsync<List<ProjectDto>>(await _driver.SendAsync(HttpMethod.Get, "/api/projects", admin));

        list.ShouldHaveSingleItem().Role.ShouldBe(ProjectRole.Admin);
    }

    [Fact]
    public async Task Adding_unknown_email_is_404_duplicate_is_409_and_owner_role_is_400()
    {
        var (owner, project) = await OwnedProjectAsync();
        var developer = await _driver.SignUpAsync();
        await _driver.AddMemberAsync(project.Id, owner, developer, ProjectRole.Developer);

        (await Add(project, owner, "nobody@test.dev", ProjectRole.Developer)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Add(project, owner, developer.Email, ProjectRole.Admin)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await Add(project, owner, (await _driver.SignUpAsync()).Email, ProjectRole.Owner)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_developer_cannot_add_members()
    {
        var (owner, project) = await OwnedProjectAsync();
        var developer = await _driver.SignUpAsync();
        await _driver.AddMemberAsync(project.Id, owner, developer, ProjectRole.Developer);

        (await Add(project, developer, (await _driver.SignUpAsync()).Email, ProjectRole.Developer))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task An_admin_changes_roles_but_cannot_touch_the_owner()
    {
        var (owner, project) = await OwnedProjectAsync();
        var admin = await _driver.SignUpAsync();
        var developer = await _driver.SignUpAsync();
        await _driver.AddMemberAsync(project.Id, owner, admin, ProjectRole.Admin);
        await _driver.AddMemberAsync(project.Id, owner, developer, ProjectRole.Developer);

        var promoted = await _driver.SendAsync(
            HttpMethod.Put, Member(project, developer), admin, new ChangeMemberRoleRequest(ProjectRole.Admin));
        promoted.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadAsync<MemberDto>(promoted)).Role.ShouldBe(ProjectRole.Admin);

        (await _driver.SendAsync(HttpMethod.Put, Member(project, owner), admin, new ChangeMemberRoleRequest(ProjectRole.Developer)))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _driver.SendAsync(HttpMethod.Delete, Member(project, owner), admin)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_developer_can_leave_but_not_remove_others()
    {
        var (owner, project) = await OwnedProjectAsync();
        var alice = await _driver.SignUpAsync();
        var bob = await _driver.SignUpAsync();
        await _driver.AddMemberAsync(project.Id, owner, alice, ProjectRole.Developer);
        await _driver.AddMemberAsync(project.Id, owner, bob, ProjectRole.Developer);

        (await _driver.SendAsync(HttpMethod.Delete, Member(project, bob), alice)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _driver.SendAsync(HttpMethod.Delete, Member(project, alice), alice)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Alice left: the project is now invisible to her.
        (await _driver.SendAsync(HttpMethod.Get, $"/api/projects/{project.Id}", alice)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Transfer_ownership_swaps_roles_and_moves_owner_only_powers()
    {
        var (owner, project) = await OwnedProjectAsync();
        var successor = await _driver.SignUpAsync();
        await _driver.AddMemberAsync(project.Id, owner, successor, ProjectRole.Developer);

        var transferred = await _driver.SendAsync(
            HttpMethod.Post, $"/api/projects/{project.Id}/transfer-ownership", owner, new TransferOwnershipRequest(successor.UserId));

        transferred.StatusCode.ShouldBe(HttpStatusCode.OK);
        var members = await ReadAsync<List<MemberDto>>(transferred);
        members.Single(member => member.Role == ProjectRole.Owner).UserId.ShouldBe(successor.UserId);
        members.Single(member => member.UserId == owner.UserId).Role.ShouldBe(ProjectRole.Admin);

        // The previous owner can no longer delete; the new one can.
        (await _driver.SendAsync(HttpMethod.Delete, $"/api/projects/{project.Id}", owner)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _driver.SendAsync(HttpMethod.Delete, $"/api/projects/{project.Id}", successor)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Only_the_owner_can_transfer_ownership()
    {
        var (owner, project) = await OwnedProjectAsync();
        var admin = await _driver.SignUpAsync();
        await _driver.AddMemberAsync(project.Id, owner, admin, ProjectRole.Admin);

        (await _driver.SendAsync(
                HttpMethod.Post, $"/api/projects/{project.Id}/transfer-ownership", admin, new TransferOwnershipRequest(admin.UserId)))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Removing_a_member_revokes_access_immediately_without_a_new_token()
    {
        var (owner, project) = await OwnedProjectAsync();
        var developer = await _driver.SignUpAsync();
        await _driver.AddMemberAsync(project.Id, owner, developer, ProjectRole.Developer);
        (await _driver.SendAsync(HttpMethod.Get, Members(project), developer)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await _driver.SendAsync(HttpMethod.Delete, Member(project, developer), owner)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Same access token as before: roles are read per request, not baked into the JWT.
        (await _driver.SendAsync(HttpMethod.Get, Members(project), developer)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private async Task<(SignedIn Owner, ProjectDto Project)> OwnedProjectAsync()
    {
        var owner = await _driver.SignUpAsync();
        return (owner, await _driver.CreateProjectAsync(owner, "Team"));
    }

    private Task<HttpResponseMessage> Add(ProjectDto project, SignedIn caller, string email, ProjectRole role) =>
        _driver.SendAsync(HttpMethod.Post, Members(project), caller, new AddMemberRequest(email, role));

    private static string Members(ProjectDto project) => $"/api/projects/{project.Id}/members";

    private static string Member(ProjectDto project, SignedIn user) => $"/api/projects/{project.Id}/members/{user.UserId}";
}
