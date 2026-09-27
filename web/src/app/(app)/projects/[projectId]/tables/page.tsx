"use client";

import Link from "next/link";
import { useParams } from "next/navigation";
import { useState } from "react";
import { PageSkeleton } from "@/components/page-skeleton";
import { accessLevelLabels } from "@/components/access-controls";
import { DraftBanner, StateBadge } from "@/components/schema-badges";
import { Alert, Badge, Button, Card, Input } from "@/components/ui";
import { useTables } from "@/lib/queries";
import type { TableSummary } from "@/lib/types";
import { CreateTableDialog } from "./create-table-dialog";
import { TemplateDialog } from "./template-dialog";

export default function TablesPage() {
  const projectId = Number(useParams<{ projectId: string }>().projectId);
  const tables = useTables(projectId);
  const [creating, setCreating] = useState(false);
  const [choosingTemplate, setChoosingTemplate] = useState(false);
  const [search, setSearch] = useState("");

  if (tables.isPending) return <PageSkeleton label="Loading tables…" variant="list" />;

  return (
    <div className="grid gap-6">
      <div className="grid gap-2">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <div>
            <h1 className="text-2xl font-semibold tracking-tight">Tables</h1>
            <p className="text-sm text-muted">
              Every table gets an <span className="font-mono">id BIGINT</span> primary key; you add the rest.
            </p>
          </div>
          <div className="flex gap-2">
            {/* Always shown (even if the list failed to load); the assistant page says "Extend" once there are tables. */}
            <Link
              href={`/projects/${projectId}/assistant`}
              className="inline-flex h-9 items-center rounded-md border border-border bg-surface px-3.5 text-sm font-medium hover:bg-surface-muted"
            >
              {tables.data && tables.data.length > 0 ? "Extend with AI" : "Design with AI"}
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
          <p className="max-w-sm text-sm text-muted">
            Design your first table, for example authors or books, start from a ready schema, or let the AI assistant
            design one with you.
          </p>
          <div className="flex flex-wrap justify-center gap-2">
            <Button onClick={() => setCreating(true)}>Create a table</Button>
            <Button variant="secondary" onClick={() => setChoosingTemplate(true)}>
              Start from a template
            </Button>
            <Link href={`/projects/${projectId}/assistant`} className="inline-flex h-9 items-center rounded-md border border-border bg-surface px-3.5 text-sm font-medium hover:bg-surface-muted">
              Design with AI
            </Link>
          </div>
        </Card>
      )}

      {tables.data && tables.data.length > 0 && (
        <TableList projectId={projectId} tables={tables.data} search={search} onSearch={setSearch} />
      )}

      <CreateTableDialog projectId={projectId} open={creating} onClose={() => setCreating(false)} />
      <TemplateDialog projectId={projectId} open={choosingTemplate} onClose={() => setChoosingTemplate(false)} />
    </div>
  );
}

/** The tables as one list: name and state, size, and how the exported API exposes each. Searchable when long. */
function TableList({
  projectId,
  tables,
  search,
  onSearch,
}: {
  projectId: number;
  tables: TableSummary[];
  search: string;
  onSearch: (value: string) => void;
}) {
  const query = search.trim().toLowerCase();
  const shown = query ? tables.filter((table) => table.name.includes(query)) : tables;

  return (
    <Card className="grid">
      {tables.length > 6 && (
        <div className="flex flex-wrap items-center justify-between gap-3 border-b border-border p-3">
          <label htmlFor="table-search" className="sr-only">
            Find a table
          </label>
          <Input
            id="table-search"
            type="search"
            placeholder="Find a table…"
            className="max-w-xs"
            value={search}
            onChange={(event) => onSearch(event.target.value)}
          />
          <span className="text-xs text-muted tabular-nums">
            {shown.length} of {tables.length} tables
          </span>
        </div>
      )}
      <ul className="divide-y divide-border">
        {shown.map((table) => {
          const dropped = table.state === "PendingDrop";
          return (
            <li key={table.id}>
              <Link
                href={`/projects/${projectId}/tables/${table.id}`}
                className="group flex flex-wrap items-center gap-x-4 gap-y-1.5 px-4 py-3 transition-colors hover:bg-surface-muted"
              >
                <span className="flex min-w-0 flex-1 items-center gap-2">
                  <span className={`truncate font-mono font-medium ${dropped ? "text-muted line-through" : "group-hover:text-accent"}`}>
                    {table.name}
                  </span>
                  {table.state !== "Applied" && <StateBadge state={table.state} />}
                </span>
                <span className="flex flex-wrap items-center gap-1.5 text-xs text-muted">
                  <span className="tabular-nums">
                    {table.columnCount + 1} {table.columnCount === 0 ? "column" : "columns"}
                  </span>
                  <span aria-hidden>·</span>
                  <span>
                    Read {accessLevelLabels[table.readAccess]}, write {accessLevelLabels[table.writeAccess]}
                  </span>
                  {!table.realtime && <Badge>Realtime off</Badge>}
                </span>
                <span aria-hidden className="text-muted group-hover:text-accent">
                  →
                </span>
              </Link>
            </li>
          );
        })}
        {shown.length === 0 && <li className="px-4 py-6 text-center text-sm text-muted">No table matches “{search}”.</li>}
      </ul>
    </Card>
  );
}
