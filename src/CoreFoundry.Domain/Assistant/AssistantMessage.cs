using CoreFoundry.Domain.Common;

namespace CoreFoundry.Domain.Assistant;

public enum AssistantMessageKind : byte
{
    /// <summary>From the assistant: one question, with optional suggested answers.</summary>
    Question = 1,

    /// <summary>From the user: the answer to the question before it.</summary>
    Answer = 2,

    /// <summary>From the assistant: the summary of a proposal (the proposal itself is on the session).</summary>
    Proposal = 3,

    /// <summary>From the user: what to change in the proposal before it.</summary>
    Feedback = 4,
}

/// <summary>One turn of an <see cref="AssistantSession"/>. Immutable once written.</summary>
public sealed class AssistantMessage
{
    public const int TextMaxLength = 4000;

    private AssistantMessage() { } // EF Core

    internal AssistantMessage(AssistantMessageKind kind, int sequence, string text, string? optionsJson)
    {
        Kind = Enum.IsDefined(kind) ? kind : throw new DomainException("Unknown message kind.");
        Sequence = sequence;
        Text = Guard.NotBlank(text, kind is AssistantMessageKind.Answer or AssistantMessageKind.Feedback ? "Your answer" : "Text", TextMaxLength);
        OptionsJson = optionsJson;
    }

    public long Id { get; private set; }
    public long SessionId { get; private set; }

    /// <summary>1, 2, 3… within the session.</summary>
    public int Sequence { get; private set; }

    public AssistantMessageKind Kind { get; private set; }
    public string Text { get; private set; } = null!;

    /// <summary>Suggested answers of a question as a JSON array of strings, or null.</summary>
    public string? OptionsJson { get; private set; }

    public DateTime CreatedAt { get; private set; }

    public bool FromAssistant => Kind is AssistantMessageKind.Question or AssistantMessageKind.Proposal;
}
