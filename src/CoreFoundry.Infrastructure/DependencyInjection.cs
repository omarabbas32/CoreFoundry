using CoreFoundry.Infrastructure.HealthChecks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CoreFoundry.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Tag for checks that must pass before the API can serve traffic.</summary>
    public const string ReadyTag = "ready";

    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services, IConfiguration configuration)
    {
        // cf_meta: the metadata database (EF Core from M1).
        var metadata = RequiredConnectionString(configuration, "Metadata");
        // cf_engine: project databases cf_p_* (schema engine + Data API).
        var engine = RequiredConnectionString(configuration, "Engine");

        services.AddHealthChecks()
            .AddCheck("mysql-metadata", new MySqlConnectionHealthCheck(metadata), tags: [ReadyTag])
            .AddCheck("mysql-engine", new MySqlConnectionHealthCheck(engine), tags: [ReadyTag]);

        return services;
    }

    private static string RequiredConnectionString(IConfiguration configuration, string name) =>
        configuration.GetConnectionString(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(
                $"Connection string '{name}' is missing. Set it with " +
                $"`dotnet user-secrets set \"ConnectionStrings:{name}\" \"...\"` " +
                "in src/CoreFoundry.Api (see README).");
}
