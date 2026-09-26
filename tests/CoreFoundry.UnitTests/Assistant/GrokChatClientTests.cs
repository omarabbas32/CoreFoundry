using System.Net;
using System.Text;
using System.Text.Json;
using CoreFoundry.Application.Assistant;
using CoreFoundry.Domain.Schema;
using CoreFoundry.Infrastructure.Ai;
using Microsoft.Extensions.Options;
using Shouldly;

namespace CoreFoundry.UnitTests.Assistant;

public class GrokChatClientTests
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
        handler.Uri.ShouldBe(new Uri("https://api.x.ai/v1/chat/completions"));
        handler.Authorization.ShouldBe("Bearer server-key");
        using var body = JsonDocument.Parse(handler.Body!);
        body.RootElement.GetProperty("model").GetString().ShouldBe("grok-4");
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
    public async Task A_timeout_is_unavailable()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, Completion("{}")) { Delay = TimeSpan.FromSeconds(5) };
        var client = new GrokChatClient(new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(50) }, Options.Create(new GrokOptions { ApiKey = "k" }));

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

    private static GrokChatClient Client(FakeHandler handler, string? defaultKey) =>
        new(new HttpClient(handler), Options.Create(new GrokOptions { ApiKey = defaultKey }));

    private static string Completion(string content) =>
        JsonSerializer.Serialize(new { choices = new[] { new { message = new { role = "assistant", content } } } });

    private sealed class FakeHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public TimeSpan Delay { get; init; }
        public Uri? Uri { get; private set; }
        public string? Authorization { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            Authorization = request.Headers.Authorization?.ToString();
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken);
            }

            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
