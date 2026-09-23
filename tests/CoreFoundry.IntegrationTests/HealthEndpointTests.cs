using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;

namespace CoreFoundry.IntegrationTests;

public class HealthEndpointTests(HealthEndpointTests.ApiFactory factory)
    : IClassFixture<HealthEndpointTests.ApiFactory>
{
    [Fact]
    public async Task Liveness_returns_200_without_a_database()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// Supplies placeholder connection strings so the app starts; liveness never uses them.
    /// Readiness tests against a real MySQL come with the test container setup later.
    /// </summary>
    public sealed class ApiFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("ConnectionStrings:Metadata", "Server=127.0.0.1;Port=1;Database=none;User=none;Password=none");
            builder.UseSetting("ConnectionStrings:Engine", "Server=127.0.0.1;Port=1;User=none;Password=none");
            builder.UseSetting("Jwt:SigningKey", "health-test-signing-key-at-least-32-bytes!");
        }
    }
}
