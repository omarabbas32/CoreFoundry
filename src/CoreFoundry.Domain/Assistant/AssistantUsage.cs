using CoreFoundry.Domain.Common;

namespace CoreFoundry.Domain.Assistant;

/// <summary>
/// How many AI calls one user made on the server's default key on one UTC day, for the daily cap. Calls made
/// with the user's own key aren't counted. Incremented atomically by the repository.
/// </summary>
public sealed class AssistantUsage
{
    private AssistantUsage() { } // EF Core

    public AssistantUsage(long userId, DateTime day)
    {
        UserId = Guard.PositiveId(userId, nameof(UserId));
        Day = Guard.Utc(day, nameof(Day)).Date;
    }

    public long UserId { get; private set; }

    /// <summary>The UTC day, at midnight.</summary>
    public DateTime Day { get; private set; }
    public int Calls { get; private set; }
}
