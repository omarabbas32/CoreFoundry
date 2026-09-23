using CoreFoundry.Application.Auth;
using CoreFoundry.Domain.Users;
using Microsoft.AspNetCore.Identity;

namespace CoreFoundry.Infrastructure.Auth;

/// <summary>ASP.NET Core Identity's PBKDF2 hasher (versioned format, so parameters can be upgraded later).</summary>
internal sealed class PasswordHasherAdapter : IPasswordHasher
{
    private readonly PasswordHasher<User> _hasher = new();

    // The Identity hasher ignores the user argument; there is no per-user salt input beyond its own random salt.
    public string Hash(string password) => _hasher.HashPassword(null!, password);

    public PasswordCheck Verify(string passwordHash, string password) =>
        _hasher.VerifyHashedPassword(null!, passwordHash, password) switch
        {
            PasswordVerificationResult.Success => PasswordCheck.Success,
            PasswordVerificationResult.SuccessRehashNeeded => PasswordCheck.SuccessRehashNeeded,
            _ => PasswordCheck.Failed,
        };
}
