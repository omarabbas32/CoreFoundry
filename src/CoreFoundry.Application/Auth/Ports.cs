using CoreFoundry.Domain.Users;

namespace CoreFoundry.Application.Auth;

public interface IUserRepository
{
    Task<User?> FindByIdAsync(long id, CancellationToken cancellationToken);

    /// <param name="normalizedEmail">Output of <see cref="User.NormalizeEmail"/>.</param>
    Task<User?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken);

    Task<bool> EmailExistsAsync(string normalizedEmail, CancellationToken cancellationToken);

    void Add(User user);
}

public interface IRefreshTokenRepository
{
    Task<RefreshToken?> FindByHashAsync(string tokenHash, CancellationToken cancellationToken);

    /// <summary>
    /// Every token that replaced <paramref name="token"/>, directly or transitively, oldest first
    /// (the rest of its rotation chain).
    /// </summary>
    Task<IReadOnlyList<RefreshToken>> ListSuccessorsAsync(RefreshToken token, CancellationToken cancellationToken);

    void Add(RefreshToken token);
}

public enum PasswordCheck
{
    Failed,
    Success,
    /// <summary>Correct, but hashed with outdated parameters; re-hash and save.</summary>
    SuccessRehashNeeded,
}

public interface IPasswordHasher
{
    string Hash(string password);

    PasswordCheck Verify(string passwordHash, string password);
}

/// <summary>A signed access token (JWT).</summary>
public sealed record AccessToken(string Value, DateTime ExpiresAt);

/// <summary>A fresh refresh token: <see cref="RawValue"/> goes to the client, only <see cref="Hash"/> is stored.</summary>
public sealed record IssuedRefreshToken(string RawValue, string Hash, DateTime ExpiresAt);

public interface ITokenService
{
    AccessToken CreateAccessToken(User user);

    IssuedRefreshToken CreateRefreshToken();

    /// <summary>The same hash <see cref="CreateRefreshToken"/> stores, for looking a presented token up.</summary>
    string HashRefreshToken(string rawValue);
}
