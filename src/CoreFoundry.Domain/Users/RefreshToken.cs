using CoreFoundry.Domain.Common;

namespace CoreFoundry.Domain.Users;

/// <summary>
/// One refresh token in a rotation chain. Only the SHA-256 hash of the token is stored;
/// the raw value exists only in the client's cookie.
/// </summary>
public sealed class RefreshToken
{
    /// <summary>SHA-256 as lower-case hex.</summary>
    public const int TokenHashLength = 64;

    private RefreshToken() { } // EF Core

    public RefreshToken(long userId, string tokenHash, DateTime expiresAt)
    {
        UserId = Guard.PositiveId(userId, nameof(UserId));
        TokenHash = tokenHash is { Length: TokenHashLength } && tokenHash.All(char.IsAsciiHexDigitLower)
            ? tokenHash
            : throw new DomainException("TokenHash must be a lower-case hex SHA-256 (64 characters).");
        ExpiresAt = Guard.Utc(expiresAt, nameof(ExpiresAt));
    }

    public long Id { get; private set; }
    public long UserId { get; private set; }
    public string TokenHash { get; private set; } = null!;
    public DateTime ExpiresAt { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? RevokedAt { get; private set; }
    public long? ReplacedByTokenId { get; private set; }

    /// <summary>The token that replaced this one during rotation (null if revoked by logout or theft detection).</summary>
    public RefreshToken? ReplacedByToken { get; private set; }

    public bool IsRevoked => RevokedAt is not null;

    public bool IsActive(DateTime utcNow) => !IsRevoked && utcNow < ExpiresAt;

    /// <summary>Revokes this token. Pass <paramref name="replacement"/> when rotating.</summary>
    public void Revoke(DateTime utcNow, RefreshToken? replacement = null)
    {
        if (IsRevoked)
        {
            throw new DomainException("Token is already revoked.");
        }

        RevokedAt = Guard.Utc(utcNow, nameof(utcNow));
        ReplacedByToken = replacement;
    }
}
