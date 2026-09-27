using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoreFoundry.Application.Assistant;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoreFoundry.Infrastructure.Ai;

/// <summary>
/// An OpenAI-compatible <c>chat/completions</c> API (Groq by default, see <see cref="AiOptions"/>) with structured
/// output: <c>response_format: json_schema</c>, strict, so the reply is JSON of the requested shape. A model that
/// refuses that format (400) is asked once more in JSON mode with the schema in the prompt; the assistant checks the
/// shape either way. The key is sent as a bearer token and never logged or put in an error message.
/// </summary>
public sealed partial class OpenAiCompatibleChatClient(HttpClient http, IOptions<AiOptions> options, ILogger<OpenAiCompatibleChatClient> logger)
    : IAiChatClient
{
    public async Task<string> CompleteAsync(AiChatRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var settings = options.Value;
        var key = !string.IsNullOrWhiteSpace(request.ApiKey) ? request.ApiKey : settings.ApiKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new AiProviderException(AiFailure.NotConfigured, "The AI assistant isn't set up: add your own Groq key, or ask the administrator to set a default one.");
        }

        var (status, text) = await SendAsync(settings, key, Body(settings.Model, request, strictSchema: true), cancellationToken);
        string? refusal = null;
        if (status == HttpStatusCode.BadRequest && RefusedSchemaFormat(ProviderError(text)))
        {
            refusal = ProviderError(text);
            LogSchemaRefused(logger, settings.Model, refusal);
            (status, text) = await SendAsync(settings, key, Body(settings.Model, request, strictSchema: false), cancellationToken);
        }

        if (status == HttpStatusCode.OK)
        {
            return Content(text);
        }

        // The model wrote something that isn't the JSON asked for. That's the model's mistake, not the request's:
        // hand its attempt to the caller, whose repair loop tells the model what's wrong and asks again.
        if (status == HttpStatusCode.BadRequest && FailedGeneration(text) is { } attempt)
        {
            LogGenerationFailed(logger, settings.Model, ProviderError(text));
            return attempt;
        }

        LogRequestFailed(logger, settings.Model, (int)status, ProviderError(text));
        var detail = ProviderError(text);
        throw Failed(status, request.ApiKey is not null, refusal is null ? detail : $"{detail} (after refusing the strict schema: {refusal})");
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Model} refused the strict JSON schema ({Error}); asking again in JSON mode.")]
    private static partial void LogSchemaRefused(ILogger logger, string model, string? error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Model} produced JSON that didn't validate ({Error}); returning it for repair.")]
    private static partial void LogGenerationFailed(ILogger logger, string model, string? error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Model} request failed with {Status}: {Error}")]
    private static partial void LogRequestFailed(ILogger logger, string model, int status, string? error);

    /// <summary>
    /// What the model generated when the provider couldn't validate it as JSON (Groq's <c>failed_generation</c>), or
    /// null. Never empty: an empty attempt is nothing to repair.
    /// </summary>
    private static string? FailedGeneration(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("failed_generation", out var generation)
                && generation.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(generation.GetString())
                    ? generation.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<(HttpStatusCode Status, string Text)> SendAsync(
        AiOptions settings, string key, JsonObject body, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(settings.BaseUrl, "chat/completions"))
        {
            Content = JsonContent.Create(body),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        try
        {
            using var response = await http.SendAsync(message, cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            return (response.IsSuccessStatusCode ? HttpStatusCode.OK : response.StatusCode, text);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AiProviderException(AiFailure.Unavailable, "The AI didn't answer in time. Try again.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new AiProviderException(AiFailure.Unavailable, "The AI service couldn't be reached. Try again.", ex);
        }
    }

    /// <param name="strictSchema">
    /// True: <c>json_schema</c> with <c>strict</c>. False: <c>json_object</c> mode, with the schema appended to the
    /// system message so the model still knows the shape.
    /// </param>
    private static JsonObject Body(string model, AiChatRequest request, bool strictSchema)
    {
        var messages = request.Messages.Select((message, index) => (JsonNode)new JsonObject
        {
            ["role"] = message.Role switch
            {
                AiChatRole.System => "system",
                AiChatRole.User => "user",
                AiChatRole.Assistant => "assistant",
                _ => throw new ArgumentOutOfRangeException(nameof(request), message.Role, null),
            },
            ["content"] = !strictSchema && index == 0 && message.Role == AiChatRole.System
                ? $"{message.Content}\n\nReply with one JSON object that follows this JSON Schema exactly:\n{request.JsonSchema}"
                : message.Content,
        });

        return new JsonObject
        {
            ["model"] = model,
            ["temperature"] = 0.2,
            ["messages"] = new JsonArray([.. messages]),
            ["response_format"] = strictSchema
                ? new JsonObject
                {
                    ["type"] = "json_schema",
                    ["json_schema"] = new JsonObject
                    {
                        ["name"] = request.SchemaName,
                        ["strict"] = true,
                        ["schema"] = JsonNode.Parse(request.JsonSchema),
                    },
                }
                : new JsonObject { ["type"] = "json_object" },
        };
    }

    /// <summary>True when a 400 is about the structured-output format (the model doesn't support it), not the request.</summary>
    private static bool RefusedSchemaFormat(string? error) =>
        error is not null
        && (error.Contains("response_format", StringComparison.OrdinalIgnoreCase)
            || error.Contains("json_schema", StringComparison.OrdinalIgnoreCase)
            || error.Contains("structured output", StringComparison.OrdinalIgnoreCase));

    /// <summary>The first choice's message text: the JSON the model wrote.</summary>
    private static string Content(string responseText)
    {
        try
        {
            using var document = JsonDocument.Parse(responseText);
            var content = document.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
            return string.IsNullOrWhiteSpace(content)
                ? throw new AiProviderException(AiFailure.BadResponse, "The AI sent an empty answer. Try again.")
                : content;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or IndexOutOfRangeException or InvalidOperationException)
        {
            throw new AiProviderException(AiFailure.BadResponse, "The AI sent an answer CoreFoundry couldn't read. Try again.", ex);
        }
    }

    private static AiProviderException Failed(HttpStatusCode status, bool ownKey, string? detail) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new(
            AiFailure.KeyRejected,
            ownKey ? "Groq rejected your key. Check it, or remove it to use CoreFoundry's key." : "Groq rejected the server's key. Ask the administrator to check it."),
        HttpStatusCode.TooManyRequests => new(AiFailure.RateLimited, "The AI is busy or the key's limit is reached. Wait a moment and try again."),
        >= HttpStatusCode.InternalServerError => new(AiFailure.Unavailable, "The AI service is unavailable right now. Try again."),
        _ => new(AiFailure.Unavailable, $"The AI service refused the request ({(int)status}){(detail is null ? "." : $": {detail}")}"),
    };

    /// <summary>The provider's own error message, if it sent one (it never contains the key).</summary>
    private static string? ProviderError(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("error", out var error))
            {
                return null;
            }

            return error.ValueKind == JsonValueKind.String ? error.GetString()
                : error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var nested) ? nested.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
