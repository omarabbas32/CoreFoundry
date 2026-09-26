"use client";

import {
  closestCenter,
  DndContext,
  KeyboardSensor,
  PointerSensor,
  useSensor,
  useSensors,
  type DragEndEvent,
} from "@dnd-kit/core";
import { arrayMove, SortableContext, sortableKeyboardCoordinates, useSortable, verticalListSortingStrategy } from "@dnd-kit/sortable";
import { CSS } from "@dnd-kit/utilities";
import { useState } from "react";
import { Button } from "@/components/ui";
import type { Column } from "@/lib/types";

const onDeleteText = { Restrict: "restrict", Cascade: "cascade", SetNull: "set null" } as const;

// Handle · name · type · constraints · default · actions. Scrolls sideways only on narrow screens.
const rowGrid = "grid grid-cols-[2rem_minmax(9rem,1.3fr)_minmax(8rem,1fr)_minmax(8rem,1fr)_minmax(6rem,0.9fr)_7.5rem] items-center gap-x-3";

/** A small tag for what a column enforces. */
function Tag({ children, tone = "neutral" }: { children: string; tone?: "neutral" | "accent" }) {
  return (
    <span className={`rounded px-1.5 py-0.5 text-xs ${tone === "accent" ? "bg-accent-soft text-accent" : "bg-surface-muted text-muted"}`}>
      {children}
    </span>
  );
}

export function typeLabel(column: Pick<Column, "dataType" | "length" | "precision" | "scale" | "referencesTableName">) {
  if (column.referencesTableName) return `BigInt → ${column.referencesTableName}`;
  if (column.dataType === "Varchar") return `Varchar(${column.length})`;
  if (column.dataType === "Decimal") return `Decimal(${column.precision},${column.scale})`;
  return column.dataType;
}

/**
 * The column list: the system id row, then the user's columns, reorderable by dragging the handle
 * (mouse, touch or keyboard: focus the handle, press Space, move with the arrow keys, Space again).
 */
export function ColumnsGrid({
  columns,
  editable,
  reordering,
  onReorder,
  onEdit,
  onDelete,
  onRestore,
}: {
  columns: Column[];
  editable: boolean;
  reordering: boolean;
  onReorder: (columnIds: number[]) => Promise<unknown>;
  onEdit: (column: Column) => void;
  onDelete: (column: Column) => void;
  onRestore: (column: Column) => void;
}) {
  // While a reorder is being saved, show the new order instead of snapping back to the old one.
  const [pendingOrder, setPendingOrder] = useState<number[] | null>(null);
  const sensors = useSensors(
    useSensor(PointerSensor, { activationConstraint: { distance: 4 } }),
    useSensor(KeyboardSensor, { coordinateGetter: sortableKeyboardCoordinates }),
  );

  const byId = new Map(columns.map((column) => [column.id, column]));
  const ordered = (pendingOrder ?? columns.map((column) => column.id))
    .map((id) => byId.get(id))
    .filter((column): column is Column => column !== undefined);

  function handleDragEnd({ active, over }: DragEndEvent) {
    if (!over || active.id === over.id) return;
    const ids = ordered.map((column) => column.id);
    const next = arrayMove(ids, ids.indexOf(Number(active.id)), ids.indexOf(Number(over.id)));
    setPendingOrder(next);
    void onReorder(next)
      .catch(() => undefined) // the page shows the error; the grid falls back to the saved order
      .finally(() => setPendingOrder(null));
  }

  return (
    <>
      {/* Phones: a stacked list (reordering needs a wider screen). */}
      <ul className="grid divide-y divide-border rounded-md border border-border text-sm sm:hidden">
        <li className="flex items-center justify-between gap-2 px-3 py-2 text-muted">
          <span className="font-mono">id</span>
          <span className="text-xs">BigInt · primary key</span>
        </li>
        {ordered.map((column) => {
          const dropped = column.state === "PendingDrop";
          return (
            <li key={column.id} className="grid gap-1.5 px-3 py-2.5" data-testid={`column-card-${column.name}`}>
              <div className="flex items-center justify-between gap-2">
                <span className={`truncate font-mono font-medium ${dropped ? "text-muted line-through" : ""}`}>{column.name}</span>
                {column.state === "New" && <span className="text-xs text-accent">new</span>}
              </div>
              <div className="flex flex-wrap items-center gap-1.5 text-xs text-muted">
                <span className={column.referencesTableName ? "font-medium text-accent" : ""}>{typeLabel(column)}</span>
                {!column.isNullable && <Tag tone="accent">required</Tag>}
                {column.isUnique && <Tag tone="accent">unique</Tag>}
                {column.defaultValue && <Tag>{`= ${column.defaultValue}`}</Tag>}
              </div>
              {editable && (
                <div className="flex gap-2">
                  {dropped ? (
                    <Button variant="secondary" className="h-9 flex-1" onClick={() => onRestore(column)}>
                      Undo delete
                    </Button>
                  ) : (
                    <>
                      <Button variant="secondary" className="h-9 flex-1" onClick={() => onEdit(column)}>
                        Edit
                      </Button>
                      <Button variant="secondary" className="h-9 flex-1 hover:text-danger" onClick={() => onDelete(column)}>
                        Delete
                      </Button>
                    </>
                  )}
                </div>
              )}
            </li>
          );
        })}
      </ul>

      <div className="hidden overflow-x-auto rounded-md border border-border sm:block">
        <div className="min-w-[44rem] text-sm">
          <div className={`${rowGrid} border-b border-border bg-surface-muted px-3 py-2 text-xs font-medium text-muted`}>
            <span aria-hidden />
            <span>Name</span>
            <span>Type</span>
            <span>Constraints</span>
            <span>Default</span>
            <span className="sr-only">Actions</span>
          </div>

          <div className={`${rowGrid} border-b border-border px-3 py-2 text-muted`}>
            <span aria-hidden className="text-center">🔒</span>
            <span className="font-mono">id</span>
            <span>BigInt</span>
            <span className="flex flex-wrap gap-1">
              <Tag>primary key</Tag>
            </span>
            <span>auto increment</span>
            <span className="text-right text-xs">added for you</span>
          </div>

          <DndContext sensors={sensors} collisionDetection={closestCenter} onDragEnd={handleDragEnd}>
            <SortableContext items={ordered.map((column) => column.id)} strategy={verticalListSortingStrategy}>
              <ul>
                {ordered.map((column) => (
                  <SortableRow
                    key={column.id}
                    column={column}
                    editable={editable}
                    canDrag={editable && !reordering}
                    onEdit={onEdit}
                    onDelete={onDelete}
                    onRestore={onRestore}
                  />
                ))}
              </ul>
            </SortableContext>
          </DndContext>

          {columns.length === 0 && (
            <p className="px-3 py-4 text-muted">No columns yet. Add the first one below, or pick a common column to start.</p>
          )}
        </div>
      </div>
    </>
  );
}

function SortableRow({
  column,
  editable,
  canDrag,
  onEdit,
  onDelete,
  onRestore,
}: {
  column: Column;
  editable: boolean;
  canDrag: boolean;
  onEdit: (column: Column) => void;
  onDelete: (column: Column) => void;
  onRestore: (column: Column) => void;
}) {
  const { attributes, listeners, setNodeRef, setActivatorNodeRef, transform, transition, isDragging } = useSortable({
    id: column.id,
    disabled: !canDrag,
  });
  const dropped = column.state === "PendingDrop";

  return (
    <li
      ref={setNodeRef}
      style={{ transform: CSS.Translate.toString(transform), transition }}
      className={`${rowGrid} border-b border-border bg-surface px-3 py-1.5 last:border-b-0 ${isDragging ? "relative z-10 shadow-lg" : ""}`}
      data-testid={`column-${column.name}`}
    >
      <button
        type="button"
        ref={setActivatorNodeRef}
        {...attributes}
        {...listeners}
        aria-label={`Move ${column.name}`}
        disabled={!canDrag}
        className="flex h-8 w-6 cursor-grab touch-none items-center justify-center rounded text-muted hover:bg-surface-muted hover:text-foreground focus-visible:outline-2 focus-visible:outline-accent active:cursor-grabbing disabled:cursor-default disabled:opacity-40"
      >
        <span aria-hidden>⠿</span>
      </button>
      <span className="flex min-w-0 items-center gap-2">
        {editable && !dropped ? (
          <button
            type="button"
            className="truncate text-left font-mono hover:text-accent hover:underline focus-visible:outline-2 focus-visible:outline-accent"
            title={`Edit ${column.name}`}
            onClick={() => onEdit(column)}
          >
            {column.name}
          </button>
        ) : (
          <span className={`truncate font-mono ${dropped ? "text-muted line-through" : ""}`}>{column.name}</span>
        )}
        {column.state === "New" && <span className="text-xs text-accent">new</span>}
        {column.state === "Changed" && <span className="text-xs text-warn">changed</span>}
      </span>
      <span className={`grid ${dropped ? "text-muted line-through" : ""}`}>
        <span className={column.referencesTableName && !dropped ? "font-medium text-accent" : ""}>{typeLabel(column)}</span>
        {column.onDelete && <span className="text-xs text-muted">on delete {onDeleteText[column.onDelete]}</span>}
      </span>
      <span className={`flex flex-wrap gap-1 ${dropped ? "opacity-50" : ""}`}>
        {!column.isNullable && <Tag tone="accent">required</Tag>}
        {column.isUnique && <Tag tone="accent">unique</Tag>}
        {column.isNullable && !column.isUnique && <Tag>optional</Tag>}
      </span>
      <span className={`truncate font-mono ${dropped ? "text-muted line-through" : ""}`} title={column.defaultValue ?? undefined}>
        {column.defaultValue ?? <span className="font-sans text-muted">—</span>}
      </span>
      <span className="flex justify-end gap-1">
        {editable &&
          (dropped ? (
            <Button variant="secondary" className="h-7 px-2.5 text-xs" onClick={() => onRestore(column)}>
              Undo delete
            </Button>
          ) : (
            <>
              <Button variant="ghost" className="h-7 px-2.5 text-xs" onClick={() => onEdit(column)}>
                Edit
              </Button>
              <Button
                variant="ghost"
                className="h-7 px-2.5 text-xs hover:text-danger"
                aria-label={`Delete ${column.name}`}
                onClick={() => onDelete(column)}
              >
                Delete
              </Button>
            </>
          ))}
      </span>
    </li>
  );
}
