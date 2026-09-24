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

// Handle · name · type · nullable · unique · default · actions. Scrolls sideways on narrow screens.
const rowGrid = "grid grid-cols-[2rem_minmax(9rem,1.4fr)_minmax(8rem,1fr)_4.5rem_4.5rem_minmax(8rem,1.2fr)_9.5rem] items-center gap-x-3";

export function typeLabel(column: Pick<Column, "dataType" | "length" | "precision" | "scale">) {
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
    <div className="overflow-x-auto rounded-md border border-border">
      <div className="min-w-[52rem] text-sm">
        <div className={`${rowGrid} border-b border-border bg-surface-muted px-3 py-2 text-xs font-medium text-muted`}>
          <span aria-hidden />
          <span>Name</span>
          <span>Type</span>
          <span>Nullable</span>
          <span>Unique</span>
          <span>Default</span>
          <span className="sr-only">Actions</span>
        </div>

        <div className={`${rowGrid} border-b border-border px-3 py-2 text-muted`}>
          <span aria-hidden className="text-center">🔒</span>
          <span className="font-mono">id</span>
          <span>BigInt</span>
          <span>no</span>
          <span>PK</span>
          <span>auto increment</span>
          <span className="text-xs">added by CoreFoundry</span>
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

        {columns.length === 0 && <p className="px-3 py-4 text-muted">No columns yet. Add the first one below.</p>}
      </div>
    </div>
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
      <span className={`truncate font-mono ${dropped ? "text-muted line-through" : ""}`}>
        {column.name}
        {column.state === "New" && <span className="ml-2 font-sans text-xs text-accent">new</span>}
      </span>
      <span className={dropped ? "text-muted line-through" : ""}>{typeLabel(column)}</span>
      <span className={dropped ? "text-muted line-through" : ""}>{column.isNullable ? "yes" : "no"}</span>
      <span className={dropped ? "text-muted line-through" : ""}>{column.isUnique ? "yes" : "—"}</span>
      <span className={`truncate font-mono ${dropped ? "text-muted line-through" : ""}`} title={column.defaultValue ?? undefined}>
        {column.defaultValue ?? <span className="font-sans text-muted">—</span>}
      </span>
      <span className="flex justify-end gap-1">
        {editable &&
          (dropped ? (
            <Button variant="secondary" className="h-8" onClick={() => onRestore(column)}>
              Undo delete
            </Button>
          ) : (
            <>
              <Button variant="ghost" className="h-8" onClick={() => onEdit(column)}>
                Edit
              </Button>
              <Button variant="ghost" className="h-8" onClick={() => onDelete(column)}>
                Delete
              </Button>
            </>
          ))}
      </span>
    </li>
  );
}
