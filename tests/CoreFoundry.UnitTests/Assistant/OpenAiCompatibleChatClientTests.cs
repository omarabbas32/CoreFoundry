using System.Net;
using System.Text;
using System.Text.Json;
using CoreFoundry.Application.Assistant;
using CoreFoundry.Domain.Schema;
using CoreFoundry.Infrastructure.Ai;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace CoreFoundry.UnitTests.Assistant;

public class OpenAiCompatibleChatClientTests
{
    private static readonly AiChatRequest Request = new(
        null,
        [new AiChatMessage(AiChatRole.System, "rules"), new AiChatMessage(AiChatRole.User, "hi")],
        AssistantPrompt.SchemaName,
        AssistantPrompt.ReplySchema);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Sends_the_model_messages_and_strict_schema_with_the_default_key_and_returns_the_content()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, Completion("{\"kind\":\"question\"}"));
        var client = Client(handler, defaultKey: "server-key");

        var content = await client.CompleteAsync(Request, Ct);

        content.ShouldBe("{\"kind\":\"question\"}");
        handler.Uri.ShouldBe(new Uri("https://api.groq.com/openai/v1/chat/completions"));
        handler.Authorization.ShouldBe("Bearer server-key");
        using var body = JsonDocument.Parse(handler.Body!);
        body.RootElement.GetProperty("model").GetString().ShouldBe("openai/gpt-oss-120b");
        body.RootElement.GetProperty("messages")[0].GetProperty("role").GetString().ShouldBe("system");
        body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString().ShouldBe("hi");
        var format = body.RootElement.GetProperty("response_format");
        format.GetProperty("type").GetString().ShouldBe("json_schema");
        format.GetProperty("json_schema").GetProperty("strict").GetBoolean().ShouldBeTrue();
        format.GetProperty("json_schema").GetProperty("schema").GetProperty("required").GetArrayLength().ShouldBe(6);
    }

    [Fact]
    public async Task A_users_own_key_wins_over_the_default()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, Completion("{}"));

        await Client(handler, defaultKey: "server-key").CompleteAsync(Request with { ApiKey = "own-key" }, Ct);

        handler.Authorization.ShouldBe("Bearer own-key");
    }

    [Fact]
    public async Task Without_any_key_nothing_is_sent()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, Completion("{}"));

        var failure = await Should.ThrowAsync<AiProviderException>(() => Client(handler, defaultKey: null).CompleteAsync(Request, Ct));

        failure.Failure.ShouldBe(AiFailure.NotConfigured);
        handler.Uri.ShouldBeNull();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, AiFailure.KeyRejected)]
    [InlineData(HttpStatusCode.TooManyRequests, AiFailure.RateLimited)]
    [InlineData(HttpStatusCode.BadGateway, AiFailure.Unavailable)]
    [InlineData(HttpStatusCode.BadRequest, AiFailure.Unavailable)]
    public async Task Provider_errors_map_to_failures_without_the_key(HttpStatusCode status, AiFailure expected)
    {
        var handler = new FakeHandler(status, "{\"error\":{\"message\":\"Model not found\"}}");

        var failure = await Should.ThrowAsync<AiProviderException>(
            () => Client(handler, defaultKey: "server-key").CompleteAsync(Request with { ApiKey = "own-secret-key" }, Ct));

        failure.Failure.ShouldBe(expected);
        failure.Message.ShouldNotContain("own-secret-key");
        if (status == HttpStatusCode.Unauthorized)
        {
            failure.Message.ShouldContain("rejected your key");
        }

        if (status == HttpStatusCode.BadRequest)
        {
            failure.Message.ShouldContain("Model not found");
        }
    }

    [Fact]
    public async Task An_unreadable_or_empty_answer_is_a_bad_response()
    {
        (await Should.ThrowAsync<AiProviderException>(
            () => Client(new FakeHandler(HttpStatusCode.OK, "<html>"), "k").CompleteAsync(Request, Ct))).Failure.ShouldBe(AiFailure.BadResponse);
        (await Should.ThrowAsync<AiProviderException>(
            () => Client(new FakeHandler(HttpStatusCode.OK, Completion("")), "k").CompleteAsync(Request, Ct))).Failure.ShouldBe(AiFailure.BadResponse);
    }

    [Fact]
    public async Task A_model_that_refuses_the_schema_format_is_asked_again_in_json_mode_with_the_schema_in_the_prompt()
    {
        var handler = new FakeHandler(
            (HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"This model does not support response format `json_schema`\"}}"),
            (HttpStatusCode.OK, Completion("{\"kind\":\"question\"}")));

        var content = await Client(handler, "k").CompleteAsync(Request, Ct);

        content.ShouldBe("{\"kind\":\"question\"}");
        handler.Bodies.Count.ShouldBe(2);
        using var retry = JsonDocument.Parse(handler.Bodies[1]);
        retry.RootElement.GetProperty("response_format").GetProperty("type").GetString().ShouldBe("json_object");
        var system = retry.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
        system.ShouldStartWith("rules");
        system.ShouldContain(AssistantPrompt.ReplySchema);
    }

    [Fact]
    public async Task A_generation_the_provider_could_not_validate_is_returned_for_the_repair_loop()
    {
        var handler = new FakeHandler(HttpStatusCode.BadRequest,
            """{"error":{"message":"Failed to generate JSON. Please adjust your prompt.","code":"json_validate_failed","failed_generation":"{\"kind\":\"question\",\"question\":\"Who?\"}"}}""");

        var content = await Client(handler, "k").CompleteAsync(Request, Ct);

        content.ShouldBe("""{"kind":"question","question":"Who?"}""");
    }

    [Fact]
    public async Task When_json_mode_fails_too_the_error_names_both_the_failure_and_the_schema_refusal()
    {
        var handler = new FakeHandler(
            (HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"invalid json_schema: enum not allowed here\"}}"),
            (HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"Something else went wrong\"}}"));

        var failure = await Should.ThrowAsync<AiProviderException>(() => Client(handler, "k").CompleteAsync(Request, Ct));

        failure.Message.ShouldContain("Something else went wrong");
        failure.Message.ShouldContain("invalid json_schema: enum not allowed here");
    }

    [Fact]
    public void Optional_choices_in_the_reply_schema_use_anyOf_as_strict_mode_documents()
    {
        using var schema = JsonDocument.Parse(AssistantPrompt.ReplySchema);
        var onDelete = schema.RootElement.GetProperty("properties").GetProperty("newTables").GetProperty("items")
            .GetProperty("properties").GetProperty("columns").GetProperty("items").GetProperty("properties").GetProperty("onDelete");

        var options = onDelete.GetProperty("anyOf");
        options[0].GetProperty("enum").GetArrayLength().ShouldBe(3);
        options[1].GetProperty("type").GetString().ShouldBe("null");
        onDelete.TryGetProperty("enum", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Other_bad_requests_are_not_retried()
    {
        var handler = new FakeHandler(HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"The model `nope` does not exist\"}}");

        var failure = await Should.ThrowAsync<AiProviderException>(() => Client(handler, "k").CompleteAsync(Request, Ct));

        failure.Message.ShouldContain("The model `nope` does not exist");
        handler.Bodies.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_timeout_is_unavailable()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, Completion("{}")) { Delay = TimeSpan.FromSeconds(5) };
        var client = new OpenAiCompatibleChatClient(
            new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(50) }, Options.Create(new AiOptions { ApiKey = "k" }), NullLogger<OpenAiCompatibleChatClient>.Instance);

        (await Should.ThrowAsync<AiProviderException>(() => client.CompleteAsync(Request, Ct))).Failure.ShouldBe(AiFailure.Unavailable);
    }

    [Fact]
    public void The_reply_schema_is_valid_json_listing_every_type_and_level()
    {
        using var schema = JsonDocument.Parse(AssistantPrompt.ReplySchema);
        var text = AssistantPrompt.ReplySchema;
        foreach (var name in Enum.GetNames<DataType>().Concat(Enum.GetNames<AccessLevel>()))
        {
            text.ShouldContain($"\"{name}\"");
        }

        schema.RootElement.GetProperty("additionalProperties").GetBoolean().ShouldBeFalse();
    }

    private static OpenAiCompatibleChatClient Client(FakeHandler handler, string? defaultKey) =>
        new(new HttpClient(handler), Options.Create(new AiOptions { ApiKey = defaultKey }), NullLogger<OpenAiCompatibleChatClient>.Instance);

    private static string Completion(string content) =>
        JsonSerializer.Serialize(new { choices = new[] { new { message = new { role = "assistant", content } } } });

    /// <summary>Answers each call with the next response; the last one repeats. Records every request body.</summary>
    private sealed class FakeHandler(params (HttpStatusCode Status, string Body)[] responses) : HttpMessageHandler
    {
        public FakeHandler(HttpStatusCode status, string body)
            : this((status, body))
        {
        }

        public TimeSpan Delay { get; init; }
        public Uri? Uri { get; private set; }
        public string? Authorization { get; private set; }
        public string? Body => Bodies.LastOrDefault();
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            Authorization = request.Headers.Authorization?.ToString();
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken);
            }

            var (status, body) = responses[Math.Min(Bodies.Count, responses.Length) - 1];
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
