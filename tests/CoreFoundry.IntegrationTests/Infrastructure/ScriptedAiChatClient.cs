using System.Collections.Concurrent;
using System.Text.Json;
using CoreFoundry.Application.Assistant;

namespace CoreFoundry.IntegrationTests.Infrastructure;

/// <summary>
/// Stands in for Grok in integration tests: each call takes the next scripted reply (JSON, or an exception to
/// throw) and records the request. Tests in the database collection run one at a time, so each one calls
/// <see cref="Reset"/> first.
/// </summary>
public sealed class ScriptedAiChatClient : IAiChatClient
{
    private readonly ConcurrentQueue<Func<string>> _replies = new();
    private readonly ConcurrentQueue<AiChatRequest> _requests = new();

    public IReadOnlyList<AiChatRequest> Requests => [.. _requests];

    public void Reset()
    {
        _replies.Clear();
        _requests.Clear();
    }

    public ScriptedAiChatClient Reply(object reply)
    {
        var json = JsonSerializer.Serialize(reply, AssistantPrompt.Json);
        _replies.Enqueue(() => json);
        return this;
    }

    public ScriptedAiChatClient Question(string question, params string[] options) =>
        Reply(new AssistantReply(AssistantReply.QuestionKind, question, options, null, [], []));

    public ScriptedAiChatClient Proposal(string summary, IReadOnlyList<ProposedTable> tables, IReadOnlyList<ProposedColumnAddition>? columns = null) =>
        Reply(new AssistantReply(AssistantReply.ProposalKind, null, [], summary, tables, columns ?? []));

    public ScriptedAiChatClient Fail(AiFailure failure, string message)
    {
        _replies.Enqueue(() => throw new AiProviderException(failure, message));
        return this;
    }

    public Task<string> CompleteAsync(AiChatRequest request, CancellationToken cancellationToken)
    {
        _requests.Enqueue(request);
        return _replies.TryDequeue(out var next)
            ? Task.FromResult(next())
            : throw new InvalidOperationException("The test scripted no more AI replies.");
    }
}
