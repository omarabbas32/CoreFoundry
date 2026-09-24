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

  if (plan.isPending || project.isPending) return <FullPageSpinner label="Comparing the draft with the database…" />;

  return (
    <div className="grid gap-6">
      <div className="grid gap-2">
        <Link href={`/projects/${projectId}`} className="text-sm text-muted hover:text-foreground">
          ← {project.data?.name ?? "Project"}
        </Link>
        <div className="flex flex-wrap items-center justify-between gap-3">
          <div>
            <h1 className="text-2xl font-semibold tracking-tight">Review plan</h1>
            <p className="text-sm text-muted">
              What applying the draft would change in <span className="font-mono">{project.data?.databaseName}</span>
              {plan.data && <> (schema version {plan.data.schemaVersion})</>}.
            </p>
          </div>
          <div className="flex gap-2">
            <Link
              href={`/projects/${projectId}/schema/history`}
              className="inline-flex h-9 items-center rounded-md border border-border bg-surface px-3.5 text-sm font-medium hover:bg-surface-muted"
            >
              History
            </Link>
            <Button variant="secondary" loading={plan.isFetching} onClick={() => void plan.refetch()}>
              Refresh
            </Button>
          </div>
        </div>
      </div>

      {plan.error && <Alert>{plan.error.message}</Alert>}
      {plan.data && project.data && (
        <PlanView key={plan.data.planHash} plan={plan.data} projectId={projectId} canApply={atLeast(project.data.role, "Admin")} onRefresh={() => void plan.refetch()} />
      )}
    </div>
  );
}

function PlanView({
  plan,
  projectId,
  canApply,
  onRefresh,
}: {
  plan: SchemaPlan;
  projectId: number;
  canApply: boolean;
  onRefresh: () => void;
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
            <h2 className="font-semibold">
              {plan.operations.length} {plan.operations.length === 1 ? "change" : "changes"}
            </h2>
            {[...byTable].map(([table, operations]) => (
              <div key={table} className="grid gap-1.5">
                <h3 className="font-mono text-sm font-semibold">{table}</h3>
                <ul className="grid gap-1">
                  {operations.map((operation, index) => (
                    <li
                      key={`${operation.description}-${index}`}
                      className={`flex flex-wrap items-center justify-between gap-2 rounded-md border border-border border-l-4 bg-surface px-3 py-2 text-sm ${rowTone[toneOf(operation.kind)]}`}
                      data-testid="plan-operation"
                    >
                      <span>{operation.description}</span>
                      {operation.risk !== "Safe" && (
                        <Badge tone={operation.risk === "Destructive" ? "danger" : "warn"}>{operation.risk}</Badge>
                      )}
                    </li>
                  ))}
                </ul>
              </div>
            ))}
          </Card>

          <SqlBlock statements={plan.statements} />

          {canApply ? (
            <ApplyPanel plan={plan} projectId={projectId} onRefresh={onRefresh} />
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
  const sql = statements.map((statement) => `${statement};`).join("\n\n");

  async function copy() {
    await navigator.clipboard.writeText(sql);
    setCopied(true);
    setTimeout(() => setCopied(false), 1500);
  }

  return (
    <Card className="grid gap-3 p-5">
      <div className="flex items-center justify-between gap-2">
        <h2 className="font-semibold">SQL ({statements.length} {statements.length === 1 ? "statement" : "statements"})</h2>
        <Button variant="secondary" className="h-8" onClick={() => void copy().catch(() => undefined)}>
          {copied ? "Copied" : "Copy"}
        </Button>
      </div>
      <pre className="max-h-[28rem] overflow-auto rounded-md border border-border bg-surface-muted p-3 font-mono text-xs leading-relaxed" data-testid="plan-sql">
        {sql}
      </pre>
    </Card>
  );
}

function ApplyPanel({ plan, projectId, onRefresh }: { plan: SchemaPlan; projectId: number; onRefresh: () => void }) {
  const apply = useApply(projectId);
  const [acknowledged, setAcknowledged] = useState(false);
  const [applied, setApplied] = useState<ApplyResult | null>(null);

  const blocked = plan.hasDestructive && !acknowledged;

  async function run() {
    setApplied(await apply.mutateAsync({ planHash: plan.planHash, acknowledgeDestructive: acknowledged }));
  }

  return (
    <Card className="grid gap-4 p-5">
      <h2 className="font-semibold">Apply</h2>
      {applied && (
        <p role="status" className="rounded-md border border-ok/30 bg-ok-soft px-3 py-2 text-sm text-ok">
          Applied as schema version {applied.version} ({applied.statements} statements).{" "}
          <Link href={`/projects/${projectId}/schema/history`} className="underline">
            See history
          </Link>
        </p>
      )}
      {apply.error && <ApplyError error={apply.error} projectId={projectId} onRefresh={onRefresh} />}

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

      <div className="flex items-center gap-3">
        <Button variant={plan.hasDestructive ? "danger" : "primary"} loading={apply.isPending} disabled={blocked} onClick={() => void run().catch(() => undefined)}>
          {apply.isPending ? "Applying…" : `Apply ${plan.statements.length} ${plan.statements.length === 1 ? "statement" : "statements"}`}
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
