using CoreFoundry.Api.Auth;
using CoreFoundry.Api.Authorization;
using CoreFoundry.Application.Assistant;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace CoreFoundry.Api.Projects;

public sealed record StartAssistantRequest(string Goal);

/// <summary><see cref="Version"/> is the conversation's version the caller last saw; a stale one gets 409.</summary>
public sealed record AssistantAnswerRequest(int Version, string Text);

public sealed record AssistantFeedbackRequest(int Version, string Feedback);

public sealed record AssistantVersionRequest(int Version);

public sealed record AiKeyRequest(string ApiKey);

/// <summary>
/// The AI schema assistant (M10): an interview that ends in a proposal the user confirms into draft tables.
/// Calls that reach the model are rate limited per user; they can take a while (the model may be retried).
/// </summary>
[ApiController]
[Route("api/projects/{projectId:long}/assistant/sessions")]
[Authorize(Policy = ProjectPolicies.Developer)]
public sealed class AssistantController(AssistantService assistant) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<AssistantSessionSummaryDto>> List(long projectId, CancellationToken cancellationToken) =>
        assistant.ListAsync(projectId, cancellationToken);

    [HttpGet("{sessionId:long}")]
    public Task<AssistantSessionDto> Get(long projectId, long sessionId, CancellationToken cancellationToken) =>
        assistant.GetAsync(projectId, sessionId, cancellationToken);

    /// <summary>Starts a conversation from a one-line goal; answers with the first question. 409 if one is open.</summary>
    [HttpPost]
    [EnableRateLimiting(RateLimitPolicies.Assistant)]
    public async Task<ActionResult<AssistantSessionDto>> Start(long projectId, StartAssistantRequest request, CancellationToken cancellationToken) =>
        StatusCode(StatusCodes.Status201Created, await assistant.StartAsync(projectId, UserId, request.Goal, cancellationToken));

    [HttpPost("{sessionId:long}/answers")]
    [EnableRateLimiting(RateLimitPolicies.Assistant)]
    public Task<AssistantSessionDto> Answer(long projectId, long sessionId, AssistantAnswerRequest request, CancellationToken cancellationToken) =>
        assistant.AnswerAsync(projectId, sessionId, UserId, request.Version, request.Text, cancellationToken);

    /// <summary>Asks for changes to the proposal.</summary>
    [HttpPost("{sessionId:long}/revise")]
    [EnableRateLimiting(RateLimitPolicies.Assistant)]
    public Task<AssistantSessionDto> Revise(long projectId, long sessionId, AssistantFeedbackRequest request, CancellationToken cancellationToken) =>
        assistant.ReviseAsync(projectId, sessionId, UserId, request.Version, request.Feedback, cancellationToken);

    /// <summary>Retries the assistant's turn after a failed call.</summary>
    [HttpPost("{sessionId:long}/continue")]
    [EnableRateLimiting(RateLimitPolicies.Assistant)]
    public Task<AssistantSessionDto> Continue(long projectId, long sessionId, AssistantVersionRequest request, CancellationToken cancellationToken) =>
        assistant.ContinueAsync(projectId, sessionId, UserId, request.Version, cancellationToken);

    /// <summary>Creates the proposal's tables and columns as drafts. Nothing reaches MySQL until the plan is applied.</summary>
    [HttpPost("{sessionId:long}/confirm")]
    public Task<ConfirmedProposalDto> Confirm(long projectId, long sessionId, AssistantVersionRequest request, CancellationToken cancellationToken) =>
        assistant.ConfirmAsync(projectId, sessionId, request.Version, cancellationToken);

    [HttpPost("{sessionId:long}/cancel")]
    public Task<AssistantSessionDto> Cancel(long projectId, long sessionId, AssistantVersionRequest request, CancellationToken cancellationToken) =>
        assistant.CancelAsync(projectId, sessionId, request.Version, cancellationToken);

    /// <summary>The policy guarantees a signed-in member, so the id is there.</summary>
    private long UserId => User.GetUserId() ?? throw new InvalidOperationException("The signed-in user has no id.");
}

/// <summary>The signed-in user's own AI (Groq) key for the assistant. The key is write-only: responses show its last characters.</summary>
[ApiController]
[Route("api/me/ai-key")]
[Authorize]
public sealed class AiKeyController(AiKeyService keys) : ControllerBase
{
    [HttpGet]
    public Task<AiKeyStatusDto> Get(CancellationToken cancellationToken) => keys.GetAsync(UserId, cancellationToken);

    [HttpPut]
    [EnableRateLimiting(RateLimitPolicies.Assistant)]
    public Task<AiKeyStatusDto> Set(AiKeyRequest request, CancellationToken cancellationToken) =>
        keys.SetAsync(UserId, request.ApiKey, cancellationToken);

    [HttpDelete]
    public Task<AiKeyStatusDto> Remove(CancellationToken cancellationToken) => keys.RemoveAsync(UserId, cancellationToken);

    private long UserId => User.GetUserId() ?? throw new InvalidOperationException("The signed-in user has no id.");
}
