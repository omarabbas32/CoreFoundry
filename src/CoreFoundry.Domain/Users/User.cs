using CoreFoundry.Domain.Common;

namespace CoreFoundry.Domain.Users;

public sealed class User
{
    public const int EmailMaxLength = 254;
    public const int PasswordHashMaxLength = 255;

    private User() { } // EF Core

    public User(string email, string passwordHash)
    {
        Email = NormalizeEmail(email);
        PasswordHash = Guard.NotBlank(passwordHash, nameof(PasswordHash), PasswordHashMaxLength);
    }

    public long Id { get; private set; }
    public string Email { get; private set; } = null!;
    public string PasswordHash { get; private set; } = null!;
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    public void ChangePasswordHash(string passwordHash) =>
        PasswordHash = Guard.NotBlank(passwordHash, nameof(PasswordHash), PasswordHashMaxLength);

    /// <summary>Trimmed and lower-cased, so lookups and the unique index are case-insensitive.</summary>
    public static string NormalizeEmail(string email)
    {
        var normalized = Guard.NotBlank(email, "Email", EmailMaxLength).ToLowerInvariant();
        var at = normalized.IndexOf('@', StringComparison.Ordinal);
        if (at <= 0 || at != normalized.LastIndexOf('@') || at == normalized.Length - 1)
        {
            throw new DomainException("Email is not a valid address.");
        }

        return normalized;
    }
}
