"use client";

import Link from "next/link";
import { useParams } from "next/navigation";
import { useState } from "react";
import { FullPageSpinner } from "@/components/full-page-spinner";
import { DraftBanner, StateBadge } from "@/components/schema-badges";
import { Alert, Button, Card } from "@/components/ui";
import { useProject, useTables } from "@/lib/queries";
import { CreateTableDialog } from "./create-table-dialog";

export default function TablesPage() {
  const projectId = Number(useParams<{ projectId: string }>().projectId);
  const project = useProject(projectId);
  const tables = useTables(projectId);
  const [creating, setCreating] = useState(false);

  if (tables.isPending) return <FullPageSpinner label="Loading tables…" />;

  return (
    <div className="grid gap-6">
      <div className="grid gap-2">
        <Link href={`/projects/${projectId}`} className="text-sm text-muted hover:text-foreground">
          ← {project.data?.name ?? "Project"}
        </Link>
        <div className="flex flex-wrap items-center justify-between gap-3">
          <div>
            <h1 className="text-2xl font-semibold tracking-tight">Tables</h1>
            <p className="text-sm text-muted">
              Every table gets an <span className="font-mono">id BIGINT</span> primary key; you add the rest.
            </p>
          </div>
          <div className="flex gap-2">
            <Link
              href={`/projects/${projectId}/tables/diagram`}
              className="inline-flex h-9 items-center rounded-md border border-border bg-surface px-3.5 text-sm font-medium hover:bg-surface-muted"
            >
              Diagram
            </Link>
            <Button onClick={() => setCreating(true)} disabled={!tables.data}>
              New table
            </Button>
          </div>
        </div>
      </div>

      <DraftBanner projectId={projectId} />
      {tables.error && <Alert>{tables.error.message}</Alert>}

      {tables.data?.length === 0 && (
        <Card className="grid justify-items-center gap-3 px-6 py-14 text-center">
          <p className="font-medium">No tables yet</p>
          <p className="max-w-sm text-sm text-muted">Design your first table, for example authors or books.</p>
          <Button onClick={() => setCreating(true)}>Create a table</Button>
        </Card>
      )}

      {tables.data && tables.data.length > 0 && (
        <ul className="grid gap-3">
          {tables.data.map((table) => (
            <li key={table.id}>
              <Link href={`/projects/${projectId}/tables/${table.id}`}>
                <Card className="flex flex-wrap items-center gap-x-4 gap-y-2 px-4 py-3 transition-colors hover:border-accent/50">
                  <span
                    className={`min-w-0 flex-1 truncate font-mono font-medium ${table.state === "PendingDrop" ? "text-muted line-through" : ""}`}
                  >
                    {table.name}
                  </span>
                  <span className="text-sm text-muted tabular-nums">
                    {table.columnCount + 1} {table.columnCount === 0 ? "column" : "columns"}
                  </span>
                  <StateBadge state={table.state} />
                </Card>
              </Link>
            </li>
          ))}
        </ul>
      )}

      <CreateTableDialog projectId={projectId} open={creating} onClose={() => setCreating(false)} />
    </div>
  );
}
