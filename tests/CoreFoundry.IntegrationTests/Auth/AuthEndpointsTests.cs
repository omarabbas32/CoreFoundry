using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CoreFoundry.Api.Auth;
using CoreFoundry.Application.Auth;
using CoreFoundry.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Shouldly;

namespace CoreFoundry.IntegrationTests.Auth;

[Collection(TestDatabaseGroup.Name)]
public sealed class AuthEndpointsTests : IDisposable
{
    private const string Password = "correct horse battery";

    private readonly TestDatabaseApi _api;
    private readonly HttpClient _client;

    public AuthEndpointsTests(TestDatabaseApi api)
    {
        _api = api;
        _api.SkipIfUnavailable();
        _client = api.CreateHttpsClient();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task Register_then_login_then_me()
    {
        var email = NewEmail();

        var register = await PostAsync("/api/auth/register", new RegisterRequest(email, Password));
        register.StatusCode.ShouldBe(HttpStatusCode.Created);

        var login = await PostAsync("/api/auth/login", new LoginRequest(email.ToUpperInvariant(), Password));
        login.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await ReadAsync<AuthResponse>(login);

        using var me = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        me.Headers.Authorization = new AuthenticationHeaderValue("Bearer", body.AccessToken);
        var meResponse = await _client.SendAsync(me, Ct);

        meResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadAsync<UserDto>(meResponse)).Email.ShouldBe(email);
    }

    [Fact]
    public async Task Refresh_cookie_is_http_only_secure_strict_and_scoped_to_auth_routes()
    {
        var response = await PostAsync("/api/auth/register", new RegisterRequest(NewEmail(), Password));

        var cookie = response.Headers.GetValues("Set-Cookie").Single(header => header.StartsWith("cf_refresh=", StringComparison.Ordinal));
        cookie.ShouldContain("httponly", Case.Insensitive);
        cookie.ShouldContain("secure", Case.Insensitive);
        cookie.ShouldContain("samesite=strict", Case.Insensitive);
        cookie.ShouldContain("path=/api/auth", Case.Insensitive);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldNotContain(RefreshCookieValue(response));
    }

    [Fact]
    public async Task Me_requires_a_token() =>
        (await _client.GetAsync(new Uri("/api/auth/me", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

    [Fact]
    public async Task Wrong_password_and_unknown_email_get_the_same_401()
    {
        var email = NewEmail();
        await PostAsync("/api/auth/register", new RegisterRequest(email, Password));

        var wrongPassword = await PostAsync("/api/auth/login", new LoginRequest(email, "not the password"));
        var unknownEmail = await PostAsync("/api/auth/login", new LoginRequest(NewEmail(), Password));

        wrongPassword.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        unknownEmail.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ReadAsync<ProblemDetails>(wrongPassword)).Detail.ShouldBe((await ReadAsync<ProblemDetails>(unknownEmail)).Detail);
    }

    [Fact]
    public async Task Duplicate_email_is_409_and_short_password_is_400()
    {
        var email = NewEmail();
        await PostAsync("/api/auth/register", new RegisterRequest(email, Password));

        (await PostAsync("/api/auth/register", new RegisterRequest(email, Password))).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var invalid = await PostAsync("/api/auth/register", new RegisterRequest(NewEmail(), "short"));
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadAsync<ValidationProblemDetails>(invalid)).Errors.Keys.ShouldContain("password");
    }

    [Fact]
    public async Task Refresh_rotates_and_the_old_cookie_stops_working()
    {
        var first = RefreshCookieValue(await PostAsync("/api/auth/register", new RegisterRequest(NewEmail(), Password)));

        var refreshed = await RefreshAsync(first);
        refreshed.StatusCode.ShouldBe(HttpStatusCode.OK);
        var second = RefreshCookieValue(refreshed);
        second.ShouldNotBe(first);

        (await RefreshAsync(first)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Replaying_a_used_cookie_signs_out_the_newer_session_too()
    {
        var first = RefreshCookieValue(await PostAsync("/api/auth/register", new RegisterRequest(NewEmail(), Password)));
        var second = RefreshCookieValue(await RefreshAsync(first));

        (await RefreshAsync(first)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized); // replay → chain revoked

        (await RefreshAsync(second)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Logout_revokes_the_cookie_and_clears_it()
    {
        var cookie = RefreshCookieValue(await PostAsync("/api/auth/register", new RegisterRequest(NewEmail(), Password)));

        using var logout = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        logout.Headers.Add("Cookie", $"{AuthController.RefreshCookieName}={cookie}");
        var response = await _client.SendAsync(logout, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        response.Headers.GetValues("Set-Cookie").ShouldContain(header => header.StartsWith("cf_refresh=;", StringComparison.Ordinal));
        (await RefreshAsync(cookie)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Refresh_without_a_cookie_is_401() =>
        (await _client.PostAsync(new Uri("/api/auth/refresh", UriKind.Relative), null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

    private static string NewEmail() => $"user-{Guid.NewGuid():N}@test.dev";

    private Task<HttpResponseMessage> PostAsync<T>(string path, T body) =>
        _client.PostAsJsonAsync(new Uri(path, UriKind.Relative), body, Ct);

    private async Task<HttpResponseMessage> RefreshAsync(string cookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh");
        request.Headers.Add("Cookie", $"{AuthController.RefreshCookieName}={cookie}");
        return await _client.SendAsync(request, Ct);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>(Ct))!;

    private static string RefreshCookieValue(HttpResponseMessage response)
    {
        var header = response.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith($"{AuthController.RefreshCookieName}=", StringComparison.Ordinal));
        return header[(AuthController.RefreshCookieName.Length + 1)..header.IndexOf(';', StringComparison.Ordinal)];
    }
}
