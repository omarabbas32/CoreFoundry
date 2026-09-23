using CoreFoundry.Application.Auth;

namespace CoreFoundry.Api.Auth;

public sealed record RegisterRequest(string Email, string Password);

public sealed record LoginRequest(string Email, string Password);

/// <summary>The refresh token is never in the body; it travels only in the <c>cf_refresh</c> cookie.</summary>
public sealed record AuthResponse(string AccessToken, DateTime ExpiresAt, UserDto User)
{
    public static AuthResponse From(AuthResult result) =>
        new(result.AccessToken.Value, result.AccessToken.ExpiresAt, result.User);
}
