"use client";

import Link from "next/link";
import { useParams } from "next/navigation";
import { useState } from "react";
import { FullPageSpinner } from "@/components/full-page-spinner";
import { Alert, Badge, Button, Card } from "@/components/ui";
import { useMigration, useMigrations, useProject } from "@/lib/queries";
import type { MigrationStatus, MigrationSummary } from "@/lib/types";

const statusTone = { Applied: "ok", Failed: "danger", Pending: "warn" } as const satisfies Record<MigrationStatus, string>;

export default function HistoryPage() {
  const projectId = Number(useParams<{ projectId: string }>().projectId);
  const project = useProject(projectId);
  const [page, setPage] = useState(1);
  const migrations = useMigrations(projectId, page);

  if (migrations.isPending) return <FullPageSpinner label="Loading history…" />;

  const totalPages = Math.max(1, Math.ceil((migrations.data?.total ?? 0) / (migrations.data?.pageSize ?? 1)));

  return (
    <div className="grid gap-6">
      <div className="grid gap-2">
        <Link href={`/projects/${projectId}/schema`} className="text-sm text-muted hover:text-foreground">
          ← Review plan
        </Link>
        <div>
          <h1 className="text-2xl font-semibold tracking-tight">Schema history</h1>
          <p className="text-sm text-muted">
            Every apply to <span className="font-mono">{project.data?.databaseName}</span>, newest first. A failed apply keeps
            the statements that ran before it; planning again finishes the rest.
          </p>
        </div>
      </div>

      {migrations.error && <Alert>{migrations.error.message}</Alert>}

      {migrations.data?.items.length === 0 && (
        <Card className="px-6 py-12 text-center text-sm text-muted">Nothing has been applied yet.</Card>
      )}

      <ul className="grid gap-2">
        {migrations.data?.items.map((migration) => (
          <MigrationRow key={migration.id} projectId={projectId} migration={migration} />
        ))}
      </ul>

      {totalPages > 1 && (
        <div className="flex items-center justify-center gap-3 text-sm">
          <Button variant="secondary" disabled={page === 1} onClick={() => setPage(page - 1)}>
            Newer
          </Button>
          <span className="text-muted">
            Page {page} of {totalPages}
          </span>
          <Button variant="secondary" disabled={page >= totalPages} onClick={() => setPage(page + 1)}>
            Older
          </Button>
        </div>
      )}
    </div>
  );
}

function MigrationRow({ projectId, migration }: { projectId: number; migration: MigrationSummary }) {
  const [open, setOpen] = useState(false);
  const detail = useMigration(projectId, migration.id, open);
  const when = new Date(migration.completedAt ?? migration.createdAt).toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short" });

  return (
    <li data-testid={`migration-${migration.id}`}>
      <Card className="grid gap-3 px-4 py-3">
        <button type="button" className="flex flex-wrap items-center gap-x-4 gap-y-1 text-left" onClick={() => setOpen(!open)} aria-expanded={open}>
          <span className="font-mono text-sm font-semibold">v{migration.version}</span>
          <Badge tone={statusTone[migration.status]}>{migration.status}</Badge>
          <span className="text-sm text-muted tabular-nums">
            {migration.statementsApplied}/{migration.statementCount} statements
          </span>
          <span className="ml-auto text-sm text-muted">{when}</span>
          <span aria-hidden className="text-muted">
            {open ? "▾" : "▸"}
          </span>
        </button>

        {migration.status === "Failed" && migration.error && (
          <p className="text-sm text-danger">
            Failed at statement {migration.statementsApplied + 1}: {migration.error}
          </p>
        )}

        {open && (
          <div className="grid gap-2">
            {detail.isPending && <p className="text-sm text-muted">Loading SQL…</p>}
            {detail.error && <Alert>{detail.error.message}</Alert>}
            {detail.data?.statements.map((statement, index) => {
              const failed = detail.data.failedStatement === index + 1;
              const skipped = detail.data.failedStatement !== null && index + 1 > detail.data.failedStatement;
              return (
                <div key={index} className="grid gap-1">
                  <span className={`text-xs ${failed ? "font-medium text-danger" : "text-muted"}`}>
                    {index + 1}. {failed ? "failed" : skipped ? "not run" : "applied"}
                  </span>
                  <pre
                    className={`overflow-auto rounded-md border bg-surface-muted p-2 font-mono text-xs ${failed ? "border-danger" : "border-border"} ${skipped ? "opacity-60" : ""}`}
                  >
                    {statement};
                  </pre>
                </div>
              );
            })}
          </div>
        )}
      </Card>
    </li>
  );
}
