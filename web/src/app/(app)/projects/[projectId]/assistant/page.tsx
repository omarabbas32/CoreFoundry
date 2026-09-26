"use client";

import Link from "next/link";
import { useParams, useRouter, useSearchParams } from "next/navigation";
import { Suspense, useState, type FormEvent } from "react";
import { accessLevelLabels } from "@/components/access-controls";
import { FullPageSpinner } from "@/components/full-page-spinner";
import { Alert, Badge, Button, Card, Spinner } from "@/components/ui";
import { useAssistantSession, useAssistantSessions, useAssistantStep, useConfirmProposal, useProject, useTables } from "@/lib/queries";
import type { AssistantSession, ProposedColumn, ProposedTable, SchemaProposal } from "@/lib/types";

const textareaClass =
  "w-full rounded-md border border-border bg-surface px-3 py-2 text-sm text-foreground placeholder:text-muted focus:border-accent focus:outline-none focus:ring-2 focus:ring-accent-soft";

/**
 * The AI schema assistant: it asks one question at a time, then proposes tables. Confirming creates them as
 * drafts; the normal plan and apply follow. Conversations are saved, so the open one resumes here.
 */
export default function AssistantPage() {
  // useSearchParams (the ?session= of an earlier conversation) needs a Suspense boundary.
  return (
    <Suspense fallback={<FullPageSpinner label="Loading the assistant…" />}>
      <Assistant />
    </Suspense>
  );
}

function Assistant() {
  const projectId = Number(useParams<{ projectId: string }>().projectId);
  const chosen = useSearchParams().get("session");
  const project = useProject(projectId);
  const tables = useTables(projectId);
  const sessions = useAssistantSessions(projectId);
  const open = sessions.data?.find((session) => session.status === "Asking" || session.status === "Proposed");
  const sessionId = chosen ? Number(chosen) : (open?.id ?? null);
  const session = useAssistantSession(projectId, sessionId);

  if (sessions.isPending || (sessionId !== null && session.isPending)) return <FullPageSpinner label="Loading the assistant…" />;

  const extending = (tables.data?.length ?? 0) > 0;
  const past = (sessions.data ?? []).filter((summary) => summary.id !== sessionId);

  return (
    <div className="grid gap-6">
      <div className="grid gap-2">
        <Link href={`/projects/${projectId}/tables`} className="text-sm text-muted hover:text-foreground">
          ← {project.data?.name ?? "Project"} tables
        </Link>
        <div>
          <h1 className="text-2xl font-semibold tracking-tight">{extending ? "Extend with AI" : "Design with AI"}</h1>
          <p className="text-sm text-muted">
            The AI asks a few questions, one at a time, then proposes tables. Nothing is created until you confirm, and then
            only as drafts you review and apply.
          </p>
        </div>
      </div>

      {sessions.error && <Alert>{sessions.error.message}</Alert>}
      {session.error && <Alert>{session.error.message}</Alert>}

      {session.data ? (
        <Conversation projectId={projectId} session={session.data} />
      ) : (
        <StartForm projectId={projectId} extending={extending} />
      )}

      {past.length > 0 && (
        <Card className="grid gap-2 p-5">
          <h2 className="font-semibold">Earlier conversations</h2>
          <ul className="grid gap-1 text-sm">
            {past.map((summary) => (
              <li key={summary.id} className="flex items-center justify-between gap-3">
                <Link href={`/projects/${projectId}/assistant?session=${summary.id}`} className="truncate text-accent hover:underline">
                  {summary.goal}
                </Link>
                <Badge>{summary.status}</Badge>
              </li>
            ))}
          </ul>
        </Card>
      )}
    </div>
  );
}

function StartForm({ projectId, extending }: { projectId: number; extending: boolean }) {
  const step = useAssistantStep(projectId);
  const [goal, setGoal] = useState("");

  function submit(event: FormEvent) {
    event.preventDefault();
    if (goal.trim()) step.mutate({ kind: "start", goal: goal.trim() });
  }

  return (
    <Card className="grid gap-3 p-5">
      <form onSubmit={submit} className="grid gap-3">
        <label htmlFor="assistant-goal" className="font-medium">
          {extending ? "What do you want to add to this project?" : "What are you building?"}
        </label>
        <textarea
          id="assistant-goal"
          rows={3}
          maxLength={1000}
          className={textareaClass}
          placeholder={extending ? "e.g. Let customers leave reviews on products" : "e.g. A booking app for a small clinic"}
          value={goal}
          onChange={(event) => setGoal(event.target.value)}
        />
        {step.error && <Alert>{step.error.message}</Alert>}
        <div className="flex justify-end">
          <Button type="submit" loading={step.isPending} disabled={!goal.trim()}>
            Start
          </Button>
        </div>
      </form>
      {step.isPending && <Thinking />}
    </Card>
  );
}

function Conversation({ projectId, session }: { projectId: number; session: AssistantSession }) {
  const router = useRouter();
  const step = useAssistantStep(projectId);
  const confirm = useConfirmProposal(projectId);
  const [text, setText] = useState("");
  const [revising, setRevising] = useState(false);

  const last = session.messages.at(-1);
  const waitingForAnswer = session.status === "Asking" && last?.kind === "Question";
  const open = session.status === "Asking" || session.status === "Proposed";
  const busy = step.isPending || confirm.isPending;
  const error = step.error ?? confirm.error;

  function send(value: string) {
    const trimmed = value.trim();
    if (!trimmed) return;
    step.mutate(
      revising ? { kind: "revise", session, feedback: trimmed } : { kind: "answer", session, text: trimmed },
      { onSuccess: () => { setText(""); setRevising(false); } },
    );
  }

  return (
    <Card className="grid gap-4 p-5" data-testid="assistant-conversation">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <p className="text-sm">
          <span className="text-muted">Goal:</span> {session.goal}
        </p>
        <div className="flex items-center gap-2 text-xs text-muted">
          <span className="tabular-nums">
            {session.questionCount}/{session.maxQuestions} questions
          </span>
          <Badge tone={session.status === "Confirmed" ? "ok" : "neutral"}>{session.status}</Badge>
        </div>
      </div>

      <ol className="grid gap-3">
        {session.messages.map((message) => {
          const mine = message.kind === "Answer" || message.kind === "Feedback";
          return (
            <li key={message.id} className={`flex ${mine ? "justify-end" : "justify-start"}`}>
              <div
                className={`max-w-[85%] whitespace-pre-wrap rounded-lg px-3 py-2 text-sm ${
                  mine ? "bg-accent text-on-accent" : "border border-border bg-surface-muted"
                }`}
              >
                {message.kind === "Feedback" && <span className="block text-xs opacity-80">Change request</span>}
                {message.kind === "Proposal" && <span className="block text-xs text-muted">Proposal</span>}
                {message.text}
              </div>
            </li>
          );
        })}
      </ol>

      {busy && <Thinking />}
      {error && <Alert>{error.message}</Alert>}

      {session.awaitingAssistant && open && !busy && (
        <div className="flex flex-wrap items-center justify-between gap-3 rounded-md border border-warn/30 bg-warn-soft px-3 py-2 text-sm text-warn">
          <span>The AI hasn&apos;t answered your last message yet.</span>
          <Button variant="secondary" onClick={() => step.mutate({ kind: "continue", session })}>
            Try again
          </Button>
        </div>
      )}

      {waitingForAnswer && last.options.length > 0 && !busy && (
        <div className="flex flex-wrap gap-2">
          {last.options.map((option) => (
            <Button key={option} variant="secondary" className="h-auto min-h-9 py-1.5 text-left" onClick={() => send(option)}>
              {option}
            </Button>
          ))}
        </div>
      )}

      {session.proposal && (session.status === "Proposed" || session.status === "Confirmed") && (
        <ProposalView proposal={session.proposal} />
      )}

      {(waitingForAnswer || revising) && (
        <form
          className="grid gap-2"
          onSubmit={(event) => {
            event.preventDefault();
            send(text);
          }}
        >
          <label htmlFor="assistant-reply" className="text-sm font-medium">
            {revising ? "What should change?" : "Your answer"}
          </label>
          <textarea
            id="assistant-reply"
            rows={2}
            maxLength={4000}
            className={textareaClass}
            value={text}
            onChange={(event) => setText(event.target.value)}
            onKeyDown={(event) => {
              if (event.key === "Enter" && !event.shiftKey) {
                event.preventDefault();
                send(text);
              }
            }}
          />
          <div className="flex justify-end gap-2">
            {revising && (
              <Button type="button" variant="ghost" onClick={() => setRevising(false)}>
                Back
              </Button>
            )}
            <Button type="submit" loading={step.isPending} disabled={!text.trim()}>
              Send
            </Button>
          </div>
        </form>
      )}

      {session.status === "Proposed" && !revising && (
        <div className="flex flex-wrap justify-end gap-2">
          <Button variant="secondary" disabled={busy} onClick={() => setRevising(true)}>
            Change something
          </Button>
          <Button loading={confirm.isPending} disabled={busy} onClick={() => confirm.mutate(session)}>
            Confirm and create drafts
          </Button>
        </div>
      )}

      {session.status === "Confirmed" && (
        <p role="status" className="flex flex-wrap items-center gap-x-3 gap-y-1 text-sm text-ok">
          The tables were created as drafts.
          <Link href={`/projects/${projectId}/tables`} className="font-medium underline">
            See the tables
          </Link>
          <Link href={`/projects/${projectId}/schema`} className="font-medium underline">
            Review the plan
          </Link>
        </p>
      )}

      {open && (
        <div className="flex justify-start">
          <Button
            variant="ghost"
            disabled={busy}
            onClick={() => step.mutate({ kind: "cancel", session }, { onSuccess: () => router.replace(`/projects/${projectId}/assistant`) })}
          >
            Cancel this conversation
          </Button>
        </div>
      )}
    </Card>
  );
}

function ProposalView({ proposal }: { proposal: SchemaProposal }) {
  return (
    <div className="grid gap-3 rounded-lg border border-accent/40 p-4" data-testid="assistant-proposal">
      <p className="text-sm">{proposal.summary}</p>
      {proposal.newTables.map((table) => (
        <ProposedTableView key={table.name} table={table} />
      ))}
      {proposal.newColumns.length > 0 && (
        <div className="grid gap-1">
          <h3 className="text-sm font-semibold">New columns on existing tables</h3>
          <ul className="grid gap-1 text-sm">
            {proposal.newColumns.map(({ table, column }) => (
              <li key={`${table}.${column.name}`} className="font-mono">
                {table}.{column.name} <span className="text-muted">{describe(column)}</span>
              </li>
            ))}
          </ul>
        </div>
      )}
    </div>
  );
}

function ProposedTableView({ table }: { table: ProposedTable }) {
  return (
    <div className="grid gap-1">
      <div className="flex flex-wrap items-baseline gap-x-3 gap-y-1">
        <h3 className="font-mono font-semibold">{table.name}</h3>
        <span className="text-xs text-muted">
          Read {accessLevelLabels[table.read]} · Write {accessLevelLabels[table.write]} · Realtime {table.realtime ? "on" : "off"}
        </span>
      </div>
      {table.description && <p className="text-xs text-muted">{table.description}</p>}
      <ul className="grid gap-0.5 text-sm">
        <li className="font-mono">
          id <span className="text-muted">BigInt, primary key</span>
        </li>
        {table.columns.map((column) => (
          <li key={column.name} className="font-mono">
            {column.name} <span className="text-muted">{describe(column)}</span>
          </li>
        ))}
      </ul>
    </div>
  );
}

function describe(column: ProposedColumn) {
  const type =
    column.type === "Varchar" ? `Varchar(${column.length})` : column.type === "Decimal" ? `Decimal(${column.precision},${column.scale})` : column.type;
  const parts = [type, column.nullable ? "optional" : "required"];
  if (column.unique) parts.push("unique");
  if (column.default) parts.push(`default ${column.default}`);
  if (column.references) parts.push(`→ ${column.references} (on delete ${column.onDelete})`);
  return parts.join(", ");
}

function Thinking() {
  return (
    <p className="flex items-center gap-2 text-sm text-muted" role="status">
      <Spinner /> The AI is thinking…
    </p>
  );
}
