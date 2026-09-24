using System.Text.Json;
using System.Text.Json.Serialization;
using CoreFoundry.Api.Auth;
using CoreFoundry.Api.Authorization;
using CoreFoundry.Api.Errors;
using CoreFoundry.Api.Projects;
using CoreFoundry.Application;
using CoreFoundry.Infrastructure;
using CoreFoundry.Infrastructure.Auth;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers()
    .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<JwtOptions>>((bearer, jwt) =>
    {
        bearer.TokenValidationParameters = jwt.Value.CreateValidationParameters();
        bearer.MapInboundClaims = false; // keep "sub"/"email" as-is
    });
builder.Services.AddHttpContextAccessor();
builder.Services.AddProjectAuthorization();
builder.Services.AddCoreFoundryRateLimiting(builder.Configuration);
builder.Services.AddHostedService<ProjectRecoveryService>();

const string WebCorsPolicy = "web";
builder.Services.AddCors(options => options.AddPolicy(WebCorsPolicy, policy => policy
    .WithOrigins(builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [])
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));

// The web app proxies /api to this service, so the client IP arrives in X-Forwarded-For.
// Only loopback proxies are trusted by default, so a remote caller can't spoof its IP.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);

var app = builder.Build();

app.UseForwardedHeaders();

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCors(WebCorsPolicy);
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// Liveness: the process is up. No dependencies, so it works without a database.
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false,
    ResponseWriter = WriteHealthResponse,
});

// Readiness: both MySQL accounts can connect.
app.MapHealthChecks("/health", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains(CoreFoundry.Infrastructure.DependencyInjection.ReadyTag),
    ResponseWriter = WriteHealthResponse,
});

app.MapControllers();

app.Run();

static Task WriteHealthResponse(HttpContext context, HealthReport report)
{
    context.Response.ContentType = "application/json";
    return JsonSerializer.SerializeAsync(context.Response.Body, new
    {
        status = report.Status.ToString(),
        checks = report.Entries.ToDictionary(
            entry => entry.Key,
            entry => new { status = entry.Value.Status.ToString(), description = entry.Value.Description }),
    }, JsonSerializerOptions.Web);
}

// Exposed for WebApplicationFactory<Program> in integration tests.
public partial class Program;
