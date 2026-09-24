using System.Text.RegularExpressions;
using CoreFoundry.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;

namespace CoreFoundry.IntegrationTests.Infrastructure;

/// <summary>
/// The API wired to the local <c>corefoundry_test</c> database (see db/setup-test.sql).
/// Credentials come from the API's user-secrets or environment variables; the metadata database is
/// always swapped to <c>corefoundry_test</c>, so the development database is never touched.
/// Recreated (drop + migrate) once per test run.
/// </summary>
/// <remarks>
/// Project databases (<c>cf_p_{Id}</c>) live on the same MySQL server as development ones, so test
/// project ids start at <see cref="FirstProjectId"/>: tests can never create or drop a dev project's database.
/// </remarks>
public sealed partial class TestDatabaseApi : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string DatabaseName = "corefoundry_test";
    public const long FirstProjectId = 1_000_000;

    private const string SkipReason =
        "No local test database configured (ConnectionStrings:Metadata/Engine in the API's user-secrets). See README › Tests.";

    private readonly (string Metadata, string Engine)? _connections = ReadConnections();

    public bool IsAvailable => _connections is not null;

    /// <summary>Connection as <c>cf_engine</c>, for asserting on project databases.</summary>
    public string EngineConnectionString => _connections?.Engine ?? throw new InvalidOperationException(SkipReason);

    /// <summary>Call first in every test that needs the database.</summary>
    public void SkipIfUnavailable() => Assert.SkipUnless(IsAvailable, SkipReason);

    public async ValueTask InitializeAsync()
    {
        if (!IsAvailable)
        {
            return;
        }

        await using (var scope = Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetadataDbContext>();
            await db.Database.EnsureDeletedAsync();
            await db.Database.MigrateAsync();
            await db.Database.ExecuteSqlRawAsync($"ALTER TABLE `Projects` AUTO_INCREMENT = {FirstProjectId}");
        }

        await DropLeftoverProjectDatabasesAsync();
    }

    public HttpClient CreateHttpsClient() => CreateClient(new WebApplicationFactoryClientOptions
    {
        // The refresh cookie is Secure; tests manage cookies by hand to replay old ones on purpose.
        BaseAddress = new Uri("https://localhost"),
        HandleCookies = false,
    });

    public async Task<bool> ProjectDatabaseExistsAsync(string databaseName)
    {
        await using var connection = new MySqlConnection(EngineConnectionString);
        await connection.OpenAsync();
        await using var command = new MySqlCommand(
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.SCHEMATA WHERE SCHEMA_NAME = @name", connection);
        command.Parameters.AddWithValue("@name", databaseName);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    /// <summary>Number of tables in a project database (the table designer must never create any).</summary>
    public async Task<long> CountTablesInAsync(string databaseName)
    {
        await using var connection = new MySqlConnection(EngineConnectionString);
        await connection.OpenAsync();
        await using var command = new MySqlCommand(
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = @name", connection);
        command.Parameters.AddWithValue("@name", databaseName);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Stands in for the schema engine (M3): marks a draft table and its columns as applied by setting
    /// <c>AppliedName</c> directly in the metadata database.
    /// </summary>
    public async Task MarkAppliedAsync(long tableId)
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MetadataDbContext>();
        await db.Database.ExecuteSqlAsync($"UPDATE `ProjectTables` SET `AppliedName` = `Name` WHERE `Id` = {tableId}");
        await db.Database.ExecuteSqlAsync($"UPDATE `ProjectColumns` SET `AppliedName` = `Name` WHERE `TableId` = {tableId}");
    }

    /// <summary>Runs raw SQL on the metadata test database (for tests that bypass the API on purpose).</summary>
    public async Task ExecuteMetadataAsync(string sql)
    {
        await using var scope = Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<MetadataDbContext>().Database.ExecuteSqlRawAsync(sql);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Metadata", _connections?.Metadata ?? "Server=127.0.0.1;Port=1;Database=none;User=none;Password=none");
        builder.UseSetting("ConnectionStrings:Engine", _connections?.Engine ?? "Server=127.0.0.1;Port=1;User=none;Password=none");
        builder.UseSetting("Jwt:SigningKey", "integration-test-signing-key-at-least-32-bytes!");
        builder.UseSetting("RateLimiting:AuthPermitLimit", "10000");
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    /// <summary>Drops cf_p_{id} databases from earlier test runs (ids ≥ <see cref="FirstProjectId"/> only).</summary>
    private async Task DropLeftoverProjectDatabasesAsync()
    {
        await using var connection = new MySqlConnection(EngineConnectionString);
        await connection.OpenAsync();

        var leftovers = new List<string>();
        await using (var list = new MySqlCommand(
            @"SELECT SCHEMA_NAME FROM INFORMATION_SCHEMA.SCHEMATA WHERE SCHEMA_NAME LIKE 'cf\_p\_%'", connection))
        await using (var reader = await list.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var name = reader.GetString(0);
                var match = ProjectDatabaseName().Match(name);
                if (match.Success && long.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) >= FirstProjectId)
                {
                    leftovers.Add(name);
                }
            }
        }

        foreach (var name in leftovers)
        {
            await using var drop = new MySqlCommand($"DROP DATABASE IF EXISTS `{name}`", connection);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static (string Metadata, string Engine)? ReadConnections()
    {
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets<Program>(optional: true)
            .AddEnvironmentVariables()
            .Build();

        var metadata = configuration.GetConnectionString("Metadata");
        var engine = configuration.GetConnectionString("Engine");
        if (string.IsNullOrWhiteSpace(metadata) || string.IsNullOrWhiteSpace(engine))
        {
            return null;
        }

        return (new MySqlConnectionStringBuilder(metadata) { Database = DatabaseName }.ConnectionString, engine);
    }

    [GeneratedRegex(@"^cf_p_([0-9]{1,19})$", RegexOptions.CultureInvariant)]
    private static partial Regex ProjectDatabaseName();
}

[CollectionDefinition(Name)]
public sealed class TestDatabaseGroup : ICollectionFixture<TestDatabaseApi>
{
    public const string Name = "test database";
}
