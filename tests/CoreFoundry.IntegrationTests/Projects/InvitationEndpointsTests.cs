using System.Net;
using CoreFoundry.Api.Projects;
using CoreFoundry.Application.Projects;
using CoreFoundry.Domain.Projects;
using CoreFoundry.IntegrationTests.Infrastructure;
using Shouldly;

namespace CoreFoundry.IntegrationTests.Projects;

/// <summary>Joining a project by invitation: the admin invites, the invited user accepts or declines.</summary>
[Collection(TestDatabaseGroup.Name)]
public sealed class InvitationEndpointsTests : IDisposable
{
    private readonly HttpClient _client;
    private readonly ApiDriver _driver;

    public InvitationEndpointsTests(TestDatabaseApi api)
    {
        api.SkipIfUnavailable();
        _client = api.CreateHttpsClient();
        _driver = new ApiDriver(_client);
    }

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task An_invited_user_is_not_a_member_until_they_accept_from_their_notifications()
    {
        var (owner, project) = await OwnedProjectAsync("Shop");
        var invitee = await _driver.SignUpAsync();

        var invitation = await InviteAsync(project, owner, invitee, ProjectRole.Developer);
        (invitation.ProjectName, invitation.Email, invitation.Role, invitation.InvitedByEmail)
            .ShouldBe(("Shop", invitee.Email, ProjectRole.Developer, owner.Email));

        // Not a member yet: the project is invisible to them.
        (await _driver.SendAsync(HttpMethod.Get, $"/api/projects/{project.Id}", invitee)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _driver.OkAsync<List<ProjectDto>>(HttpMethod.Get, "/api/projects", invitee)).ShouldBeEmpty();

        // It's in their notifications, and the project's members can see it's pending.
        (await Mine(invitee)).ShouldHaveSingleItem().Id.ShouldBe(invitation.Id);
        (await _driver.OkAsync<List<InvitationDto>>(HttpMethod.Get, Invitations(project), owner)).ShouldHaveSingleItem().Email.ShouldBe(invitee.Email);

        var joined = await _driver.OkAsync<ProjectDto>(HttpMethod.Post, $"/api/me/invitations/{invitation.Id}/accept", invitee);

        (joined.Id, joined.Role).ShouldBe((project.Id, ProjectRole.Developer));
        (await _driver.OkAsync<List<ProjectDto>>(HttpMethod.Get, "/api/projects", invitee)).ShouldHaveSingleItem().Id.ShouldBe(project.Id);
        (await _driver.OkAsync<List<MemberDto>>(HttpMethod.Get, $"/api/projects/{project.Id}/members", owner))
            .Select(member => member.Email).ShouldContain(invitee.Email);
        (await Mine(invitee)).ShouldBeEmpty();
        (await _driver.OkAsync<List<InvitationDto>>(HttpMethod.Get, Invitations(project), owner)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Declining_leaves_the_user_out_and_they_can_be_invited_again()
    {
        var (owner, project) = await OwnedProjectAsync("Blog");
        var invitee = await _driver.SignUpAsync();
        var invitation = await InviteAsync(project, owner, invitee, ProjectRole.Admin);

        (await _driver.SendAsync(HttpMethod.Post, $"/api/me/invitations/{invitation.Id}/decline", invitee)).StatusCode
            .ShouldBe(HttpStatusCode.NoContent);

        (await Mine(invitee)).ShouldBeEmpty();
        (await _driver.SendAsync(HttpMethod.Get, $"/api/projects/{project.Id}", invitee)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _driver.SendAsync(HttpMethod.Post, $"/api/me/invitations/{invitation.Id}/accept", invitee)).StatusCode
            .ShouldBe(HttpStatusCode.NotFound); // answered already
        await InviteAsync(project, owner, invitee, ProjectRole.Developer);
    }

    [Fact]
    public async Task An_admin_can_cancel_a_pending_invitation_and_it_can_no_longer_be_accepted()
    {
        var (owner, project) = await OwnedProjectAsync("Crm");
        var invitee = await _driver.SignUpAsync();
        var invitation = await InviteAsync(project, owner, invitee, ProjectRole.Developer);

        (await _driver.SendAsync(HttpMethod.Delete, $"{Invitations(project)}/{invitation.Id}", owner)).StatusCode
            .ShouldBe(HttpStatusCode.NoContent);

        (await Mine(invitee)).ShouldBeEmpty();
        (await _driver.SendAsync(HttpMethod.Post, $"/api/me/invitations/{invitation.Id}/accept", invitee)).StatusCode
            .ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Invitations_are_only_the_invited_users_to_answer_and_only_admins_send_or_cancel_them()
    {
        var (owner, project) = await OwnedProjectAsync("Guarded");
        var developer = await _driver.SignUpAsync();
        await _driver.AddMemberAsync(project.Id, owner, developer, ProjectRole.Developer);
        var invitee = await _driver.SignUpAsync();
        var stranger = await _driver.SignUpAsync();
        var invitation = await InviteAsync(project, owner, invitee, ProjectRole.Developer);

        // Someone else can't accept or decline it, or even tell it exists.
        (await _driver.SendAsync(HttpMethod.Post, $"/api/me/invitations/{invitation.Id}/accept", stranger)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _driver.SendAsync(HttpMethod.Post, $"/api/me/invitations/{invitation.Id}/decline", developer)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Mine(stranger)).ShouldBeEmpty();

        // Developers see pending invitations but can't send or cancel them; strangers see nothing.
        (await _driver.OkAsync<List<InvitationDto>>(HttpMethod.Get, Invitations(project), developer)).ShouldHaveSingleItem();
        (await _driver.SendAsync(HttpMethod.Delete, $"{Invitations(project)}/{invitation.Id}", developer)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _driver.SendAsync(HttpMethod.Post, Invitations(project), developer, new InviteMemberRequest(stranger.Email, ProjectRole.Developer)))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _driver.SendAsync(HttpMethod.Get, Invitations(project), stranger)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // One pending invitation per person.
        (await _driver.SendAsync(HttpMethod.Post, Invitations(project), owner, new InviteMemberRequest(invitee.Email, ProjectRole.Admin)))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Invitations_to_a_deleted_project_disappear()
    {
        var (owner, project) = await OwnedProjectAsync("Short lived");
        var invitee = await _driver.SignUpAsync();
        await InviteAsync(project, owner, invitee, ProjectRole.Developer);

        (await _driver.SendAsync(HttpMethod.Delete, $"/api/projects/{project.Id}", owner)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await Mine(invitee)).ShouldBeEmpty();
    }

    private async Task<(SignedIn Owner, ProjectDto Project)> OwnedProjectAsync(string name)
    {
        var owner = await _driver.SignUpAsync();
        return (owner, await _driver.CreateProjectAsync(owner, name));
    }

    private Task<InvitationDto> InviteAsync(ProjectDto project, SignedIn admin, SignedIn invitee, ProjectRole role) =>
        _driver.OkAsync<InvitationDto>(HttpMethod.Post, Invitations(project), admin, new InviteMemberRequest(invitee.Email, role), HttpStatusCode.Created);

    private Task<List<InvitationDto>> Mine(SignedIn user) => _driver.OkAsync<List<InvitationDto>>(HttpMethod.Get, "/api/me/invitations", user);

    private static string Invitations(ProjectDto project) => $"/api/projects/{project.Id}/invitations";
}
