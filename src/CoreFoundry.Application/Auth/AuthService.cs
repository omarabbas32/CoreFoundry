using CoreFoundry.Application.Common;
using CoreFoundry.Domain.Common;
using CoreFoundry.Domain.Users;

namespace CoreFoundry.Application.Auth;

public sealed class AuthService(
    IUserRepository users,
    IRefreshTokenRepository refreshTokens,
    IUnitOfWork unitOfWork,
    IPasswordHasher passwordHasher,
    ITokenService tokenService,
    TimeProvider timeProvider)
{
    // NIST 800-63B: length over composition rules. The upper bound caps hashing cost.
    public const int PasswordMinLength = 10;
    public const int PasswordMaxLength = 128;

    public async Task<AuthResult> RegisterAsync(string email, string password, CancellationToken cancellationToken)
    {
        var normalizedEmail = ValidateEmail(email);
        ValidatePassword(password);

        if (await users.EmailExistsAsync(normalizedEmail, cancellationToken))
        {
            throw new ConflictException("An account with this email already exists.");
        }

        var user = new User(normalizedEmail, passwordHasher.Hash(password));
        users.Add(user);
        await unitOfWork.SaveChangesAsync(cancellationToken); // assigns user.Id

        return await IssueTokensAsync(user, cancellationToken);
    }

    public async Task<AuthResult> LoginAsync(string email, string password, CancellationToken cancellationToken)
    {
        string normalizedEmail;
        try
        {
            normalizedEmail = User.NormalizeEmail(email);
        }
        catch (DomainException)
        {
            throw new AuthenticationFailedException();
        }

        var user = await users.FindByEmailAsync(normalizedEmail, cancellationToken);
        if (user is null)
        {
            // Spend the same time as a real check so response timing doesn't reveal which emails exist.
            _ = passwordHasher.Hash(password ?? string.Empty);
            throw new AuthenticationFailedException();
        }

        switch (passwordHasher.Verify(user.PasswordHash, password ?? string.Empty))
        {
            case PasswordCheck.Failed:
                throw new AuthenticationFailedException();
            case PasswordCheck.SuccessRehashNeeded:
                user.ChangePasswordHash(passwordHasher.Hash(password!));
                break;
        }

        return await IssueTokensAsync(user, cancellationToken);
    }

    /// <summary>
    /// Rotates a refresh token. Presenting an already-revoked token is treated as theft:
    /// every newer token in its chain is revoked, signing out both the attacker and the user.
    /// </summary>
    public async Task<AuthResult> RefreshAsync(string? rawRefreshToken, CancellationToken cancellationToken)
    {
        var current = await FindPresentedTokenAsync(rawRefreshToken, cancellationToken)
            ?? throw new AuthenticationFailedException("Refresh token is invalid.");
        var now = UtcNow;

        if (current.IsRevoked)
        {
            await RevokeChainAfterAsync(current, now, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            throw new AuthenticationFailedException("Refresh token was already used.");
        }

        if (!current.IsActive(now))
        {
            throw new AuthenticationFailedException("Refresh token has expired.");
        }

        var user = await users.FindByIdAsync(current.UserId, cancellationToken)
            ?? throw new AuthenticationFailedException("Refresh token is invalid.");

        var issued = tokenService.CreateRefreshToken();
        var replacement = new RefreshToken(user.Id, issued.Hash, issued.ExpiresAt);
        refreshTokens.Add(replacement);
        current.Revoke(now, replacement);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            // Another request rotated this token a moment earlier (e.g. two tabs). Not theft: just reject.
            throw new AuthenticationFailedException("Refresh token was already used.");
        }

        return new AuthResult(tokenService.CreateAccessToken(user), issued, ToDto(user));
    }

    /// <summary>Revokes the presented token if it is still active. Always succeeds.</summary>
    public async Task LogoutAsync(string? rawRefreshToken, CancellationToken cancellationToken)
    {
        var token = await FindPresentedTokenAsync(rawRefreshToken, cancellationToken);
        if (token is null || !token.IsActive(UtcNow))
        {
            return;
        }

        token.Revoke(UtcNow);
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            // Already revoked by a concurrent request; the outcome is the same.
        }
    }

    public async Task<UserDto> GetCurrentUserAsync(long userId, CancellationToken cancellationToken)
    {
        var user = await users.FindByIdAsync(userId, cancellationToken)
            ?? throw new AuthenticationFailedException("User no longer exists.");
        return ToDto(user);
    }

    private DateTime UtcNow => timeProvider.GetUtcNow().UtcDateTime;

    private async Task<AuthResult> IssueTokensAsync(User user, CancellationToken cancellationToken)
    {
        var issued = tokenService.CreateRefreshToken();
        refreshTokens.Add(new RefreshToken(user.Id, issued.Hash, issued.ExpiresAt));
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new AuthResult(tokenService.CreateAccessToken(user), issued, ToDto(user));
    }

    private async Task<RefreshToken?> FindPresentedTokenAsync(string? rawValue, CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(rawValue)
            ? null
            : await refreshTokens.FindByHashAsync(tokenService.HashRefreshToken(rawValue), cancellationToken);

    private async Task RevokeChainAfterAsync(RefreshToken reused, DateTime now, CancellationToken cancellationToken)
    {
        foreach (var successor in await refreshTokens.ListSuccessorsAsync(reused, cancellationToken))
        {
            if (!successor.IsRevoked)
            {
                successor.Revoke(now);
            }
        }
    }

    private static string ValidateEmail(string email)
    {
        try
        {
            return User.NormalizeEmail(email);
        }
        catch (DomainException ex)
        {
            throw new ValidationFailedException("email", ex.Message);
        }
    }

    private static void ValidatePassword(string password)
    {
        if (password is null || password.Length < PasswordMinLength)
        {
            throw new ValidationFailedException("password", $"Password must be at least {PasswordMinLength} characters.");
        }

        if (password.Length > PasswordMaxLength)
        {
            throw new ValidationFailedException("password", $"Password must be at most {PasswordMaxLength} characters.");
        }
    }

    private static UserDto ToDto(User user) => new(user.Id, user.Email);
}
