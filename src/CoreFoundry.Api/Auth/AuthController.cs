using CoreFoundry.Api.Authorization;
using CoreFoundry.Application.Auth;
using CoreFoundry.Application.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace CoreFoundry.Api.Auth;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(AuthService auth) : ControllerBase
{
    public const string RefreshCookieName = "cf_refresh";
    public const string RefreshCookiePath = "/api/auth";

    [HttpPost("register")]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public async Task<ActionResult<AuthResponse>> Register(RegisterRequest request, CancellationToken cancellationToken)
    {
        var result = await auth.RegisterAsync(request.Email, request.Password, cancellationToken);
        SetRefreshCookie(result.RefreshToken);
        return Created(new Uri("/api/auth/me", UriKind.Relative), AuthResponse.From(result));
    }

    [HttpPost("login")]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public async Task<ActionResult<AuthResponse>> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        var result = await auth.LoginAsync(request.Email, request.Password, cancellationToken);
        SetRefreshCookie(result.RefreshToken);
        return AuthResponse.From(result);
    }

    [HttpPost("refresh")]
    public async Task<ActionResult<AuthResponse>> Refresh(CancellationToken cancellationToken)
    {
        try
        {
            var result = await auth.RefreshAsync(Request.Cookies[RefreshCookieName], cancellationToken);
            SetRefreshCookie(result.RefreshToken);
            return AuthResponse.From(result);
        }
        catch (AuthenticationFailedException)
        {
            // A rejected token is useless to the browser; stop it from being sent again.
            DeleteRefreshCookie();
            throw;
        }
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        await auth.LogoutAsync(Request.Cookies[RefreshCookieName], cancellationToken);
        DeleteRefreshCookie();
        return NoContent();
    }

    [Authorize]
    [HttpGet("me")]
    public async Task<ActionResult<UserDto>> Me(CancellationToken cancellationToken)
    {
        if (User.GetUserId() is not long userId)
        {
            return Unauthorized();
        }

        return await auth.GetCurrentUserAsync(userId, cancellationToken);
    }

    private void SetRefreshCookie(IssuedRefreshToken token) =>
        Response.Cookies.Append(RefreshCookieName, token.RawValue, CookieOptions(token.ExpiresAt));

    private void DeleteRefreshCookie() =>
        Response.Cookies.Delete(RefreshCookieName, CookieOptions(expires: null));

    private static CookieOptions CookieOptions(DateTime? expires) => new()
    {
        HttpOnly = true,
        Secure = true, // browsers treat http://localhost as secure, so this works in development too
        SameSite = SameSiteMode.Strict,
        Path = RefreshCookiePath,
        Expires = expires,
        IsEssential = true,
    };
}
