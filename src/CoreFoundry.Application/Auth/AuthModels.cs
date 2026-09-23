namespace CoreFoundry.Application.Auth;

public sealed record UserDto(long Id, string Email);

/// <summary>Result of register / login / refresh. The API sends <see cref="RefreshToken"/> only as a cookie.</summary>
public sealed record AuthResult(AccessToken AccessToken, IssuedRefreshToken RefreshToken, UserDto User);
