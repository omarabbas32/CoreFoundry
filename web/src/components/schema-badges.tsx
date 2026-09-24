import Link from "next/link";
import type { SchemaObjectState } from "@/lib/types";
import { Badge } from "./ui";

const stateTone = { New: "accent", Applied: "ok", Changed: "warn", PendingDrop: "danger" } as const;
const stateLabel: Record<SchemaObjectState, string> = {
  New: "New",
  Applied: "Applied",
  Changed: "Changed",
  PendingDrop: "Will be dropped",
};

export function StateBadge({ state }: { state: SchemaObjectState }) {
  return <Badge tone={stateTone[state]}>{stateLabel[state]}</Badge>;
}

/** Shown on designer pages: the draft reaches the database only when a plan is reviewed and applied. */
export function DraftBanner({ projectId }: { projectId: number }) {
  return (
    <p className="flex flex-wrap items-center justify-between gap-2 rounded-md border border-warn/30 bg-warn-soft px-3 py-2 text-sm text-warn">
      <span>
        <span className="font-medium">Draft changes aren&apos;t applied yet.</span> Review the plan to see the exact SQL
        before anything changes in the project&apos;s database.
      </span>
      <Link href={`/projects/${projectId}/schema`} className="font-medium underline underline-offset-2 hover:no-underline">
        Review plan →
      </Link>
    </p>
  );
}
