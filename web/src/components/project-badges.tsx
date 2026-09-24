import type { ProjectRole, ProjectStatus } from "@/lib/types";
import { Badge } from "./ui";

const statusTone = { Active: "ok", Provisioning: "warn", Failed: "danger", Deleting: "neutral" } as const;

const statusLabel: Record<ProjectStatus, string> = {
  Active: "Active",
  Provisioning: "Creating database…",
  Failed: "Database not created",
  Deleting: "Deleting…",
};

export function StatusBadge({ status }: { status: ProjectStatus }) {
  return <Badge tone={statusTone[status]}>{statusLabel[status]}</Badge>;
}

export function RoleBadge({ role }: { role: ProjectRole }) {
  return <Badge tone={role === "Owner" ? "accent" : "neutral"}>{role}</Badge>;
}
