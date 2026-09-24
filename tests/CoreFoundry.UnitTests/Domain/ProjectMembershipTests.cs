using CoreFoundry.Domain.Common;
using CoreFoundry.Domain.Projects;
using Shouldly;

namespace CoreFoundry.UnitTests.Domain;

public class ProjectMembershipTests
{
    private const long Owner = 1;
    private const long Alice = 2;
    private const long Bob = 3;

    private static Project NewProject() => new("Shop", "shop", Owner);

    [Fact]
    public void Adds_admins_and_developers()
    {
        var project = NewProject();

        project.AddMember(Alice, ProjectRole.Admin);
        project.AddMember(Bob, ProjectRole.Developer);

        project.Members.Count.ShouldBe(3);
        project.FindMember(Bob)!.Role.ShouldBe(ProjectRole.Developer);
    }

    [Fact]
    public void Cannot_add_a_second_owner() =>
        Should.Throw<DomainException>(() => NewProject().AddMember(Alice, ProjectRole.Owner));

    [Fact]
    public void Cannot_add_the_same_user_twice()
    {
        var project = NewProject();
        project.AddMember(Alice, ProjectRole.Developer);

        Should.Throw<DomainException>(() => project.AddMember(Alice, ProjectRole.Admin));
    }

    [Fact]
    public void Changes_a_role_between_admin_and_developer()
    {
        var project = NewProject();
        project.AddMember(Alice, ProjectRole.Developer);

        project.ChangeMemberRole(Alice, ProjectRole.Admin);

        project.FindMember(Alice)!.Role.ShouldBe(ProjectRole.Admin);
    }

    [Fact]
    public void Cannot_promote_to_owner_or_demote_the_owner()
    {
        var project = NewProject();
        project.AddMember(Alice, ProjectRole.Admin);

        Should.Throw<DomainException>(() => project.ChangeMemberRole(Alice, ProjectRole.Owner));
        Should.Throw<DomainException>(() => project.ChangeMemberRole(Owner, ProjectRole.Developer));
    }

    [Fact]
    public void Removes_a_member_but_never_the_owner()
    {
        var project = NewProject();
        project.AddMember(Alice, ProjectRole.Admin);

        project.RemoveMember(Alice);

        project.FindMember(Alice).ShouldBeNull();
        Should.Throw<DomainException>(() => project.RemoveMember(Owner));
    }

    [Fact]
    public void Transfer_makes_the_new_owner_and_keeps_the_old_one_as_admin()
    {
        var project = NewProject();
        project.AddMember(Alice, ProjectRole.Developer);

        project.TransferOwnership(Alice);

        project.OwnerId.ShouldBe(Alice);
        project.FindMember(Alice)!.Role.ShouldBe(ProjectRole.Owner);
        project.FindMember(Owner)!.Role.ShouldBe(ProjectRole.Admin);
        project.Members.Count(member => member.Role == ProjectRole.Owner).ShouldBe(1);
    }

    [Fact]
    public void Transfer_requires_an_existing_member_other_than_the_owner()
    {
        var project = NewProject();

        Should.Throw<DomainException>(() => project.TransferOwnership(Bob));
        Should.Throw<DomainException>(() => project.TransferOwnership(Owner));
    }

    [Fact]
    public void Changing_or_removing_a_non_member_is_rejected()
    {
        var project = NewProject();

        Should.Throw<DomainException>(() => project.ChangeMemberRole(Bob, ProjectRole.Admin));
        Should.Throw<DomainException>(() => project.RemoveMember(Bob));
    }
}
