using CoreFoundry.Application.Auth;
using CoreFoundry.Application.Common;
using CoreFoundry.Application.Projects;
using CoreFoundry.Application.Schema;
using CoreFoundry.Application.SchemaEngine;
using CoreFoundry.Infrastructure.Auth;
using CoreFoundry.Infrastructure.Engine;
using CoreFoundry.Infrastructure.HealthChecks;
using CoreFoundry.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
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
        // cf_meta: the metadata database (EF Core).
        var metadata = RequiredConnectionString(configuration, "Metadata");
        // cf_engine: project databases cf_p_* (schema engine + Data API).
        var engine = RequiredConnectionString(configuration, "Engine");

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<TimestampsInterceptor>();
        services.AddDbContext<MetadataDbContext>((provider, options) => options
            .UseMySQL(metadata)
            .AddInterceptors(provider.GetRequiredService<TimestampsInterceptor>()));

        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        services.AddScoped<IProjectRepository, ProjectRepository>();
        services.AddScoped<ITableRepository, TableRepository>();
        services.AddSingleton<IProjectDatabaseProvisioner>(new MySqlProjectDatabaseProvisioner(engine));
        services.AddScoped<ISchemaMigrationRepository, SchemaMigrationRepository>();
        services.AddSingleton<ISqlRenderer, MySqlSqlRenderer>();
        services.AddSingleton<ISchemaIntrospector>(new MySqlSchemaIntrospector(engine));
        services.AddSingleton<ISchemaEngine>(new MySqlSchemaEngine(engine));

        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<ITokenService, JwtTokenService>();
        services.AddSingleton<IPasswordHasher, PasswordHasherAdapter>();

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
