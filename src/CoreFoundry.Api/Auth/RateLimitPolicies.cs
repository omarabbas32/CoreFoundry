using System.Threading.RateLimiting;
using CoreFoundry.Api.Authorization;
using Microsoft.AspNetCore.RateLimiting;

namespace CoreFoundry.Api.Auth;

public static class RateLimitPolicies
{
    /// <summary>Login and register: a fixed window per client IP, to slow down password guessing.</summary>
    public const string Auth = "auth";

    /// <summary>
    /// AI assistant calls: a fixed window per signed-in user (they cost money on the server's key). Needs the rate
    /// limiter to run after authentication; anonymous requests share one partition, but the endpoints require sign-in.
    /// </summary>
    public const string Assistant = "assistant";

    public static IServiceCollection AddCoreFoundryRateLimiting(
        this IServiceCollection services, IConfiguration configuration)
    {
        var permitLimit = configuration.GetValue("RateLimiting:AuthPermitLimit", 10);
        var window = TimeSpan.FromSeconds(configuration.GetValue("RateLimiting:AuthWindowSeconds", 60));
        var assistantLimit = configuration.GetValue("RateLimiting:AssistantPermitLimit", 20);
        var assistantWindow = TimeSpan.FromSeconds(configuration.GetValue("RateLimiting:AssistantWindowSeconds", 60));

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
            options.AddPolicy(Assistant, context => RateLimitPartition.GetFixedWindowLimiter(
                context.User.GetUserId()?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "anonymous",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = assistantLimit,
                    Window = assistantWindow,
                    QueueLimit = 0,
                }));
        });
    }
}
