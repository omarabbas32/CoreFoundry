using System.Text.Json;
using CoreFoundry.Application.Auth;
using CoreFoundry.Application.Common;
using CoreFoundry.Application.Projects;
using CoreFoundry.Application.Schema;
using CoreFoundry.Application.SchemaEngine;
using CoreFoundry.Application.Templates;
using CoreFoundry.Domain.Assistant;
using CoreFoundry.Domain.Common;

namespace CoreFoundry.Application.Assistant;

public sealed record AssistantMessageDto(long Id, int Sequence, AssistantMessageKind Kind, string Text, IReadOnlyList<string> Options, DateTime CreatedAt);

/// <param name="Proposal">The latest proposal while <see cref="Status"/> is Proposed or Confirmed; otherwise null.</param>
/// <param name="AwaitingAssistant">True when the user spoke last and the assistant's turn failed: <c>continue</c> retries it.</param>
public sealed record AssistantSessionDto(
    long Id,
    string Goal,
    AssistantSessionStatus Status,
    int Version,
    int QuestionCount,
    int MaxQuestions,
    bool AwaitingAssistant,
    IReadOnlyList<AssistantMessageDto> Messages,
    SchemaProposal? Proposal,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record AssistantSessionSummaryDto(long Id, string Goal, AssistantSessionStatus Status, DateTime CreatedAt, DateTime UpdatedAt);

public sealed record ConfirmedProposalDto(AssistantSessionDto Session, IReadOnlyList<TableSummaryDto> Tables);

/// <summary>
/// The AI schema assistant (M10): an interview, one question at a time, then a proposal the user confirms into
/// draft tables. Each user message is saved before the model is called, so a failed call loses nothing and can be
/// retried with <see cref="ContinueAsync"/>. The model's replies are data: a proposal must pass
/// <see cref="ProposalValidator"/>, and invalid replies are sent back to the model to fix (<see cref="MaxAttempts"/>).
/// </summary>
public sealed class AssistantService(
    IProjectRepository projects,
    ITableRepository tables,
    IAssistantSessionRepository sessions,
    IAssistantUsageRepository usage,
    IUserRepository users,
    IAiChatClient ai,
    IAiKeyProtector keys,
    AssistantSettings settings,
    DraftSchemaWriter writer,
    TableService tableService,
    IUnitOfWork unitOfWork,
    TimeProvider time)
{
    /// <summary>Model calls per assistant turn: the first answer plus up to two repairs.</summary>
    public const int MaxAttempts = 3;

    public const int MaxOptions = 5;
    public const int OptionMaxLength = 200;

    public async Task<IReadOnlyList<AssistantSessionSummaryDto>> ListAsync(long projectId, CancellationToken cancellationToken)
    {
        await SchemaPlanService.VisibleProjectAsync(projects, projectId, cancellationToken);
        return [.. (await sessions.ListAsync(projectId, 50, cancellationToken))
            .Select(session => new AssistantSessionSummaryDto(session.Id, session.Goal, session.Status, session.CreatedAt, session.UpdatedAt))];
    }

    public async Task<AssistantSessionDto> GetAsync(long projectId, long sessionId, CancellationToken cancellationToken) =>
        ToDto(await FindAsync(projectId, sessionId, cancellationToken));

    /// <summary>Starts a conversation and gets the first question (or, if the goal says enough, a proposal).</summary>
    /// <exception cref="ConflictException">The project already has an open conversation.</exception>
    public async Task<AssistantSessionDto> StartAsync(long projectId, long userId, string goal, CancellationToken cancellationToken)
    {
        await SchemaPlanService.VisibleProjectAsync(projects, projectId, cancellationToken);
        if (await sessions.FindOpenAsync(projectId, cancellationToken) is not null)
        {
            throw new ConflictException("This project already has an open conversation with the assistant. Finish or cancel it first.");
        }

        var session = Validated(() => new AssistantSession(projectId, userId, goal), "goal");
        sessions.Add(session);
        await SaveAsync(cancellationToken);
        return await TurnAsync(session, userId, cancellationToken);
    }

    public async Task<AssistantSessionDto> AnswerAsync(
        long projectId, long sessionId, long userId, int version, string answer, CancellationToken cancellationToken)
    {
        var session = await FindForChangeAsync(projectId, sessionId, version, cancellationToken);
        Validated(() => session.Answer(answer), "text");
        await SaveAsync(cancellationToken);
        return await TurnAsync(session, userId, cancellationToken);
    }

    /// <summary>Asks for changes to the proposal; the assistant asks one question or proposes again.</summary>
    public async Task<AssistantSessionDto> ReviseAsync(
        long projectId, long sessionId, long userId, int version, string feedback, CancellationToken cancellationToken)
    {
        var session = await FindForChangeAsync(projectId, sessionId, version, cancellationToken);
        Validated(() => session.RequestChanges(feedback), "feedback");
        await SaveAsync(cancellationToken);
        return await TurnAsync(session, userId, cancellationToken);
    }

    /// <summary>Retries the assistant's turn after a failed call (the user's message is already saved).</summary>
    public async Task<AssistantSessionDto> ContinueAsync(
        long projectId, long sessionId, long userId, int version, CancellationToken cancellationToken)
    {
        var session = await FindForChangeAsync(projectId, sessionId, version, cancellationToken);
        if (!AwaitingAssistant(session))
        {
            throw new ConflictException("The assistant isn't waiting to answer: answer its question, or ask for changes to its proposal.");
        }

        return await TurnAsync(session, userId, cancellationToken);
    }

    /// <summary>
    /// Writes the proposal into the draft schema (drafts only: the user reviews the plan and applies it as usual).
    /// The proposal is checked again against the current drafts, which may have changed since it was made.
    /// </summary>
    public async Task<ConfirmedProposalDto> ConfirmAsync(
        long projectId, long sessionId, int version, CancellationToken cancellationToken)
    {
        var session = await FindForChangeAsync(projectId, sessionId, version, cancellationToken);
        var proposal = session.Status == AssistantSessionStatus.Proposed ? Proposal(session)! : throw new ConflictException("There is no proposal to confirm.");
        var problems = ProposalValidator.Validate(proposal, await tables.ListAsync(projectId, cancellationToken));
        if (problems.Count > 0)
        {
            throw new ConflictException(
                $"The tables changed since this proposal, so it no longer fits: {string.Join(" ", problems)} Ask for changes to get a new proposal.");
        }

        // Tables first: the writer saves as it goes, so the session is marked Confirmed only once they all exist.
        await writer.WriteAsync(
            projectId,
            [.. proposal.NewTables.Select(table => table.ToTemplate())],
            [.. proposal.NewColumns.Select(addition => new ExistingTableColumn(addition.Table, addition.Column.ToTemplate()))],
            cancellationToken);
        session.Confirm();
        await SaveAsync(cancellationToken);
        return new ConfirmedProposalDto(ToDto(session), await tableService.ListAsync(projectId, cancellationToken));
    }

    public async Task<AssistantSessionDto> CancelAsync(long projectId, long sessionId, int version, CancellationToken cancellationToken)
    {
        var session = await FindForChangeAsync(projectId, sessionId, version, cancellationToken);
        Validated(session.Cancel, "version");
        await SaveAsync(cancellationToken);
        return ToDto(session);
    }

    /// <summary>
    /// One assistant turn: call the model with the whole conversation and the current drafts; if its reply can't be
    /// used, tell it why and call again. Saves the question or proposal it ends with.
    /// </summary>
    private async Task<AssistantSessionDto> TurnAsync(AssistantSession session, long userId, CancellationToken cancellationToken)
    {
        var apiKey = await ApiKeyAsync(userId, cancellationToken);
        var project = await SchemaPlanService.VisibleProjectAsync(projects, session.ProjectId, cancellationToken);
        var existing = await tables.ListAsync(session.ProjectId, cancellationToken);
        var messages = Conversation(session, project.Name, existing);

        var problems = (IReadOnlyList<string>)[];
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            if (apiKey is null && !await usage.TryConsumeAsync(userId, time.GetUtcNow().UtcDateTime, settings.DailyCallsPerUser, cancellationToken))
            {
                throw new AiProviderException(
                    AiFailure.RateLimited,
                    $"You've used today's {settings.DailyCallsPerUser} assistant calls on CoreFoundry's key. Add your own xAI key to continue, or try again tomorrow.");
            }

            var json = await ai.CompleteAsync(
                new AiChatRequest(apiKey, messages, AssistantPrompt.SchemaName, AssistantPrompt.ReplySchema), cancellationToken);
            var (reply, errors) = Interpret(json, session, existing);
            if (reply is not null)
            {
                Apply(session, reply);
                await SaveAsync(cancellationToken);
                return ToDto(session);
            }

            problems = errors;
            messages.Add(new AiChatMessage(AiChatRole.Assistant, json));
            messages.Add(new AiChatMessage(AiChatRole.User,
                $"That reply can't be used: {string.Join(" ", errors)} Fix these problems and send the whole reply again."));
        }

        throw new AiProviderException(
            AiFailure.BadResponse,
            $"Grok's answer still had problems after {MaxAttempts} tries: {string.Join(" ", problems)} Try again, or rephrase your last message.");
    }

    /// <summary>The key for this user's calls: their own if they added one, else null for the server's default.</summary>
    private async Task<string?> ApiKeyAsync(long userId, CancellationToken cancellationToken)
    {
        var user = await users.FindByIdAsync(userId, cancellationToken) ?? throw new NotFoundException("User not found.");
        if (user.AiKeyCiphertext is { } ciphertext)
        {
            return keys.Unprotect(ciphertext);
        }

        return settings.HasDefaultKey
            ? null
            : throw new AiProviderException(AiFailure.NotConfigured, "The AI assistant isn't set up: add your own xAI key in the account menu.");
    }

    private static List<AiChatMessage> Conversation(AssistantSession session, string projectName, IReadOnlyList<Domain.Schema.ProjectTable> existing)
    {
        var messages = new List<AiChatMessage>
        {
            new(AiChatRole.System, AssistantPrompt.System),
            new(AiChatRole.User, AssistantPrompt.Context(projectName, session.Goal, existing)),
        };
        foreach (var message in session.Messages)
        {
            messages.Add(message.Kind switch
            {
                AssistantMessageKind.Question => new(AiChatRole.Assistant, JsonSerializer.Serialize(
                    new AssistantReply(AssistantReply.QuestionKind, message.Text, Options(message), null, [], []), AssistantPrompt.Json)),
                AssistantMessageKind.Proposal => new(AiChatRole.Assistant, $"(Proposal sent to the user) {message.Text}"),
                AssistantMessageKind.Feedback => new(AiChatRole.User, $"Change the proposal: {message.Text}"),
                _ => new(AiChatRole.User, message.Text),
            });
        }

        if (session.MustPropose)
        {
            messages.Add(new(AiChatRole.User, $"You have asked {AssistantSession.MaxQuestions} questions, the maximum. Reply with kind=\"proposal\" now."));
        }

        return messages;
    }

    /// <summary>Parses and checks a reply. Returns it when usable, or the problems to send back to the model.</summary>
    private static (AssistantReply? Reply, IReadOnlyList<string> Errors) Interpret(
        string json, AssistantSession session, IReadOnlyList<Domain.Schema.ProjectTable> existing)
    {
        AssistantReply? reply;
        try
        {
            reply = JsonSerializer.Deserialize<AssistantReply>(json, AssistantPrompt.Json);
        }
        catch (JsonException ex)
        {
            return (null, [$"It isn't JSON of the required shape ({ex.Message})."]);
        }

        switch (reply?.Kind)
        {
            case AssistantReply.QuestionKind when session.MustPropose:
                return (null, [$"You have asked {AssistantSession.MaxQuestions} questions already; send a proposal instead."]);
            case AssistantReply.QuestionKind when string.IsNullOrWhiteSpace(reply.Question):
                return (null, ["A question reply needs the question text."]);
            case AssistantReply.QuestionKind:
                return (reply, []);
            case AssistantReply.ProposalKind when string.IsNullOrWhiteSpace(reply.Summary):
                return (null, ["A proposal needs a summary."]);
            case AssistantReply.ProposalKind:
                var errors = ProposalValidator.Validate(ToProposal(reply), existing);
                return errors.Count == 0 ? (reply, []) : (null, errors);
            default:
                return (null, ["kind must be \"question\" or \"proposal\"."]);
        }
    }

    private static void Apply(AssistantSession session, AssistantReply reply)
    {
        if (reply.Kind == AssistantReply.QuestionKind)
        {
            var options = (reply.Options ?? [])
                .Where(option => !string.IsNullOrWhiteSpace(option))
                .Select(option => Clip(option.Trim(), OptionMaxLength))
                .Distinct(StringComparer.Ordinal)
                .Take(MaxOptions)
                .ToList();
            session.Ask(Clip(reply.Question!.Trim(), AssistantMessage.TextMaxLength), options.Count == 0 ? null : JsonSerializer.Serialize(options));
            return;
        }

        var proposal = ToProposal(reply);
        session.Propose(Clip(proposal.Summary, AssistantMessage.TextMaxLength), JsonSerializer.Serialize(proposal, AssistantPrompt.Json));
    }

    private static SchemaProposal ToProposal(AssistantReply reply) =>
        new(reply.Summary?.Trim() ?? string.Empty, reply.NewTables ?? [], reply.NewColumns ?? []);

    private static string Clip(string text, int maxLength) => text.Length <= maxLength ? text : text[..maxLength];

    private static bool AwaitingAssistant(AssistantSession session)
    {
        var messages = session.Messages;
        return session.Status == AssistantSessionStatus.Asking
            && (messages.Count == 0 || messages[^1].Kind is AssistantMessageKind.Answer or AssistantMessageKind.Feedback);
    }

    private static SchemaProposal? Proposal(AssistantSession session) =>
        session.Status is AssistantSessionStatus.Proposed or AssistantSessionStatus.Confirmed && session.ProposalJson is { } json
            ? JsonSerializer.Deserialize<SchemaProposal>(json, AssistantPrompt.Json)
            : null;

    private static List<string> Options(AssistantMessage message) =>
        message.OptionsJson is { } json ? JsonSerializer.Deserialize<List<string>>(json) ?? [] : [];

    private static AssistantSessionDto ToDto(AssistantSession session) => new(
        session.Id,
        session.Goal,
        session.Status,
        session.Version,
        session.QuestionCount,
        AssistantSession.MaxQuestions,
        AwaitingAssistant(session),
        [.. session.Messages.Select(message => new AssistantMessageDto(
            message.Id, message.Sequence, message.Kind, message.Text, Options(message), message.CreatedAt))],
        Proposal(session),
        session.CreatedAt,
        session.UpdatedAt);

    private async Task<AssistantSession> FindAsync(long projectId, long sessionId, CancellationToken cancellationToken)
    {
        await SchemaPlanService.VisibleProjectAsync(projects, projectId, cancellationToken);
        return await sessions.FindAsync(projectId, sessionId, cancellationToken)
            ?? throw new NotFoundException("Conversation not found.");
    }

    private async Task<AssistantSession> FindForChangeAsync(long projectId, long sessionId, int version, CancellationToken cancellationToken)
    {
        var session = await FindAsync(projectId, sessionId, cancellationToken);
        return session.Version == version ? session : throw StaleVersion();
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyConflictException ex)
        {
            throw new ConflictException(StaleVersionMessage, ex);
        }
    }

    /// <summary>Runs a domain operation, turning a rule violation into a 400 on <paramref name="field"/>.</summary>
    private static T Validated<T>(Func<T> operation, string field)
    {
        try
        {
            return operation();
        }
        catch (DomainException ex)
        {
            throw new ValidationFailedException(ex.Field ?? field, ex.Message);
        }
    }

    private static void Validated(Action operation, string field) =>
        Validated(() =>
        {
            operation();
            return true;
        }, field);

    private const string StaleVersionMessage = "This conversation changed in another tab. Reload it to see the latest messages.";

    private static ConflictException StaleVersion() => new(StaleVersionMessage);
}
