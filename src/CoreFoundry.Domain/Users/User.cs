using CoreFoundry.Domain.Common;

namespace CoreFoundry.Domain.Users;

public sealed class User
{
    public const int EmailMaxLength = 254;
    public const int PasswordHashMaxLength = 255;
    public const int AiKeyHintLength = 4;

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

    /// <summary>The user's own AI (Groq) key, encrypted by the Infrastructure layer; null: the server's default key is used.</summary>
    public string? AiKeyCiphertext { get; private set; }

    /// <summary>The key's last characters, so the user can tell which key is set. Never enough to use it.</summary>
    public string? AiKeyHint { get; private set; }

    public void SetAiKey(string ciphertext, string hint)
    {
        AiKeyCiphertext = string.IsNullOrWhiteSpace(ciphertext) ? throw new DomainException("The encrypted key is required.") : ciphertext;
        AiKeyHint = hint.Length == AiKeyHintLength ? hint : throw new DomainException($"The key hint must be {AiKeyHintLength} characters.");
    }

    public void RemoveAiKey()
    {
        AiKeyCiphertext = null;
        AiKeyHint = null;
    }

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
