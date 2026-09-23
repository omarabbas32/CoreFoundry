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
/// Credentials come from the API's user-secrets or environment variables; the database name is
/// always swapped to <c>corefoundry_test</c>, so the development database is never touched.
/// Recreated (drop + migrate) once per test run.
/// </summary>
public sealed class TestDatabaseApi : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string DatabaseName = "corefoundry_test";
    private const string SkipReason =
        "No local test database configured (ConnectionStrings:Metadata in the API's user-secrets). See README › Tests.";

    private readonly string? _connectionString = BuildTestConnectionString();

    public bool IsAvailable => _connectionString is not null;

    /// <summary>Call first in every test that needs the database.</summary>
    public void SkipIfUnavailable() => Assert.SkipUnless(IsAvailable, SkipReason);

    public async ValueTask InitializeAsync()
    {
        if (!IsAvailable)
        {
            return;
        }

        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MetadataDbContext>();
        await db.Database.EnsureDeletedAsync();
        await db.Database.MigrateAsync();
    }

    public HttpClient CreateHttpsClient() => CreateClient(new WebApplicationFactoryClientOptions
    {
        // The refresh cookie is Secure; tests manage cookies by hand to replay old ones on purpose.
        BaseAddress = new Uri("https://localhost"),
        HandleCookies = false,
    });

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Metadata", _connectionString ?? "Server=127.0.0.1;Port=1;Database=none;User=none;Password=none");
        builder.UseSetting("ConnectionStrings:Engine", "Server=127.0.0.1;Port=1;User=none;Password=none");
        builder.UseSetting("Jwt:SigningKey", "integration-test-signing-key-at-least-32-bytes!");
        builder.UseSetting("RateLimiting:AuthPermitLimit", "10000");
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private static string? BuildTestConnectionString()
    {
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets<Program>(optional: true)
            .AddEnvironmentVariables()
            .Build();

        var devConnectionString = configuration.GetConnectionString("Metadata");
        if (string.IsNullOrWhiteSpace(devConnectionString))
        {
            return null;
        }

        return new MySqlConnectionStringBuilder(devConnectionString) { Database = DatabaseName }.ConnectionString;
    }
}

[CollectionDefinition(Name)]
public sealed class TestDatabaseGroup : ICollectionFixture<TestDatabaseApi>
{
    public const string Name = "test database";
}
