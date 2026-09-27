"use client";

import Link from "next/link";
import { useParams, useSearchParams } from "next/navigation";
import { Suspense, useState } from "react";
import { PageSkeleton } from "@/components/page-skeleton";
import { Alert, Badge, Card } from "@/components/ui";
import { useAssistantSession, useAssistantSessions, useAssistantStep, useTables } from "@/lib/queries";
import type { AssistantSessionStatus } from "@/lib/types";
import { Bubble, Composer, TypingBubble } from "./chat";
import { Conversation } from "./conversation";

/**
 * The AI schema assistant: it asks one question at a time, then proposes tables. Confirming creates them as
 * drafts; the normal plan and apply follow. Conversations are saved, so the open one resumes here.
 */
export default function AssistantPage() {
  // useSearchParams (the ?session= of an earlier conversation) needs a Suspense boundary.
  return (
    <Suspense fallback={<PageSkeleton label="Loading the assistant…" variant="detail" />}>
      <Assistant />
    </Suspense>
  );
}

function Assistant() {
  const projectId = Number(useParams<{ projectId: string }>().projectId);
  const chosen = useSearchParams().get("session");
  const tables = useTables(projectId);
  const sessions = useAssistantSessions(projectId);
  const open = sessions.data?.find((session) => session.status === "Asking" || session.status === "Proposed");
  const sessionId = chosen ? Number(chosen) : (open?.id ?? null);
  const session = useAssistantSession(projectId, sessionId);

  if (sessions.isPending || (sessionId !== null && session.isPending)) return <PageSkeleton label="Loading the assistant…" variant="detail" />;

  const extending = (tables.data?.length ?? 0) > 0;
  const past = (sessions.data ?? []).filter((summary) => summary.id !== sessionId);
  const viewingPast = chosen !== null && session.data && session.data.id !== open?.id;

  return (
    <div className="grid gap-6">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="grid gap-1">
          <h1 className="text-2xl font-semibold tracking-tight">{extending ? "Extend with AI" : "Design with AI"}</h1>
          <p className="max-w-2xl text-sm text-muted">
            Describe your app. The AI asks a few questions, one at a time, then proposes tables. Nothing is created until
            you confirm, and then only as drafts you review and apply. Your conversations are yours: other members have
            their own.
          </p>
        </div>
        {viewingPast && (
          <Link
            href={`/projects/${projectId}/assistant`}
            className="inline-flex h-9 items-center rounded-md border border-border bg-surface px-3.5 text-sm font-medium hover:bg-surface-muted"
          >
            {open ? "Back to the open conversation" : "New conversation"}
          </Link>
        )}
      </div>

      {sessions.error && <Alert>{sessions.error.message}</Alert>}
      {session.error && <Alert>{session.error.message}</Alert>}

      {session.data ? (
        <Conversation key={session.data.id} projectId={projectId} session={session.data} />
      ) : (
        <StartForm projectId={projectId} extending={extending} />
      )}

      {past.length > 0 && (
        <Card className="grid gap-3 p-5">
          <h2 className="font-semibold">Your earlier conversations</h2>
          <ul className="grid divide-y divide-border">
            {past.map((summary) => (
              <li key={summary.id}>
                <Link
                  href={`/projects/${projectId}/assistant?session=${summary.id}`}
                  className="flex items-center justify-between gap-3 py-2 text-sm hover:text-accent"
                >
                  <span className="min-w-0 truncate">{summary.goal}</span>
                  <span className="flex shrink-0 items-center gap-2 text-xs text-muted">
                    {new Date(summary.updatedAt).toLocaleDateString(undefined, { dateStyle: "medium" })}
                    <Badge tone={historyTone[summary.status]}>{summary.status}</Badge>
                  </span>
                </Link>
              </li>
            ))}
          </ul>
        </Card>
      )}
    </div>
  );
}

const historyTone: Record<AssistantSessionStatus, "accent" | "warn" | "ok" | "neutral"> = {
  Asking: "accent",
  Proposed: "warn",
  Confirmed: "ok",
  Cancelled: "neutral",
};

const newProjectExamples = [
  "A booking app for a small clinic: patients, doctors and appointments",
  "An online bookshop with authors, books, orders and reviews",
  "A task tracker for teams with projects, tasks and comments",
];

const extendExamples = [
  "Let customers leave reviews and ratings",
  "Add discount codes that apply to orders",
  "Track stock per warehouse",
];

/** The first message: what the user wants. It shows as their first bubble right away, while the AI thinks. */
function StartForm({ projectId, extending }: { projectId: number; extending: boolean }) {
  const step = useAssistantStep(projectId);
  const [example, setExample] = useState<string | null>(null);
  const start = (goal: string) => step.mutateAsync({ kind: "start", goal });

  if (step.isPending && step.variables?.kind === "start") {
    return (
      <Card className="p-5">
        <ol className="grid gap-3" role="log" aria-live="polite">
          <Bubble from="you" label="Goal" pending>
            {step.variables.goal}
          </Bubble>
          <TypingBubble />
        </ol>
      </Card>
    );
  }

  return (
    <Card className="grid gap-4 p-5">
      <ol className="grid gap-3">
        <Bubble from="ai">
          {extending
            ? "What would you like to add to this project? I'll look at the tables you already have."
            : "What are you building? One sentence is enough; I'll ask about the details."}
        </Bubble>
      </ol>

      <div className="grid gap-1.5 pl-9">
        <p className="text-xs text-muted">Try an example</p>
        <div className="flex flex-wrap gap-2">
          {(extending ? extendExamples : newProjectExamples).map((text) => (
            <button
              key={text}
              type="button"
              className="rounded-full border border-border bg-surface px-3 py-1.5 text-left text-sm transition-colors hover:border-accent/50 hover:text-accent"
              onClick={() => setExample(text)}
            >
              {text}
            </button>
          ))}
        </div>
      </div>

      {step.error && <Alert>{step.error.message}</Alert>}

      <Composer
        key={example ?? "empty"}
        id="assistant-goal"
        label={extending ? "What do you want to add?" : "What are you building?"}
        placeholder={extending ? "e.g. Let customers leave reviews on products" : "e.g. A booking app for a small clinic"}
        initialText={example ?? ""}
        maxLength={1000}
        sending={step.isPending}
        onSend={start}
      />
    </Card>
  );
}
