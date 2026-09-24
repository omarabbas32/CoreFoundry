using CoreFoundry.Domain.Common;
using CoreFoundry.Domain.Projects;
using Shouldly;

namespace CoreFoundry.UnitTests.Domain;

public class ProjectTests
{
    [Fact]
    public void New_project_is_provisioning_with_its_owner_as_only_member()
    {
        var project = new Project("Bookshop", "bookshop", ownerId: 42);

        project.Status.ShouldBe(ProjectStatus.Provisioning);
        project.SchemaVersion.ShouldBe(0);
        var member = project.Members.ShouldHaveSingleItem();
        member.UserId.ShouldBe(42);
        member.Role.ShouldBe(ProjectRole.Owner);
    }

    [Theory]
    [InlineData("Book Shop")]
    [InlineData("book_shop")]
    [InlineData("-bookshop")]
    [InlineData("bookshop-")]
    [InlineData("book--shop")]
    [InlineData("BookShop")]
    public void Invalid_slug_is_rejected(string slug) =>
        Should.Throw<DomainException>(() => new Project("Bookshop", slug, 1));

    [Fact]
    public void Database_name_is_derived_from_the_id() =>
        Project.DatabaseNameFor(7).ShouldBe("cf_p_7");

    [Fact]
    public void Database_name_is_unavailable_before_the_project_is_saved() =>
        Should.Throw<InvalidOperationException>(() => new Project("Bookshop", "bookshop", 1).DatabaseName);

    [Fact]
    public void Provisioning_can_fail_and_be_retried_until_active()
    {
        var project = new Project("Bookshop", "bookshop", 1);

        project.MarkProvisioningFailed();
        project.Status.ShouldBe(ProjectStatus.Failed);
        project.RetryProvisioning();
        project.MarkActive();

        project.Status.ShouldBe(ProjectStatus.Active);
    }

    [Fact]
    public void An_active_project_cannot_go_back_to_provisioning()
    {
        var project = new Project("Bookshop", "bookshop", 1);
        project.MarkActive();

        Should.Throw<DomainException>(project.RetryProvisioning);
    }

    [Fact]
    public void A_deleting_project_cannot_be_reactivated()
    {
        var project = new Project("Bookshop", "bookshop", 1);
        project.MarkDeleting();

        Should.Throw<DomainException>(project.MarkActive);
    }

    [Theory]
    [InlineData("My Shop", "my-shop")]
    [InlineData("  Book   Shop!! 2026 ", "book-shop-2026")]
    [InlineData("Café-Bar", "caf-bar")]
    [InlineData("---", "project")]
    [InlineData("متجر الكتب", "project")]
    [InlineData("", "project")]
    public void Slug_from_name(string name, string expected)
    {
        var slug = Project.SlugFrom(name);

        slug.ShouldBe(expected);
        Should.NotThrow(() => new Project(name.Length > 0 ? name : "x", slug, 1)); // always a valid slug
    }

    [Fact]
    public void Slug_from_a_long_name_leaves_room_for_a_collision_suffix()
    {
        var slug = Project.SlugFrom(new string('a', 30) + " " + new string('b', 60));

        slug.Length.ShouldBeLessThanOrEqualTo(Project.SlugBaseMaxLength);
        slug.ShouldNotEndWith("-");
        Should.NotThrow(() => new Project("Long", Project.SlugCandidate(slug, 9_999_999), 1));
    }

    [Theory]
    [InlineData(1, "shop")]
    [InlineData(2, "shop-2")]
    [InlineData(10, "shop-10")]
    public void Slug_candidates_for_collisions(int attempt, string expected) =>
        Project.SlugCandidate("shop", attempt).ShouldBe(expected);

    [Theory]
    [InlineData(ProjectRole.Owner, ProjectRole.Admin, true)]
    [InlineData(ProjectRole.Admin, ProjectRole.Admin, true)]
    [InlineData(ProjectRole.Developer, ProjectRole.Admin, false)]
    [InlineData(ProjectRole.Admin, ProjectRole.Owner, false)]
    [InlineData(ProjectRole.Developer, ProjectRole.Developer, true)]
    public void Role_ranking(ProjectRole role, ProjectRole minimum, bool expected) =>
        role.AtLeast(minimum).ShouldBe(expected);
}
