using CoreFoundry.Domain.Assistant;
using CoreFoundry.Domain.Common;
using Shouldly;

namespace CoreFoundry.UnitTests.Assistant;

public class AssistantSessionTests
{
    [Fact]
    public void Questions_and_answers_alternate_then_a_proposal_waits_for_confirmation()
    {
        var session = new AssistantSession(1, 2, "  A booking app  ");
        session.Goal.ShouldBe("A booking app");

        session.Ask("Who books?", "[\"Patients\"]");
        session.Answer("Patients");
        session.Propose("Two tables.", "{}");

        session.Status.ShouldBe(AssistantSessionStatus.Proposed);
        session.Messages.Select(message => (message.Sequence, message.Kind)).ShouldBe(
            [(1, AssistantMessageKind.Question), (2, AssistantMessageKind.Answer), (3, AssistantMessageKind.Proposal)]);
        session.QuestionCount.ShouldBe(1);
        session.Version.ShouldBe(4);

        session.Confirm();
        session.Status.ShouldBe(AssistantSessionStatus.Confirmed);
        session.IsOpen.ShouldBeFalse();
    }

    [Fact]
    public void Turns_out_of_order_are_refused()
    {
        var session = new AssistantSession(1, 2, "Goal");
        Should.Throw<DomainException>(() => session.Answer("nobody asked"));
        Should.Throw<DomainException>(session.Confirm);

        session.Ask("First?", null);
        Should.Throw<DomainException>(() => session.Ask("Second before an answer?", null));
        Should.Throw<DomainException>(() => session.Propose("Before the answer", "{}"));
        Should.Throw<DomainException>(() => session.RequestChanges("no proposal yet"));
    }

    [Fact]
    public void After_the_question_budget_only_a_proposal_is_accepted()
    {
        var session = new AssistantSession(1, 2, "Goal");
        for (var i = 0; i < AssistantSession.MaxQuestions; i++)
        {
            session.Ask($"Q{i}?", null);
            session.Answer("A");
        }

        session.MustPropose.ShouldBeTrue();
        Should.Throw<DomainException>(() => session.Ask("One more?", null)).Message.ShouldContain("must propose");
        session.Propose("Done.", "{}");
    }

    [Fact]
    public void Feedback_reopens_the_conversation_and_cancel_closes_it()
    {
        var session = new AssistantSession(1, 2, "Goal");
        session.Propose("Straight away.", "{}"); // the goal was enough
        session.RequestChanges("Add prices");

        session.Status.ShouldBe(AssistantSessionStatus.Asking);
        session.Propose("With prices.", "{\"v\":2}");
        session.ProposalJson.ShouldBe("{\"v\":2}");

        session.Cancel();
        session.Status.ShouldBe(AssistantSessionStatus.Cancelled);
        Should.Throw<DomainException>(session.Cancel);
    }

    [Fact]
    public void Blank_or_too_long_text_is_refused()
    {
        Should.Throw<DomainException>(() => new AssistantSession(1, 2, " "));
        Should.Throw<DomainException>(() => new AssistantSession(1, 2, new string('x', AssistantSession.GoalMaxLength + 1)));

        var session = new AssistantSession(1, 2, "Goal");
        session.Ask("Q?", null);
        Should.Throw<DomainException>(() => session.Answer("   ")).Message.ShouldContain("Your answer");
        Should.Throw<DomainException>(() => session.Answer(new string('x', AssistantMessage.TextMaxLength + 1)));
    }
}
