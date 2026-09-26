using System.ComponentModel.DataAnnotations;

namespace CoreFoundry.Infrastructure.Ai;

/// <summary>
/// Bound from the <c>Grok</c> configuration section. <see cref="ApiKey"/> is the server's default xAI key, used
/// for users who haven't added their own; set it with user-secrets, or leave it empty to require users' own keys.
/// </summary>
public sealed class GrokOptions
{
    public const string SectionName = "Grok";

    /// <summary>The default key (user-secrets or the <c>Grok__ApiKey</c> environment variable). Empty: no default.</summary>
    public string? ApiKey { get; set; }

    [Required]
    public string Model { get; set; } = "grok-4";

    [Required]
    public Uri BaseUrl { get; set; } = new("https://api.x.ai/v1/");

    [Range(5, 300)]
    public int TimeoutSeconds { get; set; } = 90;

    /// <summary>AI calls a user may make per UTC day on the default key. Users' own keys aren't capped here.</summary>
    [Range(1, 10_000)]
    public int DailyCallsPerUser { get; set; } = 60;
}
