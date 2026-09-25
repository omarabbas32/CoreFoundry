"use client";

import Link from "next/link";
import { useParams, useRouter } from "next/navigation";
import { useState } from "react";
import { FullPageSpinner } from "@/components/full-page-spinner";
import { DraftBanner, StateBadge } from "@/components/schema-badges";
import { Alert, Button, Card, ConfirmDialog } from "@/components/ui";
import { ApiError } from "@/lib/api";
import { useProject, useTable, useTableChange, useTables } from "@/lib/queries";
import { limits, rowBytes } from "@/lib/schema-rules";
import type { Column, Table } from "@/lib/types";
import { ColumnDialog } from "./column-dialog";
import { ColumnsGrid } from "./columns-grid";
import { RenameTableDialog } from "./rename-table-dialog";

/** A never-applied column is deleted outright; this keeps what's needed to put it back. */
type DeletedColumn = { column: Column; position: number };

export default function TableDesignerPage() {
  const params = useParams<{ projectId: string; tableId: string }>();
  const projectId = Number(params.projectId);
  const tableId = Number(params.tableId);
  const project = useProject(projectId);
  const table = useTable(projectId, tableId);

  if (table.isPending) return <FullPageSpinner label="Loading table…" />;

  if (table.error) {
    const notFound = table.error instanceof ApiError && table.error.status === 404;
    return (
      <Card className="grid justify-items-center gap-3 px-6 py-14 text-center">
        <p className="font-medium">{notFound ? "Table not found" : "Couldn't load the table"}</p>
        <p className="text-sm text-muted">{notFound ? "It may have been deleted." : table.error.message}</p>
        <Link href={`/projects/${projectId}/tables`} className="text-sm font-medium text-accent hover:underline">
          Back to tables
        </Link>
      </Card>
    );
  }

  return <Designer projectId={projectId} projectName={project.data?.name} table={table.data} onReload={() => table.refetch()} />;
}

function Designer({
  projectId,
  projectName,
  table,
  onReload,
}: {
  projectId: number;
  projectName?: string;
  table: Table;
  onReload: () => void;
}) {
  const router = useRouter();
  const change = useTableChange(projectId, table.id);
  const tables = useTables(projectId);
  const [editing, setEditing] = useState<Column | "new" | null>(null);
  const [renaming, setRenaming] = useState(false);
  const [confirmingDelete, setConfirmingDelete] = useState(false);
  const [deleted, setDeleted] = useState<DeletedColumn | null>(null);

  const tableDropped = table.state === "PendingDrop";
  const editable = !tableDropped;
  const liveColumns = table.columns.filter((column) => column.state !== "PendingDrop");
  const bytes = rowBytes(liveColumns);
  const conflict = change.error instanceof ApiError && change.error.status === 409;

  function run(...args: Parameters<typeof change.mutateAsync>) {
    change.reset();
    return change.mutateAsync(...args);
  }

  async function deleteColumn(column: Column) {
    setDeleted(null);
    const position = table.columns.findIndex((candidate) => candidate.id === column.id);
    await run({ kind: "deleteColumn", columnId: column.id });
    if (column.state === "New") setDeleted({ column, position });
  }

  /** Re-adds a hard-deleted column with the same definition, then moves it back to where it was. */
  async function undoDelete({ column, position }: DeletedColumn) {
    setDeleted(null);
    const { name, dataType, length, precision, scale, isNullable, isUnique, defaultValue, referencesTableId, onDelete } = column;
    const added = await run({
      kind: "addColumn",
      column: { name, dataType, length, precision, scale, isNullable, isUnique, defaultValue, referencesTableId, onDelete },
    });
    const ids = added!.columns.map((candidate) => candidate.id);
    const restoredId = ids.pop()!;
    ids.splice(Math.min(position, ids.length), 0, restoredId);
    await run({ kind: "reorder", columnIds: ids });
  }

  async function deleteTable() {
    const result = await run({ kind: "delete" });
    if (!result) router.replace(`/projects/${projectId}/tables`);
    setConfirmingDelete(false);
  }

  const report = (promise: Promise<unknown>) => void promise.catch(() => undefined); // shown via change.error

  return (
    <div className="grid gap-6">
      <div className="grid gap-2">
        <Link href={`/projects/${projectId}/tables`} className="text-sm text-muted hover:text-foreground">
          ← {projectName ? `${projectName} · tables` : "Tables"}
        </Link>
        <div className="flex flex-wrap items-center justify-between gap-3">
          <div className="flex flex-wrap items-center gap-3">
            <h1 className={`font-mono text-2xl font-semibold tracking-tight ${tableDropped ? "text-muted line-through" : ""}`}>
              {table.name}
            </h1>
            <StateBadge state={table.state} />
          </div>
          <div className="flex gap-2">
            {tableDropped ? (
              <Button variant="secondary" loading={change.isPending} onClick={() => report(run({ kind: "restore" }))}>
                Undo delete
              </Button>
            ) : (
              <>
                <Button variant="secondary" onClick={() => setRenaming(true)}>
                  Rename
                </Button>
                <Button variant="secondary" onClick={() => setConfirmingDelete(true)}>
                  Delete table
                </Button>
              </>
            )}
          </div>
        </div>
      </div>

      <DraftBanner />

      {conflict ? (
        <div role="alert" className="flex flex-wrap items-center justify-between gap-3 rounded-md border border-danger/30 bg-danger-soft px-3 py-2 text-sm text-danger">
          <span>Someone else changed this table. Reload to see their changes, then try again.</span>
          <Button
            variant="secondary"
            onClick={() => {
              change.reset();
              onReload();
            }}
          >
            Reload
          </Button>
        </div>
      ) : (
        change.error && !editing && <Alert>{change.error.message}</Alert>
      )}

      {tableDropped && (
        <p className="rounded-md border border-border bg-surface-muted px-3 py-2 text-sm text-muted">
          This table and its columns will be dropped at the next apply. Undo the delete to edit it again.
        </p>
      )}

      {deleted && (
        <div role="status" className="flex flex-wrap items-center justify-between gap-3 rounded-md border border-border bg-surface px-3 py-2 text-sm">
          <span>
            Deleted column <span className="font-mono">{deleted.column.name}</span>.
          </span>
          <div className="flex gap-2">
            <Button variant="secondary" className="h-8" loading={change.isPending} onClick={() => report(undoDelete(deleted))}>
              Undo
            </Button>
            <Button variant="ghost" className="h-8" onClick={() => setDeleted(null)}>
              Dismiss
            </Button>
          </div>
        </div>
      )}

      <Card className="grid gap-4 p-5">
        <div className="flex flex-wrap items-baseline justify-between gap-2">
          <h2 className="font-semibold">Columns</h2>
          <p className={`text-sm tabular-nums ${bytes > limits.maxRowBytes ? "text-danger" : "text-muted"}`}>
            Row size {bytes.toLocaleString()} / {limits.maxRowBytes.toLocaleString()} bytes
          </p>
        </div>

        <ColumnsGrid
          columns={table.columns}
          editable={editable}
          reordering={change.isPending}
          onReorder={(columnIds) => run({ kind: "reorder", columnIds })}
          onEdit={(column) => setEditing(column)}
          onDelete={(column) => report(deleteColumn(column))}
          onRestore={(column) => report(run({ kind: "restoreColumn", columnId: column.id }))}
        />

        {editable && (
          <div>
            <Button onClick={() => setEditing("new")}>Add column</Button>
          </div>
        )}
      </Card>

      <ColumnDialog
        projectId={projectId}
        tableId={table.id}
        column={editing === "new" ? null : editing}
        tables={tables.data ?? []}
        open={editing !== null}
        onClose={() => setEditing(null)}
      />
      <RenameTableDialog projectId={projectId} table={table} open={renaming} onClose={() => setRenaming(false)} />
      <ConfirmDialog
        open={confirmingDelete}
        title={`Delete ${table.name}?`}
        description={
          table.state === "New"
            ? "It was never applied, so it's removed for good along with its columns."
            : "It will be dropped from the database at the next apply. You can undo this until then."
        }
        confirmLabel={table.state === "New" ? "Delete table" : "Mark for drop"}
        danger
        pending={change.isPending}
        error={change.error?.message}
        onConfirm={() => report(deleteTable())}
        onClose={() => {
          setConfirmingDelete(false);
          change.reset();
        }}
      />
    </div>
  );
}
