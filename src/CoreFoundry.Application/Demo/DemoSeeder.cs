using CoreFoundry.Application.Auth;
using CoreFoundry.Application.Common;
using CoreFoundry.Application.Projects;
using CoreFoundry.Application.Schema;
using CoreFoundry.Application.SchemaEngine;
using CoreFoundry.Application.Templates;
using CoreFoundry.Domain.Projects;
using CoreFoundry.Domain.Schema;
using CoreFoundry.Domain.Users;

namespace CoreFoundry.Application.Demo;

/// <param name="Seeded">False when the demo account already existed and nothing was changed.</param>
public sealed record DemoSeedResult(bool Seeded, long? ProjectId, int SampleRows);

/// <summary>
/// Demo data for screenshots, videos and a first look after <c>docker compose up</c>: two accounts, and a
/// project started from the E-commerce template, applied, with its sample rows and one pending change, so
/// every page has something to show. Goes through the same services as the API. Runs once: if the demo
/// account exists, it does nothing.
/// </summary>
public sealed class DemoSeeder(
    IUserRepository users,
    IPasswordHasher passwordHasher,
    IUnitOfWork unitOfWork,
    ProjectService projectService,
    InvitationService invitations,
    TemplateService templates,
    TableService tableService,
    SchemaPlanService planService,
    SchemaApplier applier,
    SampleDataService sampleData)
{
    public const string OwnerEmail = "demo@corefoundry.dev";
    public const string DeveloperEmail = "dev@corefoundry.dev";
    public const string Password = "corefoundry-demo";
    public const string ProjectName = "Demo shop";

    public async Task<DemoSeedResult> SeedAsync(CancellationToken cancellationToken)
    {
        if (await users.EmailExistsAsync(OwnerEmail, cancellationToken))
        {
            return new DemoSeedResult(false, null, 0);
        }

        var owner = await AddUserAsync(OwnerEmail, cancellationToken);
        var developer = await AddUserAsync(DeveloperEmail, cancellationToken);

        var project = await projectService.CreateAsync(owner.Id, ProjectName, cancellationToken);
        if (project.Status != ProjectStatus.Active)
        {
            throw new InvalidOperationException($"The demo project's database ({project.DatabaseName}) couldn't be created; see the log.");
        }

        var invitation = await invitations.InviteAsync(project.Id, owner.Id, DeveloperEmail, ProjectRole.Developer, cancellationToken);
        await invitations.AcceptAsync(developer.Id, invitation.Id, cancellationToken);

        await templates.UseAsync(project.Id, "ecommerce", withSampleData: true, cancellationToken);
        var plan = await planService.PlanAsync(project.Id, cancellationToken);
        await applier.ApplyAsync(project.Id, owner.Id, plan.PlanHash, acknowledgeDestructive: false, cancellationToken);
        var rows = await sampleData.InsertAsync(project.Id, cancellationToken);

        await DraftPendingChangeAsync(project.Id, cancellationToken);
        return new DemoSeedResult(true, project.Id, rows.Inserted);
    }

    private async Task<User> AddUserAsync(string email, CancellationToken cancellationToken)
    {
        var user = new User(email, passwordHasher.Hash(Password));
        users.Add(user);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return user;
    }

    /// <summary>A rename, an addition and a drop, so Plan &amp; apply shows each kind of change and a data-loss warning.</summary>
    private async Task DraftPendingChangeAsync(long projectId, CancellationToken cancellationToken)
    {
        var schema = await tableService.GetSchemaAsync(projectId, cancellationToken);

        var products = schema.Single(table => table.Name == "products");
        var stock = products.Columns.Single(column => column.Name == "stock");
        products = await tableService.UpdateColumnAsync(projectId, products.Id, stock.Id, products.Version,
            InputFrom(stock) with { Name = "stock_quantity" }, cancellationToken);
        await tableService.AddColumnAsync(projectId, products.Id, products.Version,
            new ColumnInput("discount_price", DataType.Decimal, null, 10, 2, IsNullable: true, IsUnique: false, DefaultValue: null),
            cancellationToken);

        var customers = schema.Single(table => table.Name == "customers");
        var phone = customers.Columns.Single(column => column.Name == "phone");
        await tableService.DeleteColumnAsync(projectId, customers.Id, phone.Id, customers.Version, cancellationToken);
    }

    private static ColumnInput InputFrom(ColumnDto column) => new(
        column.Name, column.DataType, column.Length, column.Precision, column.Scale, column.IsNullable, column.IsUnique,
        column.DefaultValue, column.ReferencesTableId, column.OnDelete);
}
