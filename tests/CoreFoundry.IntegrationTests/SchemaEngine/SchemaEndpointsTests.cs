using System.Globalization;
using System.Net;
using System.Text.Json;
using CoreFoundry.Api.Projects;
using CoreFoundry.Application.Projects;
using CoreFoundry.Application.Schema;
using CoreFoundry.Application.SchemaEngine;
using CoreFoundry.Domain.Projects;
using CoreFoundry.Domain.Schema;
using CoreFoundry.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using Shouldly;
using static CoreFoundry.IntegrationTests.Infrastructure.ApiDriver;

namespace CoreFoundry.IntegrationTests.SchemaEngine;

/// <summary>Plan / apply / history / drift through the API, against real project databases.</summary>
[Collection(TestDatabaseGroup.Name)]
public sealed class SchemaEndpointsTests : IDisposable
{
    private readonly TestDatabaseApi _api;
    private readonly HttpClient _client;
    private readonly ApiDriver _driver;

    public SchemaEndpointsTests(TestDatabaseApi api)
    {
        api.SkipIfUnavailable();
        _api = api;
        _client = api.CreateHttpsClient();
        _driver = new ApiDriver(_client);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task Design_plan_apply_creates_the_tables_and_the_next_plan_is_empty()
    {
        var (owner, project) = await ProjectAsync();
        var authors = await CreateTableAsync(project, owner, "authors", Column("name", DataType.Varchar, length: 120, nullable: false));
        await CreateTableAsync(project, owner, "books",
            Column("title", DataType.Varchar, length: 200, nullable: false, unique: true),
            Column("price", DataType.Decimal, precision: 10, scale: 2, nullable: false, defaultValue: "0.00"),
            Column("author_id", DataType.BigInt) with { ReferencesTableId = authors.Id, OnDelete = ReferenceAction.Cascade });

        var plan = await PlanAsync(project, owner);
        plan.Operations.Select(operation => operation.Kind).ShouldBe(["CreateTable", "CreateTable", "AddForeignKey"]);
        plan.HasDestructive.ShouldBeFalse();

        var result = await ApplyAsync(project, owner, plan.PlanHash);
        (result.Version, result.Status, result.Statements).ShouldBe((1, MigrationStatus.Applied, 3));

        var createBooks = await ShowCreateTableAsync(project, "books");
        createBooks.ShouldContain("`title` varchar(200) NOT NULL");
        createBooks.ShouldContain("`price` decimal(10,2) NOT NULL DEFAULT '0.00'");
        createBooks.ShouldContain("UNIQUE KEY `uq_books_title` (`title`)");
        createBooks.ShouldContain("CONSTRAINT `fk_books_author_id` FOREIGN KEY (`author_id`) REFERENCES `authors` (`id`) ON DELETE CASCADE");

        var again = await PlanAsync(project, owner);
        again.Operations.ShouldBeEmpty();
        again.SchemaVersion.ShouldBe(1);
        (await OkAsync<List<TableSummaryDto>>(HttpMethod.Get, $"/api/projects/{project.Id}/tables", owner))
            .ShouldAllBe(table => table.State == SchemaObjectState.Applied);
    }

    [Fact]
    public async Task Renaming_a_column_with_data_keeps_the_data()
    {
        var (owner, project) = await ProjectAsync();
        var books = await CreateTableAsync(project, owner, "books", Column("price", DataType.Decimal, precision: 10, scale: 2));
        await ApplyAsync(project, owner, (await PlanAsync(project, owner)).PlanHash);
        await ExecuteAsync($"INSERT INTO `{project.DatabaseName}`.`books` (`price`) VALUES (12.34), (5.00)");

        books = await OkAsync<TableDto>(HttpMethod.Get, $"/api/projects/{project.Id}/tables/{books.Id}", owner);
        var price = books.Columns.Single();
        var renamed = await OkAsync<TableDto>(HttpMethod.Put, $"/api/projects/{project.Id}/tables/{books.Id}/columns/{price.Id}", owner,
            new SaveColumnRequest(books.Version, "price_usd", DataType.Decimal, null, 10, 2, true, false, null));
        renamed.Columns.Single().State.ShouldBe(SchemaObjectState.Changed);

        var plan = await PlanAsync(project, owner);
        plan.Operations.ShouldHaveSingleItem().Kind.ShouldBe("RenameColumn");
        await ApplyAsync(project, owner, plan.PlanHash);

        (await ScalarAsync($"SELECT GROUP_CONCAT(`price_usd` ORDER BY `id`) FROM `{project.DatabaseName}`.`books`")).ShouldBe("12.34,5.00");
    }

    [Fact]
    public async Task A_failure_mid_apply_is_journaled_and_planning_again_finishes_the_job()
    {
        var (owner, project) = await ProjectAsync();
        var books = await CreateTableAsync(project, owner, "books", Column("title", DataType.Varchar, length: 50));
        await ApplyAsync(project, owner, (await PlanAsync(project, owner)).PlanHash);
        await ExecuteAsync($"INSERT INTO `{project.DatabaseName}`.`books` (`title`) VALUES ('Dune'), ('Dune')");

        // Plan: CREATE TABLE reviews (works), then make books.title unique (fails: duplicates).
        await CreateTableAsync(project, owner, "reviews", Column("body", DataType.Text));
        books = await OkAsync<TableDto>(HttpMethod.Get, $"/api/projects/{project.Id}/tables/{books.Id}", owner);
        await OkAsync<TableDto>(HttpMethod.Put, $"/api/projects/{project.Id}/tables/{books.Id}/columns/{books.Columns[0].Id}", owner,
            new SaveColumnRequest(books.Version, "title", DataType.Varchar, 50, null, null, true, true, null));
        var plan = await PlanAsync(project, owner);
        plan.Statements.Count.ShouldBe(2);

        var response = await _driver.SendAsync(HttpMethod.Post, $"/api/projects/{project.Id}/schema/apply", owner, new ApplyRequest(plan.PlanHash, false));
        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        var problem = await ReadAsync<ProblemDetails>(response);
        problem.Type.ShouldBe("https://corefoundry.dev/problems/apply-failed");
        ((JsonElement)problem.Extensions["failedStatement"]!).GetInt32().ShouldBe(2);
        var migrationId = ((JsonElement)problem.Extensions["migrationId"]!).GetInt64();

        var migration = await OkAsync<MigrationDto>(HttpMethod.Get, $"/api/projects/{project.Id}/schema/migrations/{migrationId}", owner);
        (migration.Status, migration.StatementsApplied, migration.FailedStatement).ShouldBe((MigrationStatus.Failed, 1, 2));
        migration.Error.ShouldNotBeNull().ShouldContain("Duplicate entry");

        // Planning again proposes only what's left; after fixing the data it applies.
        var rest = await PlanAsync(project, owner);
        rest.Operations.ShouldHaveSingleItem().Kind.ShouldBe("AddUniqueKey");
        await ExecuteAsync($"DELETE FROM `{project.DatabaseName}`.`books` WHERE `id` = 2");
        (await ApplyAsync(project, owner, rest.PlanHash)).Version.ShouldBe(2);

        (await PlanAsync(project, owner)).Operations.ShouldBeEmpty();
        var history = await OkAsync<MigrationPageDto>(HttpMethod.Get, $"/api/projects/{project.Id}/schema/migrations", owner);
        history.Items.Select(item => (item.Version, item.Status)).ShouldBe(
            [(2, MigrationStatus.Applied), (2, MigrationStatus.Failed), (1, MigrationStatus.Applied)]);
    }

    [Fact]
    public async Task An_apply_while_the_lock_is_held_is_409_apply_in_progress()
    {
        var (owner, project) = await ProjectAsync();
        await CreateTableAsync(project, owner, "books", Column("title", DataType.Text));
        var plan = await PlanAsync(project, owner);

        await using var holder = new MySqlConnection(_api.EngineConnectionString);
        await holder.OpenAsync(Ct);
        await using (var take = new MySqlCommand($"SELECT GET_LOCK('cf_apply_{project.Id}', 0)", holder))
        {
            (await take.ExecuteScalarAsync(Ct)).ShouldBe(1L);
        }

        await ShouldBeProblemAsync(
            await _driver.SendAsync(HttpMethod.Post, $"/api/projects/{project.Id}/schema/apply", owner, new ApplyRequest(plan.PlanHash, false)),
            HttpStatusCode.Conflict, "apply-in-progress");
    }

    [Fact]
    public async Task A_stale_plan_hash_is_409_plan_stale()
    {
        var (owner, project) = await ProjectAsync();
        await CreateTableAsync(project, owner, "books", Column("title", DataType.Text));
        var plan = await PlanAsync(project, owner);
        await CreateTableAsync(project, owner, "authors");

        await ShouldBeProblemAsync(
            await _driver.SendAsync(HttpMethod.Post, $"/api/projects/{project.Id}/schema/apply", owner, new ApplyRequest(plan.PlanHash, false)),
            HttpStatusCode.Conflict, "plan-stale");
        (await TablesInAsync(project)).ShouldBe(0);
    }

    [Fact]
    public async Task Developers_can_plan_but_not_apply()
    {
        var (owner, project) = await ProjectAsync();
        var developer = await _driver.SignUpAsync();
        await _driver.AddMemberAsync(project.Id, owner, developer, ProjectRole.Developer);
        await CreateTableAsync(project, owner, "books", Column("title", DataType.Text));

        var plan = await PlanAsync(project, developer);

        (await _driver.SendAsync(HttpMethod.Post, $"/api/projects/{project.Id}/schema/apply", developer, new ApplyRequest(plan.PlanHash, false)))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Destructive_plans_need_acknowledging_and_foreign_keys_follow_the_draft()
    {
        var (owner, project) = await ProjectAsync();
        var authors = await CreateTableAsync(project, owner, "authors", Column("name", DataType.Text));
        var books = await CreateTableAsync(project, owner, "books",
            Column("author_id", DataType.BigInt) with { ReferencesTableId = authors.Id, OnDelete = ReferenceAction.Cascade });
        await ApplyAsync(project, owner, (await PlanAsync(project, owner)).PlanHash);

        // Cascade → SetNull: the constraint is dropped and added again.
        books = await OkAsync<TableDto>(HttpMethod.Get, $"/api/projects/{project.Id}/tables/{books.Id}", owner);
        await OkAsync<TableDto>(HttpMethod.Put, $"/api/projects/{project.Id}/tables/{books.Id}/columns/{books.Columns[0].Id}", owner,
            new SaveColumnRequest(books.Version, "author_id", DataType.BigInt, null, null, null, true, false, null, authors.Id, ReferenceAction.SetNull));
        var change = await PlanAsync(project, owner);
        change.Operations.Select(operation => operation.Kind).ShouldBe(["DropForeignKey", "AddForeignKey"]);
        await ApplyAsync(project, owner, change.PlanHash);
        (await ShowCreateTableAsync(project, "books")).ShouldContain("ON DELETE SET NULL");

        // Drop books (which references authors) and authors in one plan.
        books = await OkAsync<TableDto>(HttpMethod.Get, $"/api/projects/{project.Id}/tables/{books.Id}", owner);
        await OkAsync<TableDto>(HttpMethod.Delete, $"/api/projects/{project.Id}/tables/{books.Id}?version={books.Version}", owner);
        authors = await OkAsync<TableDto>(HttpMethod.Get, $"/api/projects/{project.Id}/tables/{authors.Id}", owner);
        await OkAsync<TableDto>(HttpMethod.Delete, $"/api/projects/{project.Id}/tables/{authors.Id}?version={authors.Version}", owner);

        var drop = await PlanAsync(project, owner);
        drop.HasDestructive.ShouldBeTrue();
        drop.Operations.Select(operation => operation.Kind).ShouldBe(["DropForeignKey", "DropTable", "DropTable"]);
        await ShouldBeProblemAsync(
            await _driver.SendAsync(HttpMethod.Post, $"/api/projects/{project.Id}/schema/apply", owner, new ApplyRequest(drop.PlanHash, false)),
            HttpStatusCode.UnprocessableEntity, "destructive-not-acknowledged");

        await ApplyAsync(project, owner, drop.PlanHash, acknowledgeDestructive: true);
        (await TablesInAsync(project)).ShouldBe(0);
        (await OkAsync<List<TableSummaryDto>>(HttpMethod.Get, $"/api/projects/{project.Id}/tables", owner)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Drift_reports_changes_made_outside_CoreFoundry()
    {
        var (owner, project) = await ProjectAsync();
        await CreateTableAsync(project, owner, "books", Column("title", DataType.Varchar, length: 50));
        await ApplyAsync(project, owner, (await PlanAsync(project, owner)).PlanHash);
        (await OkAsync<DriftDto>(HttpMethod.Get, $"/api/projects/{project.Id}/schema/drift", owner)).Differences.ShouldBeEmpty();

        await ExecuteAsync($"ALTER TABLE `{project.DatabaseName}`.`books` MODIFY `title` VARCHAR(80) NULL, ADD COLUMN `legacy` INT");
        await ExecuteAsync($"CREATE TABLE `{project.DatabaseName}`.`manual` (`x` INT)");

        var drift = await OkAsync<DriftDto>(HttpMethod.Get, $"/api/projects/{project.Id}/schema/drift", owner);
        drift.SinceVersion.ShouldBe(1);
        drift.Differences.ShouldBe(
        [
            "Column books.title was changed outside CoreFoundry: Varchar(50) → Varchar(80).",
            "Column books.legacy was added outside CoreFoundry.",
            "Table manual was created outside CoreFoundry.",
        ], ignoreOrder: true);

        var plan = await PlanAsync(project, owner);
        plan.UnmanagedTables.ShouldBe(["manual"]);
        plan.UnmanagedColumns.ShouldBe(["books.legacy"]);
        plan.Operations.ShouldHaveSingleItem().RiskReason.ShouldNotBeNull().ShouldContain("truncates");
    }

    [Fact]
    public async Task A_crafted_name_that_skipped_validation_never_reaches_mysql()
    {
        var (owner, project) = await ProjectAsync();
        var table = await CreateTableAsync(project, owner, "books", Column("title", DataType.Text));

        // Bypass every validation layer by writing the name straight into the metadata.
        await _api.ExecuteMetadataAsync($"UPDATE `ProjectTables` SET `Name` = 'x`; DROP DATABASE mysql; --' WHERE `Id` = {table.Id}");

        (await _driver.SendAsync(HttpMethod.Get, $"/api/projects/{project.Id}/schema/plan", owner)).StatusCode
            .ShouldBe(HttpStatusCode.InternalServerError);
        (await TablesInAsync(project)).ShouldBe(0);
    }

    [Fact]
    public async Task A_warning_counts_the_nulls_before_not_null()
    {
        var (owner, project) = await ProjectAsync();
        var books = await CreateTableAsync(project, owner, "books", Column("title", DataType.Varchar, length: 50));
        await ApplyAsync(project, owner, (await PlanAsync(project, owner)).PlanHash);
        await ExecuteAsync($"INSERT INTO `{project.DatabaseName}`.`books` (`title`) VALUES (NULL), ('x')");

        books = await OkAsync<TableDto>(HttpMethod.Get, $"/api/projects/{project.Id}/tables/{books.Id}", owner);
        await OkAsync<TableDto>(HttpMethod.Put, $"/api/projects/{project.Id}/tables/{books.Id}/columns/{books.Columns[0].Id}", owner,
            new SaveColumnRequest(books.Version, "title", DataType.Varchar, 50, null, null, false, false, null));

        (await PlanAsync(project, owner)).Warnings.ShouldHaveSingleItem().ShouldContain("1 row(s) contain NULL");
    }

    private async Task<(SignedIn Owner, ProjectDto Project)> ProjectAsync()
    {
        var owner = await _driver.SignUpAsync();
        return (owner, await _driver.CreateProjectAsync(owner, "Bookshop"));
    }

    private async Task<TableDto> CreateTableAsync(ProjectDto project, SignedIn user, string name, params ColumnRequest[] columns)
    {
        var response = await _driver.SendAsync(HttpMethod.Post, $"/api/projects/{project.Id}/tables", user, new CreateTableRequest(name, columns));
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return await ReadAsync<TableDto>(response);
    }

    private Task<SchemaPlanDto> PlanAsync(ProjectDto project, SignedIn user) =>
        OkAsync<SchemaPlanDto>(HttpMethod.Get, $"/api/projects/{project.Id}/schema/plan", user);

    private Task<ApplyResultDto> ApplyAsync(ProjectDto project, SignedIn user, string hash, bool acknowledgeDestructive = false) =>
        OkAsync<ApplyResultDto>(HttpMethod.Post, $"/api/projects/{project.Id}/schema/apply", user, new ApplyRequest(hash, acknowledgeDestructive));

    private async Task<T> OkAsync<T>(HttpMethod method, string path, SignedIn user, object? body = null)
    {
        var response = await _driver.SendAsync(method, path, user, body);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return await ReadAsync<T>(response);
    }

    private static async Task ShouldBeProblemAsync(HttpResponseMessage response, HttpStatusCode status, string type)
    {
        response.StatusCode.ShouldBe(status, await response.Content.ReadAsStringAsync(Ct));
        (await ReadAsync<ProblemDetails>(response)).Type.ShouldBe($"https://corefoundry.dev/problems/{type}");
    }

    private Task<long> TablesInAsync(ProjectDto project) => _api.CountTablesInAsync(project.DatabaseName);

    private async Task<string> ShowCreateTableAsync(ProjectDto project, string table)
    {
        await using var connection = new MySqlConnection(_api.EngineConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new MySqlCommand($"SHOW CREATE TABLE `{project.DatabaseName}`.`{table}`", connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        await reader.ReadAsync(Ct);
        return reader.GetString(1);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new MySqlConnection(_api.EngineConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new MySqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<string?> ScalarAsync(string sql)
    {
        await using var connection = new MySqlConnection(_api.EngineConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new MySqlCommand(sql, connection);
        return Convert.ToString(await command.ExecuteScalarAsync(Ct), CultureInfo.InvariantCulture);
    }

    private static ColumnRequest Column(
        string name, DataType type, int? length = null, int? precision = null, int? scale = null,
        bool nullable = true, bool unique = false, string? defaultValue = null) =>
        new(name, type, length, precision, scale, nullable, unique, defaultValue);
}
