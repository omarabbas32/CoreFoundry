using CoreFoundry.Application.Common;
using CoreFoundry.Domain.Common;
using CoreFoundry.Domain.Projects;
using Shouldly;

namespace CoreFoundry.UnitTests.Projects;

public class ProjectServiceTests
{
    private const long Owner = 42;
    private const long Stranger = 7;

    private readonly ProjectsHarness _projects = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Create_provisions_the_database_and_activates_the_project()
    {
        var created = await _projects.Service.CreateAsync(Owner, "Book Shop", Ct);

        created.Status.ShouldBe(ProjectStatus.Active);
        created.Role.ShouldBe(ProjectRole.Owner);
        created.Slug.ShouldBe("book-shop");
        created.DatabaseName.ShouldBe($"cf_p_{created.Id}");
        _projects.Provisioner.Databases.ShouldContain(created.DatabaseName);
    }

    [Fact]
    public async Task Create_suffixes_a_taken_slug()
    {
        await _projects.Service.CreateAsync(Owner, "Shop", Ct);
        await _projects.Service.CreateAsync(Owner, "shop!", Ct);

        (await _projects.Service.CreateAsync(Stranger, "SHOP", Ct)).Slug.ShouldBe("shop-3");
    }

    [Fact]
    public async Task Create_keeps_the_project_as_failed_when_the_database_cannot_be_created()
    {
        _projects.Provisioner.FailCreate = true;

        var created = await _projects.Service.CreateAsync(Owner, "Shop", Ct);

        created.Status.ShouldBe(ProjectStatus.Failed);
        _projects.Provisioner.Databases.ShouldBeEmpty();
    }

    [Fact]
    public async Task Retry_provisioning_activates_a_failed_project()
    {
        _projects.Provisioner.FailCreate = true;
        var created = await _projects.Service.CreateAsync(Owner, "Shop", Ct);
        _projects.Provisioner.FailCreate = false;

        var retried = await _projects.Service.RetryProvisioningAsync(created.Id, Owner, Ct);

        retried.Status.ShouldBe(ProjectStatus.Active);
    }

    [Fact]
    public async Task Retry_is_rejected_for_an_active_project()
    {
        var created = await _projects.Service.CreateAsync(Owner, "Shop", Ct);

        await Should.ThrowAsync<DomainException>(() => _projects.Service.RetryProvisioningAsync(created.Id, Owner, Ct));
    }

    [Fact]
    public async Task Create_rejects_a_blank_name_as_a_validation_error()
    {
        var error = await Should.ThrowAsync<ValidationFailedException>(() => _projects.Service.CreateAsync(Owner, "   ", Ct));

        error.Errors.Keys.ShouldBe(["name"]);
    }

    [Fact]
    public async Task List_returns_only_the_callers_projects_with_their_role()
    {
        await _projects.Service.CreateAsync(Owner, "Mine", Ct);
        await _projects.Service.CreateAsync(Stranger, "Theirs", Ct);

        var mine = await _projects.Service.ListForUserAsync(Owner, Ct);

        mine.ShouldHaveSingleItem().Name.ShouldBe("Mine");
        mine[0].Role.ShouldBe(ProjectRole.Owner);
    }

    [Fact]
    public async Task Rename_changes_the_name_but_not_the_slug()
    {
        var created = await _projects.Service.CreateAsync(Owner, "Shop", Ct);

        var renamed = await _projects.Service.RenameAsync(created.Id, Owner, "Book Shop", Ct);

        renamed.Name.ShouldBe("Book Shop");
        renamed.Slug.ShouldBe("shop");
    }

    [Fact]
    public async Task Delete_drops_the_database_and_removes_the_project()
    {
        var created = await _projects.Service.CreateAsync(Owner, "Shop", Ct);

        await _projects.Service.DeleteAsync(created.Id, Ct);

        _projects.Provisioner.Databases.ShouldNotContain(created.DatabaseName);
        _projects.Projects.All.ShouldBeEmpty();
    }

    [Fact]
    public async Task Failed_drop_leaves_the_project_deleting_and_hidden_until_recovery_finishes()
    {
        var created = await _projects.Service.CreateAsync(Owner, "Shop", Ct);
        _projects.Provisioner.FailDrop = true;

        await Should.ThrowAsync<DatabaseProvisioningException>(() => _projects.Service.DeleteAsync(created.Id, Ct));

        _projects.Stored(created.Id).Status.ShouldBe(ProjectStatus.Deleting);
        (await _projects.Service.ListForUserAsync(Owner, Ct)).ShouldBeEmpty();
        await Should.ThrowAsync<NotFoundException>(() => _projects.Service.GetAsync(created.Id, Owner, Ct));

        _projects.Provisioner.FailDrop = false;
        await _projects.Service.RecoverAsync(Ct);

        _projects.Projects.All.ShouldBeEmpty();
        _projects.Provisioner.Databases.ShouldBeEmpty();
    }

    [Fact]
    public async Task Recovery_provisions_projects_left_in_provisioning()
    {
        // Simulate a crash between saving the project and creating its database.
        var stuck = new Project("Stuck", "stuck", Owner);
        _projects.Projects.Add(stuck);
        await _projects.UnitOfWork.SaveChangesAsync(Ct);

        await _projects.Service.RecoverAsync(Ct);

        stuck.Status.ShouldBe(ProjectStatus.Active);
        _projects.Provisioner.Databases.ShouldContain(stuck.DatabaseName);
    }

    [Fact]
    public async Task Get_of_an_unknown_project_is_not_found() =>
        await Should.ThrowAsync<NotFoundException>(() => _projects.Service.GetAsync(999, Owner, Ct));
}
