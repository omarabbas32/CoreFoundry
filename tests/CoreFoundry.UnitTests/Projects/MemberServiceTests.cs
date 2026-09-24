using CoreFoundry.Application.Common;
using CoreFoundry.Application.Projects;
using CoreFoundry.Domain.Common;
using CoreFoundry.Domain.Projects;
using CoreFoundry.Domain.Users;
using CoreFoundry.UnitTests.Auth;
using Shouldly;

namespace CoreFoundry.UnitTests.Projects;

public class MemberServiceTests
{
    private readonly FakeProjects _projects = new();
    private readonly FakeUsers _users = new();
    private readonly MemberService _members;
    private readonly Project _project;
    private readonly User _owner;
    private readonly User _alice;
    private readonly User _bob;

    public MemberServiceTests()
    {
        _members = new MemberService(_projects, _users, new FakeProjectsUnitOfWork(_projects));
        _owner = AddUser(1, "owner@test.dev");
        _alice = AddUser(2, "alice@test.dev");
        _bob = AddUser(3, "bob@test.dev");

        _project = new Project("Shop", "shop", _owner.Id);
        typeof(Project).GetProperty(nameof(Project.Id))!.SetValue(_project, 10L);
        _projects.Add(_project);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Adds_an_existing_account_by_email()
    {
        var added = await _members.AddAsync(_project.Id, " Alice@Test.dev ", ProjectRole.Developer, Ct);

        added.UserId.ShouldBe(_alice.Id);
        added.Email.ShouldBe("alice@test.dev");
        _project.FindMember(_alice.Id)!.Role.ShouldBe(ProjectRole.Developer);
    }

    [Fact]
    public async Task Adding_an_unknown_email_is_not_found() =>
        await Should.ThrowAsync<NotFoundException>(() => _members.AddAsync(_project.Id, "nobody@test.dev", ProjectRole.Developer, Ct));

    [Fact]
    public async Task Adding_someone_twice_is_a_conflict()
    {
        await _members.AddAsync(_project.Id, _alice.Email, ProjectRole.Developer, Ct);

        await Should.ThrowAsync<ConflictException>(() => _members.AddAsync(_project.Id, _alice.Email, ProjectRole.Admin, Ct));
    }

    [Fact]
    public async Task Adding_as_owner_is_rejected() =>
        await Should.ThrowAsync<DomainException>(() => _members.AddAsync(_project.Id, _alice.Email, ProjectRole.Owner, Ct));

    [Fact]
    public async Task List_puts_the_owner_first_then_admins_then_developers()
    {
        await _members.AddAsync(_project.Id, _bob.Email, ProjectRole.Developer, Ct);
        await _members.AddAsync(_project.Id, _alice.Email, ProjectRole.Admin, Ct);

        var list = await _members.ListAsync(_project.Id, Ct);

        list.Select(member => member.Email).ShouldBe(["owner@test.dev", "alice@test.dev", "bob@test.dev"]);
    }

    [Fact]
    public async Task A_developer_can_leave_but_cannot_remove_others()
    {
        await _members.AddAsync(_project.Id, _alice.Email, ProjectRole.Developer, Ct);
        await _members.AddAsync(_project.Id, _bob.Email, ProjectRole.Developer, Ct);

        await Should.ThrowAsync<ForbiddenException>(() => _members.RemoveAsync(_project.Id, callerId: _alice.Id, userId: _bob.Id, Ct));
        await _members.RemoveAsync(_project.Id, callerId: _alice.Id, userId: _alice.Id, Ct);

        _project.FindMember(_alice.Id).ShouldBeNull();
        _project.FindMember(_bob.Id).ShouldNotBeNull();
    }

    [Fact]
    public async Task An_admin_can_remove_a_member_but_nobody_can_remove_the_owner()
    {
        await _members.AddAsync(_project.Id, _alice.Email, ProjectRole.Admin, Ct);
        await _members.AddAsync(_project.Id, _bob.Email, ProjectRole.Developer, Ct);

        await _members.RemoveAsync(_project.Id, callerId: _alice.Id, userId: _bob.Id, Ct);
        await Should.ThrowAsync<DomainException>(() => _members.RemoveAsync(_project.Id, callerId: _alice.Id, userId: _owner.Id, Ct));
        await Should.ThrowAsync<DomainException>(() => _members.RemoveAsync(_project.Id, callerId: _owner.Id, userId: _owner.Id, Ct));
    }

    [Fact]
    public async Task Changing_the_role_of_a_non_member_is_not_found() =>
        await Should.ThrowAsync<NotFoundException>(() => _members.ChangeRoleAsync(_project.Id, _bob.Id, ProjectRole.Admin, Ct));

    [Fact]
    public async Task Transfer_swaps_owner_and_admin()
    {
        await _members.AddAsync(_project.Id, _alice.Email, ProjectRole.Developer, Ct);

        var members = await _members.TransferOwnershipAsync(_project.Id, _alice.Id, Ct);

        members.Single(member => member.Role == ProjectRole.Owner).UserId.ShouldBe(_alice.Id);
        members.Single(member => member.UserId == _owner.Id).Role.ShouldBe(ProjectRole.Admin);
        _project.OwnerId.ShouldBe(_alice.Id);
    }

    [Fact]
    public async Task Transfer_to_a_non_member_is_not_found() =>
        await Should.ThrowAsync<NotFoundException>(() => _members.TransferOwnershipAsync(_project.Id, _bob.Id, Ct));

    private User AddUser(long id, string email)
    {
        var user = new User(email, "hash");
        typeof(User).GetProperty(nameof(User.Id))!.SetValue(user, id);
        _users.Add(user);
        return user;
    }
}
