using System.Net;
using CoreFoundry.Api.Projects;
using CoreFoundry.Application.Projects;
using CoreFoundry.Application.Schema;
using CoreFoundry.Application.SchemaEngine;
using CoreFoundry.Application.Templates;
using CoreFoundry.Domain.Projects;
using CoreFoundry.Domain.Schema;
using CoreFoundry.IntegrationTests.Infrastructure;
using Shouldly;

namespace CoreFoundry.IntegrationTests.Templates;

/// <summary>Starting a project from a ready schema, and its sample rows.</summary>
[Collection(TestDatabaseGroup.Name)]
public sealed class TemplateEndpointsTests : IDisposable
{
    private readonly TestDatabaseApi _api;
    private readonly HttpClient _client;
    private readonly ApiDriver _driver;

    public TemplateEndpointsTests(TestDatabaseApi api)
    {
        api.SkipIfUnavailable();
        _api = api;
        _client = api.CreateHttpsClient();
        _driver = new ApiDriver(_client);
    }

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task Templates_are_listed_for_signed_in_users()
    {
        var user = await _driver.SignUpAsync();

        var templates = await _driver.OkAsync<List<TemplateDto>>(HttpMethod.Get, "/api/templates", user);

        var shop = templates.ShouldHaveSingleItem();
        (shop.Key, shop.Name, shop.Tables.Count).ShouldBe(("ecommerce", "E-commerce", 8));
        shop.Tables.Single(table => table.Name == "order_items").References.ShouldBe(["orders", "products"]);
        shop.SampleRowCount.ShouldBeGreaterThan(50);
        (await _driver.SendAsync(HttpMethod.Get, "/api/templates", null)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_template_creates_the_draft_tables_with_their_references()
    {
        var (owner, project) = await ProjectAsync();

        var used = await _driver.OkAsync<UsedTemplateDto>(HttpMethod.Post, $"/api/projects/{project.Id}/templates/ecommerce", owner,
            new UseTemplateRequest(WithSampleData: true));

        (used.TemplateKey, used.SampleDataPending).ShouldBe(("ecommerce", true));
        used.Tables.Select(table => table.Name).Order().ShouldBe(
            ["addresses", "categories", "customers", "order_items", "orders", "payments", "products", "reviews"]);
        used.Tables.ShouldAllBe(table => table.State == SchemaObjectState.New);

        var schema = await _driver.OkAsync<List<TableDto>>(HttpMethod.Get, $"/api/projects/{project.Id}/schema", owner);
        Column(schema, "categories", "parent_id").ShouldSatisfyAllConditions(
            column => column.ReferencesTableName.ShouldBe("categories"),
            column => column.OnDelete.ShouldBe(ReferenceAction.SetNull));
        Column(schema, "orders", "customer_id").OnDelete.ShouldBe(ReferenceAction.Restrict);
        Column(schema, "orders", "public_id").DefaultValue.ShouldBe("UUID()");
        schema.Single(table => table.Name == "products").Columns.Select(column => column.Name).ShouldBe(
            ["category_id", "sku", "name", "description", "price", "stock", "is_active", "attributes", "created_at"]);

        var reloaded = await _driver.OkAsync<ProjectDto>(HttpMethod.Get, $"/api/projects/{project.Id}", owner);
        (reloaded.TemplateKey, reloaded.SampleDataPending).ShouldBe(("ecommerce", true));

        // The plan creates all eight tables, and applying it works.
        var plan = await _driver.OkAsync<SchemaPlanDto>(HttpMethod.Get, $"/api/projects/{project.Id}/schema/plan", owner);
        plan.Operations.Count(operation => operation.Kind == "CreateTable").ShouldBe(8);
        await _driver.OkAsync<ApplyResultDto>(HttpMethod.Post, $"/api/projects/{project.Id}/schema/apply", owner, new ApplyRequest(plan.PlanHash, false));
        (await _api.CountTablesInAsync(project.DatabaseName)).ShouldBe(8);
    }

    [Fact]
    public async Task Only_an_empty_project_can_start_from_a_template()
    {
        var (owner, project) = await ProjectAsync();
        await _driver.OkAsync<TableDto>(HttpMethod.Post, $"/api/projects/{project.Id}/tables", owner,
            new CreateTableRequest("notes", [new ColumnRequest("body", DataType.Text, null, null, null, true, false, null)]), HttpStatusCode.Created);

        (await _driver.SendAsync(HttpMethod.Post, $"/api/projects/{project.Id}/templates/ecommerce", owner, new UseTemplateRequest(false)))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await _driver.OkAsync<List<TableSummaryDto>>(HttpMethod.Get, $"/api/projects/{project.Id}/tables", owner)).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Unknown_templates_are_404_and_so_are_other_peoples_projects()
    {
        var (owner, project) = await ProjectAsync();
        var stranger = await _driver.SignUpAsync();
        var developer = await _driver.SignUpAsync();
        await _driver.AddMemberAsync(project.Id, owner, developer, ProjectRole.Developer);

        (await _driver.SendAsync(HttpMethod.Post, $"/api/projects/{project.Id}/templates/nope", owner, new UseTemplateRequest(false)))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _driver.SendAsync(HttpMethod.Post, $"/api/projects/{project.Id}/templates/ecommerce", stranger, new UseTemplateRequest(false)))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _driver.SendAsync(HttpMethod.Post, $"/api/projects/{project.Id}/templates/ecommerce", developer, new UseTemplateRequest(false)))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private async Task<(SignedIn Owner, ProjectDto Project)> ProjectAsync()
    {
        var owner = await _driver.SignUpAsync();
        return (owner, await _driver.CreateProjectAsync(owner, "Shop"));
    }

    private static ColumnDto Column(List<TableDto> schema, string table, string column) =>
        schema.Single(candidate => candidate.Name == table).Columns.Single(candidate => candidate.Name == column);
}
