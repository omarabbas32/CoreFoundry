using CoreFoundry.Application.Auth;
using CoreFoundry.Application.Common;
using CoreFoundry.Domain.Users;

namespace CoreFoundry.Application.Assistant;

/// <param name="HasOwnKey">The user added their own xAI key; it is used for their assistant calls.</param>
/// <param name="Hint">The own key's last characters, e.g. <c>"a1b2"</c>. The key itself is never returned.</param>
/// <param name="HasDefaultKey">The server has a key to fall back on (capped per day).</param>
public sealed record AiKeyStatusDto(bool HasOwnKey, string? Hint, bool HasDefaultKey, int DailyCallsOnDefaultKey);

/// <summary>A user's own xAI key: stored encrypted, shown only as its last characters.</summary>
public sealed class AiKeyService(IUserRepository users, IAiKeyProtector protector, AssistantSettings settings, IUnitOfWork unitOfWork)
{
    public const int MinKeyLength = 20;
    public const int MaxKeyLength = 512;

    public async Task<AiKeyStatusDto> GetAsync(long userId, CancellationToken cancellationToken) =>
        Status(await FindAsync(userId, cancellationToken));

    public async Task<AiKeyStatusDto> SetAsync(long userId, string? apiKey, CancellationToken cancellationToken)
    {
        var key = apiKey?.Trim() ?? string.Empty;
        if (key.Length is < MinKeyLength or > MaxKeyLength || key.Any(char.IsWhiteSpace))
        {
            throw new ValidationFailedException("apiKey", $"Paste the whole xAI key: {MinKeyLength} to {MaxKeyLength} characters, no spaces.");
        }

        var user = await FindAsync(userId, cancellationToken);
        user.SetAiKey(protector.Protect(key), key[^User.AiKeyHintLength..]);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Status(user);
    }

    public async Task<AiKeyStatusDto> RemoveAsync(long userId, CancellationToken cancellationToken)
    {
        var user = await FindAsync(userId, cancellationToken);
        user.RemoveAiKey();
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Status(user);
    }

    private AiKeyStatusDto Status(User user) =>
        new(user.AiKeyCiphertext is not null, user.AiKeyHint, settings.HasDefaultKey, settings.DailyCallsPerUser);

    private async Task<User> FindAsync(long userId, CancellationToken cancellationToken) =>
        await users.FindByIdAsync(userId, cancellationToken) ?? throw new NotFoundException("User not found.");
}
