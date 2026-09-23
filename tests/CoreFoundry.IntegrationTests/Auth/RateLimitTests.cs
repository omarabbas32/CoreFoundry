using System.Net;
using System.Net.Http.Json;
using CoreFoundry.Api.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;

namespace CoreFoundry.IntegrationTests.Auth;

/// <summary>The limiter rejects before any database work, so this needs no database.</summary>
public sealed class RateLimitTests(RateLimitTests.StrictLimitApi api) : IClassFixture<RateLimitTests.StrictLimitApi>
{
    [Fact]
    public async Task Third_login_attempt_in_the_window_gets_429()
    {
        using var client = api.CreateClient();
        var request = new LoginRequest("nobody@test.dev", "whatever-password");
        var ct = TestContext.Current.CancellationToken;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            (await client.PostAsJsonAsync(new Uri("/api/auth/login", UriKind.Relative), request, ct))
                .StatusCode.ShouldNotBe(HttpStatusCode.TooManyRequests);
        }

        (await client.PostAsJsonAsync(new Uri("/api/auth/login", UriKind.Relative), request, ct))
            .StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    public sealed class StrictLimitApi : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // No reachable database: the two allowed attempts fail with 500, which is fine for this test.
            builder.UseSetting("ConnectionStrings:Metadata", "Server=127.0.0.1;Port=1;Database=none;User=none;Password=none");
            builder.UseSetting("ConnectionStrings:Engine", "Server=127.0.0.1;Port=1;User=none;Password=none");
            builder.UseSetting("Jwt:SigningKey", "rate-limit-test-signing-key-at-least-32-bytes!");
            builder.UseSetting("RateLimiting:AuthPermitLimit", "2");
        }
    }
}
