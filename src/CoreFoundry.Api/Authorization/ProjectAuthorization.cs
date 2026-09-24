using System.Globalization;
using System.Security.Claims;
using CoreFoundry.Application.Projects;
using CoreFoundry.Domain.Projects;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.JsonWebTokens;

namespace CoreFoundry.Api.Authorization;

/// <summary>Policy names for routes under <c>/api/projects/{projectId}</c>.</summary>
public static class ProjectPolicies
{
    public const string Developer = "Project.Developer";
    public const string Admin = "Project.Admin";
    public const string Owner = "Project.Owner";

    /// <summary>Route value every project-scoped route must use.</summary>
    public const string ProjectIdRouteKey = "projectId";

    public static IServiceCollection AddProjectAuthorization(this IServiceCollection services)
    {
        services.AddScoped<IAuthorizationHandler, ProjectRoleHandler>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, ProjectAuthorizationResultHandler>();

        services.AddAuthorizationBuilder()
            .AddPolicy(Developer, policy => policy.RequireAuthenticatedUser().AddRequirements(new ProjectRoleRequirement(ProjectRole.Developer)))
            .AddPolicy(Admin, policy => policy.RequireAuthenticatedUser().AddRequirements(new ProjectRoleRequirement(ProjectRole.Admin)))
            .AddPolicy(Owner, policy => policy.RequireAuthenticatedUser().AddRequirements(new ProjectRoleRequirement(ProjectRole.Owner)));

        return services;
    }
}

/// <summary>The caller must be a member of the route's project with at least <see cref="Minimum"/>.</summary>
public sealed record ProjectRoleRequirement(ProjectRole Minimum) : IAuthorizationRequirement;

/// <summary>
/// Reads the caller's role from <c>ProjectMembers</c> on every request, so a role change or removal
/// takes effect immediately (roles are deliberately not in the JWT).
/// </summary>
internal sealed class ProjectRoleHandler(IHttpContextAccessor httpContextAccessor, ProjectService projects)
    : AuthorizationHandler<ProjectRoleRequirement>
{
    /// <summary>Set when the caller isn't a member, so the result handler answers 404 instead of 403.</summary>
    public const string NotMemberItemKey = "cf:project-not-member";

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, ProjectRoleRequirement requirement)
    {
        var httpContext = httpContextAccessor.HttpContext;
        if (httpContext is null
            || context.User.GetUserId() is not long userId
            || !TryGetProjectId(httpContext, out var projectId))
        {
            return;
        }

        var role = await projects.GetMemberRoleAsync(projectId, userId, httpContext.RequestAborted);
        if (role is null)
        {
            httpContext.Items[NotMemberItemKey] = true;
            return;
        }

        if (role.Value.AtLeast(requirement.Minimum))
        {
            context.Succeed(requirement);
        }
    }

    private static bool TryGetProjectId(HttpContext httpContext, out long projectId) =>
        long.TryParse(
            httpContext.GetRouteValue(ProjectPolicies.ProjectIdRouteKey) as string,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out projectId);
}

/// <summary>Non-members get 404 (not 403) so project ids can't be probed; everything else is the default.</summary>
internal sealed class ProjectAuthorizationResultHandler(IProblemDetailsService problemDetails)
    : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(
        RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Forbidden && context.Items.ContainsKey(ProjectRoleHandler.NotMemberItemKey))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await problemDetails.WriteAsync(new ProblemDetailsContext
            {
                HttpContext = context,
                ProblemDetails = new ProblemDetails { Status = StatusCodes.Status404NotFound, Title = "Project not found." },
            });
            return;
        }

        await _default.HandleAsync(next, context, policy, authorizeResult);
    }
}

public static class ClaimsPrincipalExtensions
{
    /// <summary>The user id from the JWT <c>sub</c> claim, or null if missing or malformed.</summary>
    public static long? GetUserId(this ClaimsPrincipal user) =>
        long.TryParse(user.FindFirstValue(JwtRegisteredClaimNames.Sub), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? id
            : null;
}
