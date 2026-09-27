using System.Net;
using CoreFoundry.Api.Projects;
using CoreFoundry.Application.Assistant;
using CoreFoundry.Application.Projects;
using CoreFoundry.Application.Schema;
using CoreFoundry.Domain.Assistant;
using CoreFoundry.Domain.Projects;
using CoreFoundry.Domain.Schema;
using CoreFoundry.IntegrationTests.Infrastructure;
using Shouldly;

namespace CoreFoundry.IntegrationTests.Assistant;

/// <summary>The AI schema assistant (M10) end to end, with a scripted model in place of the AI provider.</summary>
[Collection(TestDatabaseGroup.Name)]
public sealed class AssistantEndpointsTests : IDisposable
{
    private readonly TestDatabaseApi _api;
    private readonly HttpClient _client;
    private readonly ApiDriver _driver;

    public AssistantEndpointsTests(TestDatabaseApi api)
    {
        api.SkipIfUnavailable();
        _api = api;
        _api.Ai.Reset();
        _client = api.CreateHttpsClient();
        _driver = new ApiDriver(_client);
    }

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task An_interview_ends_in_a_proposal_that_confirms_into_draft_tables()
    {
        var (owner, project) = await ProjectAsync("Clinic");
        _api.Ai.Question("Do patients book appointments themselves?", "Yes, online", "No, staff book them")
            .Question("Should anyone see a live calendar?", "Yes", "No")
            .Proposal("Patients and their appointments.", [Patients(), Appointments()]);

        var started = await _driver.OkAsync<AssistantSessionDto>(HttpMethod.Post, Sessions(project), owner,
            new StartAssistantRequest("A small clinic booking app"), HttpStatusCode.Created);
        started.Status.ShouldBe(AssistantSessionStatus.Asking);
        var question = started.Messages.ShouldHaveSingleItem();
        (question.Kind, question.Text).ShouldBe((AssistantMessageKind.Question, "Do patients book appointments themselves?"));
        question.Options.ShouldBe(["Yes, online", "No, staff book them"]);

        var second = await Answer(project, owner, started, "Yes, online");
        second.Messages.Select(message => message.Kind).ShouldBe(
            [AssistantMessageKind.Question, AssistantMessageKind.Answer, AssistantMessageKind.Question]);

        var proposed = await Answer(project, owner, second, "No");
        proposed.Status.ShouldBe(AssistantSessionStatus.Proposed);
        proposed.Proposal.ShouldNotBeNull().NewTables.Select(table => table.Name).ShouldBe(["patients", "appointments"]);

        // The model saw the rules, the goal and the empty project, then the whole conversation so far.
        var last = _api.Ai.Requests[^1];
        last.Messages[0].Role.ShouldBe(AiChatRole.System);
        last.Messages[1].Content.ShouldContain("A small clinic booking app");
        last.Messages[1].Content.ShouldContain("no tables yet");
        last.Messages.Where(message => message.Role == AiChatRole.User).Select(message => message.Content).ShouldContain("Yes, online");
        last.ApiKey.ShouldBeNull(); // no own key: the server's default

        // Resuming shows the same conversation.
        (await _driver.OkAsync<AssistantSessionDto>(HttpMethod.Get, $"{Sessions(project)}/{started.Id}", owner)).Messages.Count.ShouldBe(5);

        var confirmed = await _driver.OkAsync<ConfirmedProposalDto>(HttpMethod.Post, $"{Sessions(project)}/{started.Id}/confirm", owner,
            new AssistantVersionRequest(proposed.Version));
        confirmed.Session.Status.ShouldBe(AssistantSessionStatus.Confirmed);
        confirmed.Tables.Select(table => (table.Name, table.State)).ShouldBe(
            [("patients", SchemaObjectState.New), ("appointments", SchemaObjectState.New)], ignoreOrder: true);
        var appointments = confirmed.Tables.Single(table => table.Name == "appointments");
        (appointments.ReadAccess, appointments.WriteAccess, appointments.Realtime).ShouldBe((AccessLevel.SignedIn, AccessLevel.Admin, true));
        confirmed.Tables.Single(table => table.Name == "patients").Realtime.ShouldBeFalse();

        var schema = await _driver.OkAsync<List<TableDto>>(HttpMethod.Get, $"/api/projects/{project.Id}/schema", owner);
        var patientId = schema.Single(table => table.Name == "appointments").Columns.Single(column => column.Name == "patient_id");
        (patientId.ReferencesTableName, patientId.OnDelete).ShouldBe(("patients", ReferenceAction.Cascade));

        // Closed: a second confirm is refused.
        (await _driver.SendAsync(HttpMethod.Post, $"{Sessions(project)}/{started.Id}/confirm", owner,
            new AssistantVersionRequest(confirmed.Session.Version))).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task An_invalid_proposal_goes_back_to_the_model_with_the_problems_before_the_user_sees_it()
    {
        var (owner, project) = await ProjectAsync("Repair");
        var broken = Patients() with { Columns = [Column("id", DataType.BigInt), Column("full_name", DataType.Varchar, length: 120)] };
        _api.Ai.Proposal("Patients.", [broken]).Proposal("Patients.", [Patients()]);

        var session = await StartAsync(project, owner, "Patients list");

        session.Status.ShouldBe(AssistantSessionStatus.Proposed);
        session.Proposal!.NewTables.ShouldHaveSingleItem().Columns.ShouldNotContain(column => column.Name == "id");
        _api.Ai.Requests.Count.ShouldBe(2);
        _api.Ai.Requests[1].Messages[^1].Content.ShouldContain("\"id\" is reserved");
    }

    [Fact]
    public async Task A_proposal_that_keeps_breaking_the_rules_fails_after_three_calls_and_can_be_retried()
    {
        var (owner, project) = await ProjectAsync("GiveUp");
        var broken = Patients() with { Columns = [Column("id", DataType.BigInt)] };
        _api.Ai.Proposal("x", [broken]).Proposal("x", [broken]).Proposal("x", [broken]);

        var response = await _driver.SendAsync(HttpMethod.Post, Sessions(project), owner, new StartAssistantRequest("Patients"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("after 3 tries");
        _api.Ai.Requests.Count.ShouldBe(AssistantService.MaxAttempts);

        // The conversation was saved before the model was called: it's open and waiting for the assistant.
        var open = (await _driver.OkAsync<List<AssistantSessionSummaryDto>>(HttpMethod.Get, Sessions(project), owner)).ShouldHaveSingleItem();
        var session = await _driver.OkAsync<AssistantSessionDto>(HttpMethod.Get, $"{Sessions(project)}/{open.Id}", owner);
        session.AwaitingAssistant.ShouldBeTrue();

        _api.Ai.Question("Do you store phone numbers?", "Yes", "No");
        var retried = await _driver.OkAsync<AssistantSessionDto>(HttpMethod.Post, $"{Sessions(project)}/{open.Id}/continue", owner,
            new AssistantVersionRequest(session.Version));
        retried.Messages.ShouldHaveSingleItem().Kind.ShouldBe(AssistantMessageKind.Question);
        retried.AwaitingAssistant.ShouldBeFalse();
    }

    [Fact]
    public async Task Provider_failures_become_readable_problems_and_keep_the_users_answer()
    {
        var (owner, project) = await ProjectAsync("Down");
        _api.Ai.Question("Anything else?", "No").Fail(AiFailure.Unavailable, "The AI service is unavailable right now. Try again.");
        var session = await StartAsync(project, owner, "Notes app");

        var response = await _driver.SendAsync(HttpMethod.Post, $"{Sessions(project)}/{session.Id}/answers", owner,
            new AssistantAnswerRequest(session.Version, "No"));

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("The AI service is unavailable right now.");
        var saved = await _driver.OkAsync<AssistantSessionDto>(HttpMethod.Get, $"{Sessions(project)}/{session.Id}", owner);
        saved.Messages[^1].Text.ShouldBe("No");
        saved.AwaitingAssistant.ShouldBeTrue();
    }

    [Fact]
    public async Task Extending_an_existing_schema_adds_tables_and_columns_that_reference_what_exists()
    {
        var (owner, project) = await ProjectAsync("Extend");
        await _driver.OkAsync<TableDto>(HttpMethod.Post, $"/api/projects/{project.Id}/tables", owner,
            new CreateTableRequest("books", [new ColumnRequest("title", DataType.Varchar, 200, null, null, false, false, null)]), HttpStatusCode.Created);
        var reviews = new ProposedTable("reviews", "Reader reviews", AccessLevel.Public, AccessLevel.SignedIn, true,
            [Column("book_id", DataType.BigInt, references: "books", onDelete: ReferenceAction.Cascade), Column("rating", DataType.Int)]);
        _api.Ai.Proposal("Reviews for books, and an ISBN on books.", [reviews],
            [new ProposedColumnAddition("books", Column("isbn", DataType.Varchar, length: 20, nullable: true, unique: true))]);

        var session = await StartAsync(project, owner, "Add reviews");
        _api.Ai.Requests[0].Messages[1].Content.ShouldContain("- books (read SignedIn, write SignedIn, realtime on): id BigInt primary key, title Varchar(200) not null");

        var confirmed = await _driver.OkAsync<ConfirmedProposalDto>(HttpMethod.Post, $"{Sessions(project)}/{session.Id}/confirm", owner,
            new AssistantVersionRequest(session.Version));

        confirmed.Tables.Select(table => table.Name).ShouldBe(["books", "reviews"], ignoreOrder: true);
        var schema = await _driver.OkAsync<List<TableDto>>(HttpMethod.Get, $"/api/projects/{project.Id}/schema", owner);
        schema.Single(table => table.Name == "books").Columns.Select(column => column.Name).ShouldBe(["title", "isbn"]);
        schema.Single(table => table.Name == "reviews").Columns[0].ReferencesTableName.ShouldBe("books");
    }

    [Fact]
    public async Task A_proposal_that_no_longer_fits_the_drafts_is_refused_at_confirm_and_writes_nothing()
    {
        var (owner, project) = await ProjectAsync("Stale");
        _api.Ai.Proposal("Patients.", [Patients()]);
        var session = await StartAsync(project, owner, "Patients");
        // Meanwhile someone creates a table with the same name.
        await _driver.OkAsync<TableDto>(HttpMethod.Post, $"/api/projects/{project.Id}/tables", owner,
            new CreateTableRequest("patients", [new ColumnRequest("name", DataType.Text, null, null, null, true, false, null)]), HttpStatusCode.Created);

        var response = await _driver.SendAsync(HttpMethod.Post, $"{Sessions(project)}/{session.Id}/confirm", owner,
            new AssistantVersionRequest(session.Version));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("no longer fits");
        (await _driver.OkAsync<List<TableSummaryDto>>(HttpMethod.Get, $"/api/projects/{project.Id}/tables", owner)).ShouldHaveSingleItem();
        (await _driver.OkAsync<AssistantSessionDto>(HttpMethod.Get, $"{Sessions(project)}/{session.Id}", owner)).Status
            .ShouldBe(AssistantSessionStatus.Proposed);
    }

    [Fact]
    public async Task Asking_for_changes_gets_a_new_proposal()
    {
        var (owner, project) = await ProjectAsync("Revise");
        _api.Ai.Proposal("Patients.", [Patients()]).Proposal("Patients with realtime.", [Patients() with { Realtime = true }]);
        var session = await StartAsync(project, owner, "Patients");

        var revised = await _driver.OkAsync<AssistantSessionDto>(HttpMethod.Post, $"{Sessions(project)}/{session.Id}/revise", owner,
            new AssistantFeedbackRequest(session.Version, "Turn realtime on"));

        revised.Status.ShouldBe(AssistantSessionStatus.Proposed);
        revised.Proposal!.NewTables.ShouldHaveSingleItem().Realtime.ShouldBeTrue();
        revised.Messages.Select(message => message.Kind).ShouldBe(
            [AssistantMessageKind.Proposal, AssistantMessageKind.Feedback, AssistantMessageKind.Proposal]);
        _api.Ai.Requests[^1].Messages[^1].Content.ShouldBe("Change the proposal: Turn realtime on");
    }

    [Fact]
    public async Task One_open_conversation_per_project_stale_versions_conflict_and_cancel_closes_it()
    {
        var (owner, project) = await ProjectAsync("Rules");
        _api.Ai.Question("Who uses it?", "Staff");
        var session = await StartAsync(project, owner, "Inventory");

        (await _driver.SendAsync(HttpMethod.Post, Sessions(project), owner, new StartAssistantRequest("Again")))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await _driver.SendAsync(HttpMethod.Post, $"{Sessions(project)}/{session.Id}/answers", owner,
            new AssistantAnswerRequest(session.Version - 1, "Staff"))).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var cancelled = await _driver.OkAsync<AssistantSessionDto>(HttpMethod.Post, $"{Sessions(project)}/{session.Id}/cancel", owner,
            new AssistantVersionRequest(session.Version));
        cancelled.Status.ShouldBe(AssistantSessionStatus.Cancelled);

        _api.Ai.Question("Who uses it?", "Staff");
        await StartAsync(project, owner, "Inventory, again"); // a new one can start now
    }

    [Fact]
    public async Task Each_member_has_their_own_conversations_and_can_not_see_anyone_elses()
    {
        var (owner, project) = await ProjectAsync("Private chats");
        var developer = await _driver.SignUpAsync();
        await _driver.AddMemberAsync(project.Id, owner, developer, ProjectRole.Developer);

        _api.Ai.Question("What do you sell?", "Books");
        var ownersChat = await StartAsync(project, owner, "Owner's shop");

        // The developer sees none of it, can't open it or answer in it, and can start their own at the same time.
        (await _driver.OkAsync<List<AssistantSessionSummaryDto>>(HttpMethod.Get, Sessions(project), developer)).ShouldBeEmpty();
        (await _driver.SendAsync(HttpMethod.Get, $"{Sessions(project)}/{ownersChat.Id}", developer)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _driver.SendAsync(HttpMethod.Post, $"{Sessions(project)}/{ownersChat.Id}/answers", developer,
            new AssistantAnswerRequest(ownersChat.Version, "Hijack"))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _driver.SendAsync(HttpMethod.Post, $"{Sessions(project)}/{ownersChat.Id}/cancel", developer,
            new AssistantVersionRequest(ownersChat.Version))).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        _api.Ai.Question("Who reads the blog?", "Everyone");
        var developersChat = await StartAsync(project, developer, "Developer's blog");

        (await _driver.OkAsync<List<AssistantSessionSummaryDto>>(HttpMethod.Get, Sessions(project), owner))
            .ShouldHaveSingleItem().Id.ShouldBe(ownersChat.Id);
        (await _driver.OkAsync<List<AssistantSessionSummaryDto>>(HttpMethod.Get, Sessions(project), developer))
            .ShouldHaveSingleItem().Id.ShouldBe(developersChat.Id);
        (await _driver.OkAsync<AssistantSessionDto>(HttpMethod.Get, $"{Sessions(project)}/{ownersChat.Id}", owner)).Goal.ShouldBe("Owner's shop");
    }

    [Fact]
    public async Task Non_members_get_404_and_developers_may_use_the_assistant()
    {
        var (owner, project) = await ProjectAsync("Private");
        var stranger = await _driver.SignUpAsync();
        var developer = await _driver.SignUpAsync();
        await _driver.AddMemberAsync(project.Id, owner, developer, ProjectRole.Developer);

        (await _driver.SendAsync(HttpMethod.Post, Sessions(project), stranger, new StartAssistantRequest("x"))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _driver.SendAsync(HttpMethod.Get, Sessions(project), stranger)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _driver.SendAsync(HttpMethod.Put, "/api/me/ai-key", null, new AiKeyRequest("gsk_anonymous-0123456789"))).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized);
        _api.Ai.Requests.ShouldBeEmpty();

        _api.Ai.Question("Who uses it?", "Staff");
        await StartAsync(project, developer, "Inventory");
    }

    [Fact]
    public async Task The_daily_cap_applies_to_the_default_key_and_an_own_key_is_used_instead_and_never_shown()
    {
        var (owner, project) = await ProjectAsync("Keys");
        await _api.ExecuteMetadataAsync(
            $"INSERT INTO AssistantUsage (UserId, Day, Calls) VALUES ({owner.UserId}, UTC_DATE(), 10000)");

        var capped = await _driver.SendAsync(HttpMethod.Post, Sessions(project), owner, new StartAssistantRequest("Shop"));
        capped.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await capped.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("Add your own Groq key");
        _api.Ai.Requests.ShouldBeEmpty();

        const string ownKey = "gsk_own-test-key-0123456789abcd";
        (await _driver.SendAsync(HttpMethod.Put, "/api/me/ai-key", owner, new AiKeyRequest("too short"))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var status = await _driver.OkAsync<AiKeyStatusDto>(HttpMethod.Put, "/api/me/ai-key", owner, new AiKeyRequest(ownKey));
        (status.HasOwnKey, status.Hint, status.HasDefaultKey).ShouldBe((true, "abcd", true));
        var raw = await (await _driver.SendAsync(HttpMethod.Get, "/api/me/ai-key", owner)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        raw.ShouldNotContain(ownKey);

        _api.Ai.Question("What do you sell?", "Books");
        var open = (await _driver.OkAsync<List<AssistantSessionSummaryDto>>(HttpMethod.Get, Sessions(project), owner)).ShouldHaveSingleItem();
        var session = await _driver.OkAsync<AssistantSessionDto>(HttpMethod.Get, $"{Sessions(project)}/{open.Id}", owner);
        await _driver.OkAsync<AssistantSessionDto>(HttpMethod.Post, $"{Sessions(project)}/{open.Id}/continue", owner,
            new AssistantVersionRequest(session.Version));
        _api.Ai.Requests.ShouldHaveSingleItem().ApiKey.ShouldBe(ownKey); // decrypted for the call; no cap on an own key

        (await _driver.OkAsync<AiKeyStatusDto>(HttpMethod.Delete, "/api/me/ai-key", owner)).HasOwnKey.ShouldBeFalse();
    }

    private async Task<(SignedIn Owner, ProjectDto Project)> ProjectAsync(string name)
    {
        var owner = await _driver.SignUpAsync();
        return (owner, await _driver.CreateProjectAsync(owner, name));
    }

    private Task<AssistantSessionDto> StartAsync(ProjectDto project, SignedIn owner, string goal) =>
        _driver.OkAsync<AssistantSessionDto>(HttpMethod.Post, Sessions(project), owner, new StartAssistantRequest(goal), HttpStatusCode.Created);

    private Task<AssistantSessionDto> Answer(ProjectDto project, SignedIn owner, AssistantSessionDto session, string text) =>
        _driver.OkAsync<AssistantSessionDto>(HttpMethod.Post, $"{Sessions(project)}/{session.Id}/answers", owner,
            new AssistantAnswerRequest(session.Version, text));

    private static string Sessions(ProjectDto project) => $"/api/projects/{project.Id}/assistant/sessions";

    private static ProposedTable Patients() => new("patients", "People treated at the clinic", AccessLevel.Admin, AccessLevel.Admin, false,
        [Column("full_name", DataType.Varchar, length: 120), Column("phone", DataType.Varchar, length: 30, nullable: true)]);

    private static ProposedTable Appointments() => new("appointments", "Booked visits", AccessLevel.SignedIn, AccessLevel.Admin, true,
        [
            Column("patient_id", DataType.BigInt, references: "patients", onDelete: ReferenceAction.Cascade),
            Column("starts_at", DataType.DateTime),
            Column("fee", DataType.Decimal, precision: 10, scale: 2, nullable: true),
        ]);

    private static ProposedColumn Column(
        string name, DataType type, int? length = null, int? precision = null, int? scale = null, bool nullable = false,
        bool unique = false, string? references = null, ReferenceAction? onDelete = null) =>
        new(name, type, length, precision, scale, nullable, unique, null, references, onDelete);
}
