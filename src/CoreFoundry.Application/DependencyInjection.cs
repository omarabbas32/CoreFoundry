using CoreFoundry.Application.Auth;
using CoreFoundry.Application.Projects;
using Microsoft.Extensions.DependencyInjection;

namespace CoreFoundry.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<AuthService>();
        services.AddScoped<ProjectService>();
        return services;
    }
}
