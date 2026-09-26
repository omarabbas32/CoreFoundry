"use client";

import Link from "next/link";
import { useParams } from "next/navigation";
import { useState } from "react";
import { FullPageSpinner } from "@/components/full-page-spinner";
import { Alert, Badge, Button, Card } from "@/components/ui";
import { ApiError } from "@/lib/api";
import { useApply, usePlan, useProject } from "@/lib/queries";
import { atLeast, type ApplyResult, type PlanOperation, type SchemaPlan } from "@/lib/types";

/** Colour by what the operation does: adds are green, renames amber, drops red, the rest neutral. */
function toneOf(kind: string) {
  if (kind.startsWith("Drop")) return "danger" as const;
  if (kind.startsWith("Rename")) return "warn" as const;
  if (kind.startsWith("Create") || kind.startsWith("Add")) return "ok" as const;
  return "accent" as const;
}

const rowTone = {
  danger: "border-l-danger",
  warn: "border-l-warn",
  ok: "border-l-ok",
  accent: "border-l-accent",
} as const;

export default function ReviewPlanPage() {
  const projectId = Number(useParams<{ projectId: string }>().projectId);
  const project = useProject(projectId);
  const plan = usePlan(projectId);
  // Lives here, not in the plan view: an apply changes the plan, which re-mounts the view.
  const apply = useApply(projectId);
  const [applied, setApplied] = useState<ApplyResult | null>(null);

  async function runApply(planHash: string, acknowledgeDestructive: boolean) {
    setApplied(null);
    apply.reset();
    setApplied(await apply.mutateAsync({ planHash, acknowledgeDestructive }));
  }

  if (plan.isPending || project.isPending) return <FullPageSpinner label="Comparing the draft with the database…" />;

  return (
    <div className="grid gap-6">
      <div className="grid gap-2">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <div>
            <h1 className="text-2xl font-semibold tracking-tight">Plan &amp; apply</h1>
            <p className="text-sm text-muted">
              What applying the draft would change in <span className="font-mono">{project.data?.databaseName}</span>
              {plan.data && <> (schema version {plan.data.schemaVersion})</>}.
            </p>
          </div>
          <Button variant="secondary" loading={plan.isFetching} onClick={() => void plan.refetch()}>
            Compare again
          </Button>
        </div>
      </div>

      {applied && (
        <div role="status" className="flex flex-wrap items-center justify-between gap-3 rounded-lg border border-ok/30 bg-ok-soft px-4 py-3">
          <div className="grid gap-0.5 text-sm text-ok">
            <p className="font-medium">
              Applied: the database is now at schema version {applied.version} ({applied.statements}{" "}
              {applied.statements === 1 ? "statement" : "statements"}).
            </p>
            {applied.sampleData && (
              <p>
                Added {applied.sampleData.inserted} sample rows.
                {applied.sampleData.skipped.length > 0 && <> Left out: {applied.sampleData.skipped.join("; ")}.</>}
              </p>
            )}
          </div>
          <div className="flex flex-wrap gap-2">
            <Link href={`/projects/${projectId}/schema/history`} className={secondaryLink}>
              History
            </Link>
            <Link href={`/projects/${projectId}/api`} className={secondaryLink}>
              API &amp; export
            </Link>
            <Link href={`/projects/${projectId}/data`} className={primaryLink}>
              Browse data
            </Link>
          </div>
        </div>
      )}
      {apply.error && <ApplyError error={apply.error} projectId={projectId} onRefresh={() => void plan.refetch()} />}

      {plan.error && <Alert>{plan.error.message}</Alert>}
      {plan.data && project.data && (
        <PlanView
          key={plan.data.planHash}
          plan={plan.data}
          canApply={atLeast(project.data.role, "Admin")}
          applying={apply.isPending}
          onApply={(acknowledge) => runApply(plan.data.planHash, acknowledge)}
        />
      )}
    </div>
  );
}

function PlanView({
  plan,
  canApply,
  applying,
  onApply,
}: {
  plan: SchemaPlan;
  canApply: boolean;
  applying: boolean;
  onApply: (acknowledgeDestructive: boolean) => Promise<void>;
}) {
  const byTable = new Map<string, PlanOperation[]>();
  for (const operation of plan.operations) {
    byTable.set(operation.table, [...(byTable.get(operation.table) ?? []), operation]);
  }

  return (
    <>
      {plan.warnings.length > 0 && (
        <div role="alert" className="grid gap-1 rounded-md border border-warn/30 bg-warn-soft px-3 py-2 text-sm text-warn">
          <p className="font-medium">Check before applying</p>
          <ul className="list-disc pl-5">
            {plan.warnings.map((warning) => (
              <li key={warning}>{warning}</li>
            ))}
          </ul>
        </div>
      )}

      {(plan.unmanagedTables.length > 0 || plan.unmanagedColumns.length > 0) && (
        <p className="rounded-md border border-border bg-surface-muted px-3 py-2 text-sm text-muted">
          Not managed by CoreFoundry (left alone, never dropped):{" "}
          <span className="font-mono">{[...plan.unmanagedTables, ...plan.unmanagedColumns].join(", ")}</span>
        </p>
      )}

      {plan.operations.length === 0 ? (
        <Card className="grid justify-items-center gap-2 px-6 py-12 text-center">
          <p className="font-medium">Nothing to apply</p>
          <p className="text-sm text-muted">The database already matches the draft.</p>
        </Card>
      ) : (
        <>
          <Card className="grid gap-4 p-5">
            <div className="grid gap-1">
              <h2 className="font-semibold">
                {plan.operations.length} {plan.operations.length === 1 ? "change" : "changes"} in {byTable.size}{" "}
                {byTable.size === 1 ? "table" : "tables"}
              </h2>
              <p className="text-sm text-muted">{summarize(plan.operations)}</p>
            </div>
            {[...byTable].map(([table, operations]) => (
              <div key={table} className="grid gap-1.5">
                <h3 className="font-mono text-sm font-semibold">{table}</h3>
                <ul className="grid gap-1">
                  {operations.map((operation, index) => (
                    <li
                      key={`${operation.description}-${index}`}
                      className={`grid gap-1 rounded-md border border-border border-l-4 bg-surface px-3 py-2 text-sm ${rowTone[toneOf(operation.kind)]}`}
                      data-testid="plan-operation"
                    >
                      <span className="flex flex-wrap items-center justify-between gap-2">
                        <span>{operation.description}</span>
                        {operation.risk !== "Safe" && (
                          <Badge tone={operation.risk === "Destructive" ? "danger" : "warn"}>
                            {operation.risk === "Destructive" ? "Data loss" : "Risky"}
                          </Badge>
                        )}
                      </span>
                      {operation.riskReason && (
                        <span className={`text-xs ${operation.risk === "Destructive" ? "text-danger" : "text-warn"}`}>
                          {operation.riskReason}
                        </span>
                      )}
                    </li>
                  ))}
                </ul>
              </div>
            ))}
          </Card>

          <SqlBlock statements={plan.statements} />

          {canApply ? (
            <ApplyPanel plan={plan} applying={applying} onApply={onApply} />
          ) : (
            <p className="text-sm text-muted">Only admins and the owner can apply the plan.</p>
          )}
        </>
      )}
    </>
  );
}

function SqlBlock({ statements }: { statements: string[] }) {
  const [copied, setCopied] = useState(false);
  const [open, setOpen] = useState(statements.length <= 8);
  const sql = statements.map((statement) => `${statement};`).join("\n\n");

  async function copy() {
    await navigator.clipboard.writeText(sql);
    setCopied(true);
    setTimeout(() => setCopied(false), 1500);
  }

  return (
    <Card className="grid gap-3 p-5">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h2 className="font-semibold">
          SQL <span className="font-normal text-muted">({statements.length} {statements.length === 1 ? "statement" : "statements"})</span>
        </h2>
        <div className="flex gap-2">
          <Button variant="ghost" className="h-8" aria-expanded={open} onClick={() => setOpen((value) => !value)}>
            {open ? "Hide SQL" : "Show SQL"}
          </Button>
          <Button variant="secondary" className="h-8" onClick={() => void copy().catch(() => undefined)}>
            {copied ? "Copied" : "Copy"}
          </Button>
        </div>
      </div>
      {open ? (
        <pre className="max-h-[28rem] overflow-auto rounded-md border border-border bg-surface-muted p-3 font-mono text-xs leading-relaxed" data-testid="plan-sql">
          {statements.map((statement, index) => (
            <span key={index} className="block pb-3 last:pb-0">
              <HighlightedSql sql={`${statement};`} />
            </span>
          ))}
        </pre>
      ) : (
        <p className="text-sm text-muted">Exactly these statements run, in this order. Show them to check before applying.</p>
      )}
    </Card>
  );
}

/** The outcome (success or error) is shown by the page, which outlives this panel. */
function ApplyPanel({
  plan,
  applying,
  onApply,
}: {
  plan: SchemaPlan;
  applying: boolean;
  onApply: (acknowledgeDestructive: boolean) => Promise<void>;
}) {
  const [acknowledged, setAcknowledged] = useState(false);
  const blocked = plan.hasDestructive && !acknowledged;

  return (
    <Card className="grid gap-4 p-5">
      <h2 className="font-semibold">Apply</h2>

      {plan.hasDestructive && (
        <label className="flex items-start gap-2 text-sm">
          <input
            type="checkbox"
            className="mt-0.5 size-4 accent-danger"
            checked={acknowledged}
            onChange={(event) => setAcknowledged(event.target.checked)}
          />
          <span>
            I understand that this plan <span className="font-medium text-danger">deletes or may truncate data</span>, and
            that it can&apos;t be undone.
          </span>
        </label>
      )}

      <p className="text-sm text-muted">{summarize(plan.operations)}</p>
      <div className="flex flex-wrap items-center gap-3">
        <Button
          variant={plan.hasDestructive ? "danger" : "primary"}
          loading={applying}
          disabled={blocked}
          onClick={() => void onApply(acknowledged).catch(() => undefined)} // the page shows the error
        >
          {applying ? "Applying…" : `Apply ${plan.statements.length} ${plan.statements.length === 1 ? "statement" : "statements"}`}
        </Button>
        <span className="text-xs text-muted">Runs exactly the SQL above, one statement at a time.</span>
      </div>
    </Card>
  );
}

function ApplyError({ error, projectId, onRefresh }: { error: Error; projectId: number; onRefresh: () => void }) {
  const problem = error instanceof ApiError ? error : null;

  if (problem?.problemType === "plan-stale") {
    return (
      <div role="alert" className="flex flex-wrap items-center justify-between gap-2 rounded-md border border-danger/30 bg-danger-soft px-3 py-2 text-sm text-danger">
        <span>The draft changed since you reviewed it. Review again.</span>
        <Button variant="secondary" className="h-8" onClick={onRefresh}>
          Review again
        </Button>
      </div>
    );
  }

  if (problem?.problemType === "apply-in-progress") {
    return <Alert>Someone else is applying changes to this project. Try again in a moment.</Alert>;
  }

  if (problem?.problemType === "apply-failed") {
    const { failedStatement, statement, error: mysqlError } = problem.problem as {
      failedStatement?: number;
      statement?: string;
      error?: string;
    };
    return (
      <div role="alert" className="grid gap-2 rounded-md border border-danger/30 bg-danger-soft px-3 py-2 text-sm text-danger">
        <p>
          <span className="font-medium">Statement {failedStatement} failed:</span> {mysqlError}
        </p>
        <pre className="overflow-auto rounded bg-surface p-2 font-mono text-xs text-foreground">{statement}</pre>
        <p>
          The statements before it were applied. Fix the cause, then review the plan again: it will only contain what&apos;s left.{" "}
          <Link href={`/projects/${projectId}/schema/history`} className="underline">
            See history
          </Link>
        </p>
      </div>
    );
  }

  return <Alert>{error.message}</Alert>;
}

const primaryLink =
  "inline-flex h-9 items-center rounded-md bg-accent-solid px-3.5 text-sm font-medium text-on-accent hover:bg-accent-solid-hover";
const secondaryLink =
  "inline-flex h-9 items-center rounded-md border border-border bg-surface px-3.5 text-sm font-medium hover:bg-surface-muted";

/** "Adds 2 tables and 5 columns · renames 1 · drops 1 column": the plan in one line, loss first if any. */
function summarize(operations: PlanOperation[]) {
  const count = (test: (kind: string) => boolean) => operations.filter((operation) => test(operation.kind)).length;
  const plural = (n: number, noun: string) => `${n} ${noun}${n === 1 ? "" : "s"}`;
  const tables = count((kind) => kind === "CreateTable");
  const columns = count((kind) => kind === "AddColumn");
  const droppedTables = count((kind) => kind === "DropTable");
  const droppedColumns = count((kind) => kind === "DropColumn");
  const drops = droppedTables + droppedColumns;
  const renames = count((kind) => kind.startsWith("Rename"));
  const others = operations.length - tables - columns - drops - renames;

  const parts: string[] = [];
  if (drops > 0) {
    const dropped = [droppedTables > 0 ? plural(droppedTables, "table") : null, droppedColumns > 0 ? plural(droppedColumns, "column") : null];
    parts.push(`drops ${dropped.filter(Boolean).join(" and ")} (data loss)`);
  }
  if (tables > 0 || columns > 0) {
    parts.push(`adds ${[tables > 0 ? plural(tables, "table") : null, columns > 0 ? plural(columns, "column") : null].filter(Boolean).join(" and ")}`);
  }
  if (renames > 0) parts.push(`renames ${renames}`);
  if (others > 0) parts.push(`${plural(others, "other change")} (types, keys)`);
  const text = parts.join(" · ");
  return text.charAt(0).toUpperCase() + text.slice(1) + ".";
}

const sqlKeywords =
  /\b(CREATE|ALTER|DROP|TABLE|ADD|COLUMN|CONSTRAINT|PRIMARY|FOREIGN|KEY|REFERENCES|UNIQUE|INDEX|MODIFY|RENAME|TO|NOT|NULL|DEFAULT|AUTO_INCREMENT|ON|DELETE|CASCADE|RESTRICT|SET|ENGINE|CHARSET|COLLATE|IF|EXISTS)\b/g;

/** Keywords in the accent color and DROP in red, so what a statement does stands out; the text stays exact. */
function HighlightedSql({ sql }: { sql: string }) {
  const parts = sql.split(sqlKeywords);
  return (
    <>
      {parts.map((part, index) =>
        index % 2 === 1 ? (
          <span key={index} className={part === "DROP" ? "font-semibold text-danger" : "text-accent"}>
            {part}
          </span>
        ) : (
          part
        ),
      )}
    </>
  );
}
