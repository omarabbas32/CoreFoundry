using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CoreFoundry.Api.Auth;
using CoreFoundry.Api.Projects;
using CoreFoundry.Application.Projects;
using CoreFoundry.Domain.Projects;
using Shouldly;

namespace CoreFoundry.IntegrationTests.Infrastructure;

/// <summary>A signed-up test user and their access token.</summary>
public sealed record SignedIn(long UserId, string Email, string AccessToken);

/// <summary>Talks to the API the way a client would: real routes, JSON with string enums, Bearer tokens.</summary>
public sealed class ApiDriver(HttpClient client)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async Task<SignedIn> SignUpAsync()
    {
        var email = $"user-{Guid.NewGuid():N}@test.dev";
        var response = await client.PostAsJsonAsync(
            new Uri("/api/auth/register", UriKind.Relative), new RegisterRequest(email, "correct horse battery"), Ct);
        response.EnsureSuccessStatusCode();
        var body = await ReadAsync<AuthResponse>(response);
        return new SignedIn(body.User.Id, email, body.AccessToken);
    }

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, SignedIn? user, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (user is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", user.AccessToken);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType(), options: Json);
        }

        return await client.SendAsync(request, Ct);
    }

    public async Task<ProjectDto> CreateProjectAsync(SignedIn owner, string name)
    {
        var response = await SendAsync(HttpMethod.Post, "/api/projects", owner, new CreateProjectRequest(name));
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return await ReadAsync<ProjectDto>(response);
    }

    public async Task AddMemberAsync(long projectId, SignedIn admin, SignedIn member, ProjectRole role)
    {
        var response = await SendAsync(
            HttpMethod.Post, $"/api/projects/{projectId}/members", admin, new AddMemberRequest(member.Email, role));
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    public static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>(Json, Ct))!;
}
