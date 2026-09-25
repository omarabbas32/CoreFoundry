using CoreFoundry.Application.Auth;
using CoreFoundry.Application.Data;
using CoreFoundry.Application.Export;
using CoreFoundry.Application.Projects;
using CoreFoundry.Application.Schema;
using CoreFoundry.Application.SchemaEngine;
using CoreFoundry.Application.Templates;
using Microsoft.Extensions.DependencyInjection;

namespace CoreFoundry.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<AuthService>();
        services.AddScoped<ProjectService>();
        services.AddScoped<MemberService>();
        services.AddScoped<TableService>();
        services.AddScoped<SchemaPlanService>();
        services.AddScoped<SchemaApplier>();
        services.AddScoped<DataService>();
        services.AddScoped<ExportService>();
        services.AddScoped<TemplateService>();
        return services;
    }
}
