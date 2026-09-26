using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoreFoundry.Application.Assistant;
using Microsoft.Extensions.Options;

namespace CoreFoundry.Infrastructure.Ai;

/// <summary>
/// xAI Grok through its OpenAI-compatible <c>chat/completions</c> endpoint, with structured output
/// (<c>response_format: json_schema</c>, strict) so the reply is JSON of the requested shape. The key is sent as a
/// bearer token and never logged or put in an error message.
/// </summary>
public sealed class GrokChatClient(HttpClient http, IOptions<GrokOptions> options) : IAiChatClient
{
    public async Task<string> CompleteAsync(AiChatRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var settings = options.Value;
        var key = !string.IsNullOrWhiteSpace(request.ApiKey) ? request.ApiKey : settings.ApiKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new AiProviderException(AiFailure.NotConfigured, "The AI assistant isn't set up: add your own xAI key, or ask the administrator to set a default one.");
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(settings.BaseUrl, "chat/completions"))
        {
            Content = JsonContent.Create(Body(settings.Model, request)),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(message, cancellationToken);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AiProviderException(AiFailure.Unavailable, "Grok didn't answer in time. Try again.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new AiProviderException(AiFailure.Unavailable, "Grok couldn't be reached. Try again.", ex);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw Failed(response.StatusCode, request.ApiKey is not null, ProviderError(text));
            }

            return Content(text);
        }
    }

    private static JsonObject Body(string model, AiChatRequest request) => new()
    {
        ["model"] = model,
        ["temperature"] = 0.2,
        ["messages"] = new JsonArray([.. request.Messages.Select(message => (JsonNode)new JsonObject
        {
            ["role"] = message.Role switch
            {
                AiChatRole.System => "system",
                AiChatRole.User => "user",
                AiChatRole.Assistant => "assistant",
                _ => throw new ArgumentOutOfRangeException(nameof(request), message.Role, null),
            },
            ["content"] = message.Content,
        })]),
        ["response_format"] = new JsonObject
        {
            ["type"] = "json_schema",
            ["json_schema"] = new JsonObject
            {
                ["name"] = request.SchemaName,
                ["strict"] = true,
                ["schema"] = JsonNode.Parse(request.JsonSchema),
            },
        },
    };

    /// <summary>The first choice's message text: the JSON the model wrote.</summary>
    private static string Content(string responseText)
    {
        try
        {
            using var document = JsonDocument.Parse(responseText);
            var content = document.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
            return string.IsNullOrWhiteSpace(content)
                ? throw new AiProviderException(AiFailure.BadResponse, "Grok sent an empty answer. Try again.")
                : content;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or IndexOutOfRangeException or InvalidOperationException)
        {
            throw new AiProviderException(AiFailure.BadResponse, "Grok sent an answer CoreFoundry couldn't read. Try again.", ex);
        }
    }

    private static AiProviderException Failed(HttpStatusCode status, bool ownKey, string? detail) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new(
            AiFailure.KeyRejected,
            ownKey ? "xAI rejected your key. Check it, or remove it to use CoreFoundry's key." : "xAI rejected the server's key. Ask the administrator to check it."),
        HttpStatusCode.TooManyRequests => new(AiFailure.RateLimited, "Grok is busy or the key's limit is reached. Wait a moment and try again."),
        >= HttpStatusCode.InternalServerError => new(AiFailure.Unavailable, "Grok is unavailable right now. Try again."),
        _ => new(AiFailure.Unavailable, $"Grok refused the request ({(int)status}){(detail is null ? "." : $": {detail}")}"),
    };

    /// <summary>The provider's own error message, if it sent one (it never contains the key).</summary>
    private static string? ProviderError(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (root.TryGetProperty("error", out var error))
            {
                return error.ValueKind == JsonValueKind.String ? error.GetString()
                    : error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var nested) ? nested.GetString()
                    : null;
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
