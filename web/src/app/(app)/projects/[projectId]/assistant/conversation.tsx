"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useEffect, useRef, useState } from "react";
import { Alert, Badge, Button, Card, ConfirmDialog } from "@/components/ui";
import { useAssistantStep, useConfirmProposal } from "@/lib/queries";
import type { AssistantSession } from "@/lib/types";
import { Bubble, Composer, TypingBubble } from "./chat";
import { ProposalView } from "./proposal-view";

const statusTone = { Asking: "accent", Proposed: "warn", Confirmed: "ok", Cancelled: "neutral" } as const;

/**
 * One conversation: the goal, then questions and answers as chat bubbles. A sent message shows at once (faded
 * until saved) with the AI's typing bubble after it; the view follows the newest message, and the answer box
 * takes focus when a question arrives.
 */
export function Conversation({ projectId, session }: { projectId: number; session: AssistantSession }) {
  const router = useRouter();
  const step = useAssistantStep(projectId);
  const confirm = useConfirmProposal(projectId);
  const [revising, setRevising] = useState(false);
  const [cancelling, setCancelling] = useState(false);
  const end = useRef<HTMLDivElement>(null);

  const last = session.messages.at(-1);
  const open = session.status === "Asking" || session.status === "Proposed";
  const waitingForAnswer = session.status === "Asking" && last?.kind === "Question";
  const thinking = step.isPending && step.variables?.kind !== "cancel";
  const busy = step.isPending || confirm.isPending;
  const error = step.error ?? confirm.error;

  // What the user just sent, shown until the server's copy arrives.
  const sending =
    step.isPending && step.variables?.kind === "answer"
      ? { text: step.variables.text, label: undefined }
      : step.isPending && step.variables?.kind === "revise"
        ? { text: step.variables.feedback, label: "Change request" }
        : null;

  useEffect(() => {
    end.current?.scrollIntoView({ behavior: "smooth", block: "end" });
  }, [session.messages.length, thinking, session.status]);

  const answer = (text: string) => step.mutateAsync({ kind: "answer", session, text });
  const requestChanges = (feedback: string) =>
    step.mutateAsync({ kind: "revise", session, feedback }).then(() => setRevising(false));

  return (
    <Card className="grid gap-5 p-5" data-testid="assistant-conversation">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <Progress session={session} />
        <Badge tone={statusTone[session.status]}>{session.status === "Asking" ? "In progress" : session.status}</Badge>
      </div>

      <ol className="grid gap-3" role="log" aria-live="polite" aria-label="Conversation with the AI">
        <Bubble from="you" label="Goal">
          {session.goal}
        </Bubble>
        {session.messages.map((message) => {
          switch (message.kind) {
            case "Question":
              return (
                <Bubble key={message.id} from="ai">
                  {message.text}
                </Bubble>
              );
            case "Proposal":
              return (
                <Bubble key={message.id} from="ai" label="Proposal">
                  {message.text}
                </Bubble>
              );
            case "Feedback":
              return (
                <Bubble key={message.id} from="you" label="Change request">
                  {message.text}
                </Bubble>
              );
            default:
              return (
                <Bubble key={message.id} from="you">
                  {message.text}
                </Bubble>
              );
          }
        })}
        {sending && (
          <Bubble from="you" label={sending.label} pending>
            {sending.text}
          </Bubble>
        )}
        {thinking && <TypingBubble />}
      </ol>

      {waitingForAnswer && !busy && last.options.length > 0 && (
        <div className="grid gap-1.5 pl-9">
          <p className="text-xs text-muted">Suggested answers</p>
          <div className="flex flex-wrap gap-2">
            {last.options.map((option) => (
              <button
                key={option}
                type="button"
                className="rounded-full border border-accent/40 bg-surface px-3 py-1.5 text-left text-sm text-accent transition-colors hover:bg-accent-soft"
                onClick={() => void answer(option).catch(() => undefined)}
              >
                {option}
              </button>
            ))}
          </div>
        </div>
      )}

      {error && !busy && <Alert>{error.message}</Alert>}

      {session.awaitingAssistant && open && !busy && (
        <div className="flex flex-wrap items-center justify-between gap-3 rounded-md border border-warn/30 bg-warn-soft px-3 py-2 text-sm text-warn">
          <span>The AI hasn&apos;t answered your last message yet. Your message is saved.</span>
          <Button variant="secondary" onClick={() => step.mutate({ kind: "continue", session })}>
            Try again
          </Button>
        </div>
      )}

      {session.proposal && (session.status === "Proposed" || session.status === "Confirmed") && (
        <ProposalView proposal={session.proposal} confirmed={session.status === "Confirmed"} />
      )}

      {waitingForAnswer && (
        <Composer
          key={last.id}
          id="assistant-reply"
          label="Your answer"
          placeholder={last.options.length > 0 ? "Pick a suggestion or type your own answer…" : "Type your answer…"}
          sending={busy}
          onSend={answer}
        />
      )}

      {session.status === "Proposed" && !busy && (
        revising ? (
          <Composer
            id="assistant-feedback"
            label="What should change?"
            placeholder="e.g. Add a phone number to customers, and keep reviews public"
            sending={busy}
            onSend={requestChanges}
            onBack={() => setRevising(false)}
          />
        ) : (
          <div className="flex flex-wrap items-center justify-between gap-3 rounded-lg border border-border bg-surface-muted px-4 py-3">
            <p className="text-sm text-muted">Nothing is created until you confirm, and then only as drafts.</p>
            <div className="flex flex-wrap gap-2">
              <Button variant="secondary" onClick={() => setRevising(true)}>
                Change something
              </Button>
              <Button loading={confirm.isPending} onClick={() => confirm.mutate(session)}>
                Confirm and create drafts
              </Button>
            </div>
          </div>
        )
      )}

      {session.status === "Confirmed" && (
        <div role="status" className="flex flex-wrap items-center justify-between gap-3 rounded-lg border border-ok/30 bg-ok-soft px-4 py-3">
          <div className="text-sm text-ok">
            <p className="font-medium">The tables were created as drafts.</p>
            <p>Review the SQL plan and apply it to create them in the database.</p>
          </div>
          <div className="flex flex-wrap gap-2">
            <Link
              href={`/projects/${projectId}/tables`}
              className="inline-flex h-9 items-center rounded-md border border-border bg-surface px-3.5 text-sm font-medium hover:bg-surface-muted"
            >
              See the tables
            </Link>
            <Link
              href={`/projects/${projectId}/schema`}
              className="inline-flex h-9 items-center rounded-md bg-accent-solid px-3.5 text-sm font-medium text-on-accent hover:bg-accent-solid-hover"
            >
              Review plan &amp; apply
            </Link>
          </div>
        </div>
      )}

      {open && (
        <div className="flex justify-end border-t border-border pt-3">
          <Button variant="ghost" className="h-8 text-xs" disabled={busy} onClick={() => setCancelling(true)}>
            Cancel this conversation
          </Button>
        </div>
      )}

      <div ref={end} aria-hidden />

      <ConfirmDialog
        open={cancelling}
        title="Cancel this conversation?"
        description="The questions and answers stay in the history, but this conversation can't be continued. Nothing was created."
        confirmLabel="Cancel conversation"
        danger
        pending={step.isPending && step.variables?.kind === "cancel"}
        error={step.variables?.kind === "cancel" ? step.error?.message : null}
        onClose={() => setCancelling(false)}
        onConfirm={() =>
          step.mutate(
            { kind: "cancel", session },
            {
              onSuccess: () => {
                setCancelling(false);
                router.replace(`/projects/${projectId}/assistant`);
              },
            },
          )
        }
      />
    </Card>
  );
}

/** How far the interview is: questions asked out of the most it will ask before proposing. */
function Progress({ session }: { session: AssistantSession }) {
  if (session.status !== "Asking") {
    return <p className="text-sm text-muted">{count(session.questionCount, "question")} asked</p>;
  }

  const percent = Math.min(100, Math.round((session.questionCount / session.maxQuestions) * 100));
  return (
    <div className="grid min-w-48 flex-1 gap-1">
      <p className="text-xs text-muted tabular-nums">
        Question {Math.max(session.questionCount, 1)} of up to {session.maxQuestions}. The AI proposes as soon as it knows enough.
      </p>
      <div
        className="h-1.5 max-w-xs overflow-hidden rounded-full bg-surface-muted"
        role="progressbar"
        aria-valuemin={0}
        aria-valuemax={session.maxQuestions}
        aria-valuenow={session.questionCount}
        aria-label="Questions asked"
      >
        <div className="h-full rounded-full bg-accent transition-[width] duration-500" style={{ width: `${percent}%` }} />
      </div>
    </div>
  );
}

function count(n: number, noun: string) {
  return `${n} ${noun}${n === 1 ? "" : "s"}`;
}
