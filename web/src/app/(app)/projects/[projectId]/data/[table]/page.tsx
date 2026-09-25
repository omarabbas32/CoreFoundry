"use client";

import Link from "next/link";
import { useParams, useRouter } from "next/navigation";
import { useState } from "react";
import { FullPageSpinner } from "@/components/full-page-spinner";
import { Alert, Button, Card, ConfirmDialog, Select } from "@/components/ui";
import { formatCell } from "@/lib/data-form";
import { useDataSchema, useDeleteRow, useProject, useRows } from "@/lib/queries";
import type { DataColumn, DataRow, DataTable } from "@/lib/types";
import { RowPanel } from "./row-panel";

const pageSizes = [10, 25, 50, 100];

export default function DataViewerPage() {
  const params = useParams<{ projectId: string; table: string }>();
  const projectId = Number(params.projectId);
  const tableName = decodeURIComponent(params.table);
  const router = useRouter();
  const project = useProject(projectId);
  const schema = useDataSchema(projectId);

  if (schema.isPending) return <FullPageSpinner label="Loading tables…" />;

  const tables = schema.data?.tables ?? [];
  const table = tables.find((candidate) => candidate.name === tableName);

  return (
    <div className="grid gap-6">
      <div className="grid gap-2">
        <Link href={`/projects/${projectId}`} className="text-sm text-muted hover:text-foreground">
          ← {project.data?.name ?? "Project"}
        </Link>
        <div className="flex flex-wrap items-center justify-between gap-3">
          <div>
            <h1 className="text-2xl font-semibold tracking-tight">Data</h1>
            <p className="text-sm text-muted">
              Rows of the applied tables
              {schema.data && <> (schema version {schema.data.schemaVersion})</>}.
            </p>
          </div>
          {tables.length > 0 && (
            <label className="flex items-center gap-2 text-sm">
              <span className="text-muted">Table</span>
              <Select
                value={table ? tableName : ""}
                onChange={(event) => router.push(`/projects/${projectId}/data/${event.target.value}`)}
                aria-label="Table"
              >
                {!table && <option value="">Choose a table</option>}
                {tables.map((candidate) => (
                  <option key={candidate.name} value={candidate.name}>
                    {candidate.name}
                  </option>
                ))}
              </Select>
            </label>
          )}
        </div>
      </div>

      {schema.error && <Alert>{schema.error.message}</Alert>}
      {schema.data &&
        (table ? (
          <TableData key={table.name} projectId={projectId} table={table} />
        ) : (
          <Card className="grid justify-items-center gap-2 px-6 py-12 text-center">
            <p className="font-medium">
              <span className="font-mono">{tableName}</span> hasn&apos;t been applied yet
            </p>
            <p className="text-sm text-muted">Tables get rows once a plan that creates them is applied.</p>
            <Link href={`/projects/${projectId}/schema`} className="text-sm font-medium text-accent hover:underline">
              Review the plan
            </Link>
          </Card>
        ))}
    </div>
  );
}

type Editing = { row: DataRow | null } | null;

function TableData({ projectId, table }: { projectId: number; table: DataTable }) {
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(25);
  const [sort, setSort] = useState("");
  const [editing, setEditing] = useState<Editing>(null);
  const [deleting, setDeleting] = useState<DataRow | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const rows = useRows(projectId, table.name, page, pageSize, sort, true);
  const remove = useDeleteRow(projectId, table.name);

  const total = rows.data?.total ?? 0;
  const pages = Math.max(1, Math.ceil(total / pageSize));
  const first = total === 0 ? 0 : (page - 1) * pageSize + 1;
  const last = Math.min(page * pageSize, total);

  function sortBy(column: string) {
    setSort(sort === column ? `-${column}` : column); // ascending first, then descending
    setPage(1);
  }

  async function confirmDelete() {
    if (!deleting) return;
    await remove.mutateAsync(deleting.id);
    setNotice(`Deleted row ${deleting.id}.`);
    setDeleting(null);
    if (rows.data?.items.length === 1 && page > 1) setPage(page - 1);
  }

  return (
    <>
      <div className="flex flex-wrap items-center justify-between gap-3">
        <p className="text-sm text-muted" aria-live="polite">
          {rows.data ? (total === 0 ? "No rows" : `Rows ${first}–${last} of ${total}`) : "Loading rows…"}
        </p>
        <Button
          onClick={() => {
            setNotice(null);
            setEditing({ row: null });
          }}
        >
          Add row
        </Button>
      </div>

      {notice && (
        <p role="status" className="rounded-md border border-ok/30 bg-ok-soft px-3 py-2 text-sm text-ok">
          {notice}
        </p>
      )}
      {rows.error && <Alert>{rows.error.message}</Alert>}

      {rows.data && total === 0 ? (
        <Card className="grid justify-items-center gap-3 px-6 py-12 text-center">
          <p className="font-medium">No rows yet</p>
          <p className="text-sm text-muted">Add the first one.</p>
          <Button onClick={() => setEditing({ row: null })}>Add row</Button>
        </Card>
      ) : (
        <Card className="overflow-x-auto">
          <table className="w-full text-sm" data-testid="data-grid">
            <thead className="border-b border-border bg-surface-muted text-left">
              <tr>
                <SortHeader label="id" sort={sort} onSort={sortBy} numeric />
                {table.columns.map((column) => (
                  <SortHeader
                    key={column.name}
                    label={column.name}
                    title={column.type}
                    sort={sort}
                    onSort={sortBy}
                    numeric={isNumeric(column)}
                  />
                ))}
                <th className="px-3 py-2">
                  <span className="sr-only">Actions</span>
                </th>
              </tr>
            </thead>
            <tbody className={rows.isPlaceholderData ? "opacity-60" : undefined}>
              {rows.data?.items.map((row) => (
                <tr key={row.id} className="border-b border-border last:border-0 hover:bg-surface-muted/50">
                  <td className="px-3 py-2 text-right font-mono tabular-nums text-muted">{row.id}</td>
                  {table.columns.map((column) => (
                    <Cell key={column.name} column={column} value={row[column.name]} />
                  ))}
                  <td className="whitespace-nowrap px-3 py-1.5 text-right">
                    <Button
                      variant="ghost"
                      className="h-8"
                      onClick={() => {
                        setNotice(null);
                        setEditing({ row });
                      }}
                      aria-label={`Edit row ${row.id}`}
                    >
                      Edit
                    </Button>
                    <Button
                      variant="ghost"
                      className="h-8 hover:text-danger"
                      onClick={() => {
                        remove.reset();
                        setDeleting(row);
                      }}
                      aria-label={`Delete row ${row.id}`}
                    >
                      Delete
                    </Button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </Card>
      )}

      {total > 0 && (
        <div className="flex flex-wrap items-center justify-between gap-3 text-sm">
          <label className="flex items-center gap-2">
            <span className="text-muted">Rows per page</span>
            <Select
              value={pageSize}
              onChange={(event) => {
                setPageSize(Number(event.target.value));
                setPage(1);
              }}
            >
              {pageSizes.map((size) => (
                <option key={size} value={size}>
                  {size}
                </option>
              ))}
            </Select>
          </label>
          <div className="flex items-center gap-2">
            <Button variant="secondary" disabled={page <= 1} onClick={() => setPage(page - 1)}>
              Previous
            </Button>
            <span className="tabular-nums text-muted">
              Page {page} of {pages}
            </span>
            <Button variant="secondary" disabled={page >= pages} onClick={() => setPage(page + 1)}>
              Next
            </Button>
          </div>
        </div>
      )}

      {editing && (
        <RowPanel
          projectId={projectId}
          table={table}
          row={editing.row}
          onClose={() => setEditing(null)}
          onSaved={(saved, created) => {
            setEditing(null);
            setNotice(created ? `Added row ${saved.id}.` : `Saved row ${saved.id}.`);
          }}
        />
      )}

      <ConfirmDialog
        open={deleting !== null}
        title={`Delete row ${deleting?.id ?? ""}?`}
        description={
          <>
            The row is deleted from <span className="font-mono">{table.name}</span> right away. Rows in other tables that
            reference it are deleted too, set to NULL, or stop the delete, depending on each relation&apos;s on-delete rule.
          </>
        }
        confirmLabel="Delete row"
        danger
        pending={remove.isPending}
        error={remove.error?.message}
        onConfirm={() => void confirmDelete().catch(() => undefined)} // shown in the dialog
        onClose={() => setDeleting(null)}
      />
    </>
  );
}

const isNumeric = (column: DataColumn) =>
  column.dataType === "Int" || column.dataType === "BigInt" || column.dataType === "Decimal";

function SortHeader({
  label,
  title,
  sort,
  onSort,
  numeric = false,
}: {
  label: string;
  title?: string;
  sort: string;
  onSort: (column: string) => void;
  numeric?: boolean;
}) {
  const direction = sort === label || (label === "id" && sort === "") ? "ascending" : sort === `-${label}` ? "descending" : "none";
  return (
    <th scope="col" aria-sort={direction} className={`px-3 py-2 font-medium ${numeric ? "text-right" : ""}`}>
      <button
        type="button"
        title={title}
        onClick={() => onSort(label)}
        className="inline-flex items-center gap-1 font-mono text-xs hover:text-accent"
      >
        {label}
        <span aria-hidden className="text-muted">
          {direction === "ascending" ? "▲" : direction === "descending" ? "▼" : ""}
        </span>
      </button>
    </th>
  );
}

function Cell({ column, value }: { column: DataColumn; value: unknown }) {
  const { text, empty } = formatCell(column, value);
  const base = "max-w-[18rem] truncate px-3 py-2";
  if (empty) return <td className={`${base} text-xs italic text-muted ${isNumeric(column) ? "text-right" : ""}`}>NULL</td>;
  if (isNumeric(column)) return <td className={`${base} text-right font-mono tabular-nums`}>{text}</td>;
  if (column.dataType === "Json") {
    return (
      <td className={base} title={text}>
        <code className="rounded bg-surface-muted px-1 font-mono text-xs">{text}</code>
      </td>
    );
  }
  if (column.dataType === "Bool") return <td className={`${base} font-mono text-xs`}>{text}</td>;
  return (
    <td className={`${base} ${column.dataType === "Uuid" || column.dataType === "DateTime" || column.dataType === "Date" ? "font-mono text-xs" : ""}`} title={text}>
      {text}
    </td>
  );
}
