using CoreFoundry.Application.Auth;
using CoreFoundry.Application.Projects;
using CoreFoundry.Application.Schema;
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
        return services;
    }
}
