import type { SchemaObjectState } from "@/lib/types";
import { Badge } from "./ui";

const stateTone = { New: "accent", Applied: "ok", PendingDrop: "danger" } as const;
const stateLabel: Record<SchemaObjectState, string> = { New: "New", Applied: "Applied", PendingDrop: "Will be dropped" };

export function StateBadge({ state }: { state: SchemaObjectState }) {
  return <Badge tone={stateTone[state]}>{stateLabel[state]}</Badge>;
}

/** Shown on designer pages: nothing here reaches the database until the schema engine applies it (M3). */
export function DraftBanner() {
  return (
    <p className="rounded-md border border-warn/30 bg-warn-soft px-3 py-2 text-sm text-warn">
      <span className="font-medium">Draft changes aren&apos;t applied yet.</span> Tables and columns are saved as a
      design; reviewing and applying them to the project&apos;s database comes with the schema engine.
    </p>
  );
}
