using System.Net.Http.Json;
using CoreFoundry.Api.Auth;
using CoreFoundry.Application.Demo;
using CoreFoundry.Application.Projects;
using CoreFoundry.Application.SchemaEngine;
using CoreFoundry.Application.Templates;
using CoreFoundry.Domain.Projects;
using CoreFoundry.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CoreFoundry.IntegrationTests.Demo;

/// <summary>The <c>seed</c> command's demo data.</summary>
[Collection(TestDatabaseGroup.Name)]
public sealed class DemoSeederTests : IDisposable
{
    private readonly TestDatabaseApi _api;
    private readonly HttpClient _client;
    private readonly ApiDriver _driver;

    public DemoSeederTests(TestDatabaseApi api)
    {
        api.SkipIfUnavailable();
        _api = api;
        _client = api.CreateHttpsClient();
        _driver = new ApiDriver(_client);
    }

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task Seeding_adds_both_accounts_and_an_applied_shop_with_a_pending_change_once()
    {
        var result = await SeedAsync();

        result.Seeded.ShouldBeTrue();
        result.SampleRows.ShouldBe(SchemaTemplates.Find("ecommerce")!.SampleRows.Count);

        var owner = await SignInAsync(DemoSeeder.OwnerEmail);
        var developer = await SignInAsync(DemoSeeder.DeveloperEmail);
        var project = (await _driver.OkAsync<List<ProjectDto>>(HttpMethod.Get, "/api/projects", owner)).ShouldHaveSingleItem();
        (project.Id, project.Name, project.Role, project.SchemaVersion).ShouldBe((result.ProjectId!.Value, DemoSeeder.ProjectName, ProjectRole.Owner, 1));
        (await _driver.OkAsync<List<ProjectDto>>(HttpMethod.Get, "/api/projects", developer)).ShouldHaveSingleItem().Role.ShouldBe(ProjectRole.Developer);
        (await _api.CountTablesInAsync(project.DatabaseName)).ShouldBe(8);

        var customers = await _driver.OkAsync<System.Text.Json.JsonElement>(HttpMethod.Get, $"/api/projects/{project.Id}/data/customers?pageSize=1", owner);
        customers.GetProperty("total").GetInt64().ShouldBe(6);

        // The pending change: one rename, one addition and one drop, which Plan & apply flags as data loss.
        var plan = await _driver.OkAsync<SchemaPlanDto>(HttpMethod.Get, $"/api/projects/{project.Id}/schema/plan", owner);
        plan.Operations.Select(operation => $"{operation.Kind} {operation.Table}").Order().ShouldBe(
            ["AddColumn products", "DropColumn customers", "RenameColumn products"]);
        plan.HasDestructive.ShouldBeTrue();

        // Running it again changes nothing.
        (await SeedAsync()).ShouldBe(new DemoSeedResult(false, null, 0));
        (await _driver.OkAsync<List<ProjectDto>>(HttpMethod.Get, "/api/projects", owner)).ShouldHaveSingleItem();
    }

    private async Task<DemoSeedResult> SeedAsync()
    {
        await using var scope = _api.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<DemoSeeder>().SeedAsync(TestContext.Current.CancellationToken);
    }

    private async Task<SignedIn> SignInAsync(string email)
    {
        var response = await _client.PostAsJsonAsync(
            new Uri("/api/auth/login", UriKind.Relative), new LoginRequest(email, DemoSeeder.Password), TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await ApiDriver.ReadAsync<AuthResponse>(response);
        return new SignedIn(body.User.Id, email, body.AccessToken);
    }
}
