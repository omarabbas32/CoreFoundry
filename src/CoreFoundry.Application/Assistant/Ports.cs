using CoreFoundry.Domain.Assistant;

namespace CoreFoundry.Application.Assistant;

public interface IAssistantSessionRepository
{
    // Conversations are private: every query is for one user's own conversations in one project.

    /// <summary>The user's session with its messages, or null if it isn't theirs or isn't in this project.</summary>
    Task<AssistantSession?> FindAsync(long projectId, long userId, long sessionId, CancellationToken cancellationToken);

    /// <summary>The user's open session (Asking or Proposed) in the project, with its messages, if any.</summary>
    Task<AssistantSession?> FindOpenAsync(long projectId, long userId, CancellationToken cancellationToken);

    /// <summary>The user's sessions in the project, newest first, without their messages.</summary>
    Task<IReadOnlyList<AssistantSession>> ListAsync(long projectId, long userId, int take, CancellationToken cancellationToken);

    void Add(AssistantSession session);
}

public interface IAssistantUsageRepository
{
    /// <summary>
    /// Counts one call for the user on <paramref name="dayUtc"/> if they have made fewer than <paramref name="limit"/>
    /// that day. Atomic: parallel requests can't exceed the limit. Saves immediately (not part of the unit of work).
    /// </summary>
    /// <returns>False when the limit is reached; nothing is counted then.</returns>
    Task<bool> TryConsumeAsync(long userId, DateTime dayUtc, int limit, CancellationToken cancellationToken);
}

public enum AiChatRole
{
    System,
    User,
    Assistant,
}

public sealed record AiChatMessage(AiChatRole Role, string Content);

/// <param name="ApiKey">The user's own key, or null for the server's default key.</param>
/// <param name="SchemaName">Name of the structured-output schema.</param>
/// <param name="JsonSchema">The JSON Schema the reply must follow (a JSON document).</param>
public sealed record AiChatRequest(string? ApiKey, IReadOnlyList<AiChatMessage> Messages, string SchemaName, string JsonSchema);

/// <summary>A chat model that answers with JSON following a schema. Implemented for OpenAI-compatible APIs (Groq) in Infrastructure.</summary>
public interface IAiChatClient
{
    /// <returns>The reply's JSON text (not yet validated against the proposal rules).</returns>
    /// <exception cref="AiProviderException">The provider refused, failed or timed out.</exception>
    Task<string> CompleteAsync(AiChatRequest request, CancellationToken cancellationToken);
}

/// <summary>Encrypts users' own AI keys at rest.</summary>
public interface IAiKeyProtector
{
    string Protect(string apiKey);

    /// <exception cref="AiProviderException">The ciphertext can't be read any more (for example the encryption keys were lost).</exception>
    string Unprotect(string ciphertext);
}

/// <param name="HasDefaultKey">True if the server has its own key to fall back on.</param>
/// <param name="DailyCallsPerUser">AI calls a user may make per UTC day on the server's key (their own key is not capped).</param>
public sealed record AssistantSettings(bool HasDefaultKey, int DailyCallsPerUser);

public enum AiFailure
{
    /// <summary>No key: the user hasn't added one and the server has none.</summary>
    NotConfigured,

    /// <summary>The provider rejected the key (401/403).</summary>
    KeyRejected,

    /// <summary>The provider's rate limit, or the user's daily cap on the server's key.</summary>
    RateLimited,

    /// <summary>Timeout, network error or 5xx.</summary>
    Unavailable,

    /// <summary>The reply wasn't usable (not JSON of the expected shape, even after retries).</summary>
    BadResponse,
}

/// <summary>An AI call that failed; the API maps <see cref="Failure"/> to a status code with <see cref="Exception.Message"/> as detail.</summary>
public sealed class AiProviderException : Exception
{
    public AiProviderException(AiFailure failure, string message, Exception? inner = null)
        : base(message, inner) => Failure = failure;

    public AiProviderException()
    {
    }

    public AiProviderException(string message)
        : base(message)
    {
    }

    public AiProviderException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public AiFailure Failure { get; }
}
