import { accessLevelLabels } from "@/components/access-controls";
import { Badge } from "@/components/ui";
import type { ProposedColumn, ProposedTable, SchemaProposal } from "@/lib/types";

/** The proposal as the user will get it: one card per new table, then the columns added to existing tables. */
export function ProposalView({ proposal, confirmed }: { proposal: SchemaProposal; confirmed: boolean }) {
  const columnCount =
    proposal.newTables.reduce((sum, table) => sum + table.columns.length, 0) + proposal.newColumns.length;
  const byTable = new Map<string, SchemaProposal["newColumns"]>();
  for (const addition of proposal.newColumns) {
    byTable.set(addition.table, [...(byTable.get(addition.table) ?? []), addition]);
  }

  return (
    <div className="grid gap-4 rounded-xl border border-accent/40 bg-accent-soft/30 p-4" data-testid="assistant-proposal">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h2 className="font-semibold">{confirmed ? "Created as drafts" : "Proposed changes"}</h2>
        <p className="text-xs text-muted tabular-nums">
          {count(proposal.newTables.length, "new table")} · {count(columnCount, "column")}
          {byTable.size > 0 && ` · ${count(byTable.size, "existing table")} extended`}
        </p>
      </div>

      <div className="grid gap-3 lg:grid-cols-2">
        {proposal.newTables.map((table) => (
          <TableCard key={table.name} table={table} />
        ))}
        {[...byTable].map(([table, additions]) => (
          <section key={table} className="grid content-start gap-2 rounded-lg border border-dashed border-border bg-surface p-3">
            <div className="flex flex-wrap items-center gap-2">
              <h3 className="font-mono text-sm font-semibold">{table}</h3>
              <Badge>existing · new columns</Badge>
            </div>
            <ColumnList columns={additions.map((addition) => addition.column)} />
          </section>
        ))}
      </div>
    </div>
  );
}

function TableCard({ table }: { table: ProposedTable }) {
  return (
    <section className="grid content-start gap-2 rounded-lg border border-border bg-surface p-3">
      <div className="flex flex-wrap items-center gap-x-2 gap-y-1">
        <h3 className="font-mono text-sm font-semibold">{table.name}</h3>
        <Badge tone="accent">new</Badge>
      </div>
      {table.description && <p className="text-xs text-muted">{table.description}</p>}
      <div className="flex flex-wrap gap-1.5 text-xs">
        <Badge>Read: {accessLevelLabels[table.read]}</Badge>
        <Badge>Write: {accessLevelLabels[table.write]}</Badge>
        <Badge tone={table.realtime ? "ok" : "neutral"}>Realtime {table.realtime ? "on" : "off"}</Badge>
      </div>
      <ColumnList columns={table.columns} withId />
    </section>
  );
}

/** Name, type and what matters about each column; references point at their table. */
function ColumnList({ columns, withId = false }: { columns: ProposedColumn[]; withId?: boolean }) {
  return (
    <ul className="grid divide-y divide-border rounded-md border border-border text-xs">
      {withId && (
        <li className="flex items-center justify-between gap-3 px-2.5 py-1.5">
          <span className="font-mono font-medium">id</span>
          <span className="text-muted">BigInt · primary key</span>
        </li>
      )}
      {columns.map((column) => (
        <li key={column.name} className="flex flex-wrap items-center justify-between gap-x-3 gap-y-0.5 px-2.5 py-1.5">
          <span className="font-mono font-medium">
            {column.name}
            {!column.nullable && <span className="text-danger" title="Required"> *</span>}
          </span>
          <span className="flex flex-wrap items-center justify-end gap-1.5 text-muted">
            <span>{type(column)}</span>
            {column.unique && <span className="rounded bg-surface-muted px-1">unique</span>}
            {column.default && <span className="rounded bg-surface-muted px-1">= {column.default}</span>}
            {column.references && (
              <span className="rounded bg-accent-soft px-1 text-accent" title={`On delete: ${column.onDelete}`}>
                → {column.references}
              </span>
            )}
          </span>
        </li>
      ))}
    </ul>
  );
}

function type(column: ProposedColumn) {
  if (column.type === "Varchar") return `Varchar(${column.length})`;
  if (column.type === "Decimal") return `Decimal(${column.precision},${column.scale})`;
  return column.type;
}

function count(n: number, noun: string) {
  return `${n} ${noun}${n === 1 ? "" : "s"}`;
}
