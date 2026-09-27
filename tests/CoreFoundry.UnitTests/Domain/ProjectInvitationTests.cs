using CoreFoundry.Domain.Common;
using CoreFoundry.Domain.Projects;
using Shouldly;

namespace CoreFoundry.UnitTests.Domain;

public class ProjectInvitationTests
{
    [Theory]
    [InlineData(ProjectRole.Admin)]
    [InlineData(ProjectRole.Developer)]
    public void Invites_as_admin_or_developer(ProjectRole role)
    {
        var invitation = new ProjectInvitation(projectId: 1, userId: 2, role, invitedBy: 3);

        (invitation.ProjectId, invitation.UserId, invitation.Role, invitation.InvitedBy).ShouldBe((1L, 2L, role, (long?)3));
    }

    [Theory]
    [InlineData(ProjectRole.Owner)]
    [InlineData((ProjectRole)0)]
    public void Ownership_and_unknown_roles_can_not_be_invited(ProjectRole role) =>
        Should.Throw<DomainException>(() => new ProjectInvitation(1, 2, role, 3)).Field.ShouldBe("role");

    [Fact]
    public void Ids_must_be_real() =>
        Should.Throw<DomainException>(() => new ProjectInvitation(0, 2, ProjectRole.Developer, 3));
}
