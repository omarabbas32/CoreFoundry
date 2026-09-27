using System.ComponentModel.DataAnnotations;

namespace CoreFoundry.Infrastructure.Ai;

/// <summary>
/// Bound from the <c>Ai</c> configuration section: any OpenAI-compatible chat API, Groq by default.
/// <see cref="ApiKey"/> is the server's default key, used for users who haven't added their own; set it with
/// user-secrets, or leave it empty to require users' own keys.
/// </summary>
public sealed class AiOptions
{
    public const string SectionName = "Ai";

    /// <summary>The default key (user-secrets or the <c>Ai__ApiKey</c> environment variable). Empty: no default.</summary>
    public string? ApiKey { get; set; }

    /// <summary>A model that supports structured output (JSON schema) on the provider.</summary>
    [Required]
    public string Model { get; set; } = "openai/gpt-oss-120b";

    /// <summary>The API root that <c>chat/completions</c> is under, with a trailing slash.</summary>
    [Required]
    public Uri BaseUrl { get; set; } = new("https://api.groq.com/openai/v1/");

    [Range(5, 300)]
    public int TimeoutSeconds { get; set; } = 90;

    /// <summary>AI calls a user may make per UTC day on the default key. Users' own keys aren't capped here.</summary>
    [Range(1, 10_000)]
    public int DailyCallsPerUser { get; set; } = 60;
}
