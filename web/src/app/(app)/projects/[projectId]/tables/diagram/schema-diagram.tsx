"use client";

import {
  Background,
  Controls,
  Handle,
  MarkerType,
  MiniMap,
  Position,
  ReactFlow,
  useNodesState,
  type Edge,
  type Node,
  type NodeProps,
} from "@xyflow/react";
import "@xyflow/react/dist/style.css";
import Link from "next/link";
import { typeLabel } from "../[tableId]/columns-grid";
import type { Table } from "@/lib/types";

type TableNode = Node<{ table: Table; projectId: number }, "table">;

const columnWidth = 300;
const rowHeight = 28;
const headerHeight = 40;
const gap = 48;

const onDeleteText = { Restrict: "restrict", Cascade: "cascade", SetNull: "set null" } as const;

/**
 * Lays tables out left to right by how deep their references go: tables that reference nothing
 * come first, a table that references them comes after, and so on. Cycles are cut where found.
 * Edges therefore run right to left: from a column's left edge to the referenced id row's right edge.
 */
function layout(tables: Table[], projectId: number): TableNode[] {
  const byId = new Map(tables.map((table) => [table.id, table]));
  const rank = new Map<number, number>();

  function rankOf(table: Table, visiting: Set<number>): number {
    const known = rank.get(table.id);
    if (known !== undefined) return known;
    visiting.add(table.id);
    let result = 0;
    for (const column of table.columns) {
      const target = column.referencesTableId === null ? undefined : byId.get(column.referencesTableId);
      if (target && target.id !== table.id && !visiting.has(target.id)) {
        result = Math.max(result, rankOf(target, visiting) + 1);
      }
    }
    visiting.delete(table.id);
    rank.set(table.id, result);
    return result;
  }

  const nextY = new Map<number, number>();
  return tables.map((table) => {
    const r = rankOf(table, new Set());
    const y = nextY.get(r) ?? 0;
    nextY.set(r, y + headerHeight + rowHeight * (table.columns.length + 1) + gap);
    return { id: String(table.id), type: "table", position: { x: r * (columnWidth + 180), y }, data: { table, projectId } };
  });
}

/** One edge per reference column: from its row to the referenced table's id row. */
function edgesFor(tables: Table[]): Edge[] {
  const ids = new Set(tables.map((table) => table.id));
  return tables.flatMap((table) =>
    table.columns
      .filter((column) => column.referencesTableId !== null && ids.has(column.referencesTableId))
      .map((column) => {
        const dropped = table.state === "PendingDrop" || column.state === "PendingDrop";
        return {
          id: `ref-${column.id}`,
          source: String(table.id),
          sourceHandle: `column-${column.id}`,
          target: String(column.referencesTableId),
          targetHandle: "id",
          type: "smoothstep",
          label: column.onDelete ? `on delete ${onDeleteText[column.onDelete]}` : undefined,
          markerEnd: { type: MarkerType.ArrowClosed },
          style: dropped ? { strokeDasharray: "4 4", opacity: 0.5 } : undefined,
          data: { column: `${table.name}.${column.name}` },
        } satisfies Edge;
      }),
  );
}

function TableNodeView({ data }: NodeProps<TableNode>) {
  const { table, projectId } = data;
  const tableDropped = table.state === "PendingDrop";

  return (
    <div
      className="rounded-md border border-border bg-surface text-xs text-foreground shadow-sm"
      style={{ width: columnWidth }}
      data-testid={`diagram-table-${table.name}`}
    >
      <div className="flex items-center justify-between gap-2 rounded-t-md border-b border-border bg-surface-muted px-3" style={{ height: headerHeight }}>
        <span className={`truncate font-mono text-sm font-semibold ${tableDropped ? "text-muted line-through" : ""}`}>{table.name}</span>
        <Link href={`/projects/${projectId}/tables/${table.id}`} className="nodrag shrink-0 text-accent hover:underline">
          Open
        </Link>
      </div>

      <div className="relative flex items-center justify-between px-3 text-muted" style={{ height: rowHeight }}>
        <Handle type="target" position={Position.Right} id="id" />
        <span className="font-mono">🔑 id</span>
        <span>BigInt</span>
      </div>

      {table.columns.map((column) => {
        const dropped = tableDropped || column.state === "PendingDrop";
        return (
          <div
            key={column.id}
            className={`relative flex items-center justify-between gap-2 border-t border-border px-3 ${dropped ? "text-muted line-through" : ""}`}
            style={{ height: rowHeight }}
          >
            <span className="truncate font-mono">{column.name}</span>
            <span className={`shrink-0 ${column.referencesTableName && !dropped ? "font-medium text-accent" : "text-muted"}`}>
              {typeLabel(column)}
              {!column.isNullable && " · not null"}
            </span>
            {column.referencesTableId !== null && <Handle type="source" position={Position.Left} id={`column-${column.id}`} />}
          </div>
        );
      })}
    </div>
  );
}

const nodeTypes = { table: TableNodeView };

/** Pan, zoom and drag the tables around; positions start from the automatic layout (they aren't saved). */
export function SchemaDiagram({ tables, projectId }: { tables: Table[]; projectId: number }) {
  const [nodes, , onNodesChange] = useNodesState(layout(tables, projectId));

  return (
    <div className="h-[70vh] min-h-96 overflow-hidden rounded-lg border border-border">
      <ReactFlow
        nodes={nodes}
        edges={edgesFor(tables)}
        nodeTypes={nodeTypes}
        onNodesChange={onNodesChange}
        nodesConnectable={false}
        colorMode="system"
        fitView
        minZoom={0.2}
      >
        <Background />
        <Controls showInteractive={false} />
        <MiniMap pannable zoomable />
      </ReactFlow>
    </div>
  );
}
