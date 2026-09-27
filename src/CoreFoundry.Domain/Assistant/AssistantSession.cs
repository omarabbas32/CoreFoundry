using CoreFoundry.Domain.Common;

namespace CoreFoundry.Domain.Assistant;

public enum AssistantSessionStatus : byte
{
    /// <summary>Waiting for the user's answer to the last question.</summary>
    Asking = 1,

    /// <summary>A proposal is waiting to be confirmed or changed.</summary>
    Proposed = 2,

    /// <summary>The proposal was written into the draft schema. Closed.</summary>
    Confirmed = 3,

    /// <summary>Stopped by the user. Closed.</summary>
    Cancelled = 4,
}

/// <summary>
/// One conversation with the AI schema assistant (M10): questions and answers, one at a time, until it
/// proposes schema changes that the user confirms. Every turn is kept, so a session can be resumed and
/// shows why the schema looks the way it does. <see cref="Version"/> is the concurrency token.
/// </summary>
public sealed class AssistantSession
{
    public const int GoalMaxLength = 1000;

    /// <summary>After this many questions the assistant must propose.</summary>
    public const int MaxQuestions = 8;

    private readonly List<AssistantMessage> _messages = [];

    private AssistantSession() { } // EF Core

    public AssistantSession(long projectId, long userId, string goal)
    {
        ProjectId = Guard.PositiveId(projectId, nameof(ProjectId));
        UserId = Guard.PositiveId(userId, nameof(UserId));
        Goal = Guard.NotBlank(goal, "Goal", GoalMaxLength);
        Status = AssistantSessionStatus.Asking;
        Version = 1;
    }

    public long Id { get; private set; }
    public long ProjectId { get; private set; }

    /// <summary>Who started it; null once that account is gone.</summary>
    public long? UserId { get; private set; }

    public string Goal { get; private set; } = null!;
    public AssistantSessionStatus Status { get; private set; }

    /// <summary>The latest proposal as JSON (the Application layer's shape), or null before the first one.</summary>
    public string? ProposalJson { get; private set; }

    public int QuestionCount { get; private set; }
    public int Version { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    /// <summary>Oldest first.</summary>
    public IReadOnlyList<AssistantMessage> Messages => [.. _messages.OrderBy(message => message.Sequence)];

    public bool IsOpen => Status is AssistantSessionStatus.Asking or AssistantSessionStatus.Proposed;

    /// <summary>True once the question budget is spent: the next assistant turn must be a proposal.</summary>
    public bool MustPropose => QuestionCount >= MaxQuestions;

    /// <summary>The assistant asks one question; <paramref name="optionsJson"/> holds its suggested answers.</summary>
    public void Ask(string question, string? optionsJson)
    {
        if (Status != AssistantSessionStatus.Asking)
        {
            throw new DomainException("The assistant can only ask while the session is waiting for answers.");
        }

        if (MustPropose)
        {
            throw new DomainException($"The assistant has asked {MaxQuestions} questions already and must propose now.");
        }

        if (LastKind == AssistantMessageKind.Question)
        {
            throw new DomainException("The last question hasn't been answered yet.");
        }

        Add(AssistantMessageKind.Question, question, optionsJson);
        QuestionCount++;
    }

    /// <summary>The user answers the last question.</summary>
    public void Answer(string answer)
    {
        if (Status != AssistantSessionStatus.Asking || LastKind != AssistantMessageKind.Question)
        {
            throw new DomainException("There is no question waiting for an answer.");
        }

        Add(AssistantMessageKind.Answer, answer, null);
    }

    /// <summary>The assistant proposes schema changes (<paramref name="summary"/> is what the user reads).</summary>
    public void Propose(string summary, string proposalJson)
    {
        if (Status != AssistantSessionStatus.Asking || LastKind == AssistantMessageKind.Question)
        {
            throw new DomainException("The assistant can only propose after the user's last message.");
        }

        ProposalJson = string.IsNullOrWhiteSpace(proposalJson) ? throw new DomainException("The proposal is required.") : proposalJson;
        Add(AssistantMessageKind.Proposal, summary, null);
        Status = AssistantSessionStatus.Proposed;
    }

    /// <summary>The user asks for changes to the proposal; the assistant then asks again or proposes again.</summary>
    public void RequestChanges(string feedback)
    {
        if (Status != AssistantSessionStatus.Proposed)
        {
            throw new DomainException("There is no proposal to change.");
        }

        Add(AssistantMessageKind.Feedback, feedback, null);
        Status = AssistantSessionStatus.Asking;
    }

    public void Confirm()
    {
        if (Status != AssistantSessionStatus.Proposed)
        {
            throw new DomainException("There is no proposal to confirm.");
        }

        Status = AssistantSessionStatus.Confirmed;
        Version++;
    }

    public void Cancel()
    {
        if (!IsOpen)
        {
            throw new DomainException("This conversation is already closed.");
        }

        Status = AssistantSessionStatus.Cancelled;
        Version++;
    }

    private AssistantMessageKind? LastKind => _messages.Count == 0 ? null : _messages.MaxBy(message => message.Sequence)!.Kind;

    private void Add(AssistantMessageKind kind, string text, string? optionsJson)
    {
        _messages.Add(new AssistantMessage(kind, _messages.Count + 1, text, optionsJson));
        Version++;
    }
}
