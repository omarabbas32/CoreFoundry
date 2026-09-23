using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace CoreFoundry.Api.Auth;

public static class RateLimitPolicies
{
    /// <summary>Login and register: a fixed window per client IP, to slow down password guessing.</summary>
    public const string Auth = "auth";

    public static IServiceCollection AddCoreFoundryRateLimiting(
        this IServiceCollection services, IConfiguration configuration)
    {
        var permitLimit = configuration.GetValue("RateLimiting:AuthPermitLimit", 10);
        var window = TimeSpan.FromSeconds(configuration.GetValue("RateLimiting:AuthWindowSeconds", 60));

        return services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(Auth, context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permitLimit,
                    Window = window,
                    QueueLimit = 0,
                }));
        });
    }
}
