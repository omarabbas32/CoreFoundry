"use client";

import Link from "next/link";
import { useParams } from "next/navigation";
import { FullPageSpinner } from "@/components/full-page-spinner";
import { RoleBadge, StatusBadge } from "@/components/project-badges";
import { Alert, Card } from "@/components/ui";
import { ApiError } from "@/lib/api";
import { useDrift, useProject } from "@/lib/queries";
import { atLeast } from "@/lib/types";
import { DeleteProjectSection } from "./delete-project-section";
import { ExportCard } from "./export-card";
import { MembersSection } from "./members-section";
import { RenameProjectForm } from "./rename-project-form";

export default function ProjectPage() {
  const projectId = Number(useParams<{ projectId: string }>().projectId);
  const project = useProject(projectId);

  if (project.isPending) return <FullPageSpinner label="Loading project…" />;

  if (project.error) {
    const notFound = project.error instanceof ApiError && project.error.status === 404;
    return (
      <Card className="grid justify-items-center gap-3 px-6 py-14 text-center">
        <p className="font-medium">{notFound ? "Project not found" : "Couldn't load the project"}</p>
        <p className="text-sm text-muted">
          {notFound ? "It doesn't exist, or you're not a member of it." : project.error.message}
        </p>
        <Link href="/projects" className="text-sm font-medium text-accent hover:underline">
          Back to projects
        </Link>
      </Card>
    );
  }

  const { data } = project;
  const created = new Date(data.createdAt).toLocaleDateString(undefined, { dateStyle: "medium" });

  return (
    <div className="grid gap-6">
      <div className="grid gap-2">
        <Link href="/projects" className="text-sm text-muted hover:text-foreground">
          ← Projects
        </Link>
        <div className="flex flex-wrap items-center gap-3">
          <h1 className="text-2xl font-semibold tracking-tight">{data.name}</h1>
          <StatusBadge status={data.status} />
          <RoleBadge role={data.role} />
        </div>
        <dl className="flex flex-wrap gap-x-6 gap-y-1 text-sm text-muted">
          <div className="flex gap-1.5">
            <dt>Slug</dt>
            <dd className="font-mono text-foreground">{data.slug}</dd>
          </div>
          <div className="flex gap-1.5">
            <dt>Database</dt>
            <dd className="font-mono text-foreground">{data.databaseName}</dd>
          </div>
          <div className="flex gap-1.5">
            <dt>Schema version</dt>
            <dd className="text-foreground tabular-nums">{data.schemaVersion}</dd>
          </div>
          <div className="flex gap-1.5">
            <dt>Created</dt>
            <dd className="text-foreground">{created}</dd>
          </div>
        </dl>
      </div>

      {data.status === "Failed" && (
        <Alert>
          The project&apos;s database couldn&apos;t be created. The owner can retry from the projects list.
        </Alert>
      )}

      {data.status === "Active" && <DriftBanner projectId={data.id} />}

      <Card className="flex flex-wrap items-center justify-between gap-3 p-5">
        <div>
          <h2 className="font-semibold">Schema</h2>
          <p className="text-sm text-muted">
            Design tables, review the SQL plan, apply it to the project&apos;s database, then browse and edit rows.
          </p>
        </div>
        <div className="flex flex-wrap gap-2">
          <Link
            href={`/projects/${data.id}/tables`}
            className="inline-flex h-9 items-center rounded-md bg-accent px-3.5 text-sm font-medium text-on-accent hover:bg-accent-hover"
          >
            Open table designer
          </Link>
          <Link
            href={`/projects/${data.id}/schema`}
            className="inline-flex h-9 items-center rounded-md border border-border bg-surface px-3.5 text-sm font-medium hover:bg-surface-muted"
          >
            Review plan
          </Link>
          <Link
            href={`/projects/${data.id}/schema/history`}
            className="inline-flex h-9 items-center rounded-md border border-border bg-surface px-3.5 text-sm font-medium hover:bg-surface-muted"
          >
            History
          </Link>
          <Link
            href={`/projects/${data.id}/data`}
            className="inline-flex h-9 items-center rounded-md border border-border bg-surface px-3.5 text-sm font-medium hover:bg-surface-muted"
          >
            Browse data
          </Link>
          <Link
            href={`/projects/${data.id}/api`}
            className="inline-flex h-9 items-center rounded-md border border-border bg-surface px-3.5 text-sm font-medium hover:bg-surface-muted"
          >
            API
          </Link>
        </div>
      </Card>

      {data.status === "Active" && <ExportCard projectId={data.id} />}

      <MembersSection project={data} />

      {atLeast(data.role, "Admin") && <RenameProjectForm project={data} />}
      {data.role === "Owner" && <DeleteProjectSection project={data} />}
    </div>
  );
}

/** Shown when the database was changed outside CoreFoundry since the last apply. */
function DriftBanner({ projectId }: { projectId: number }) {
  const drift = useDrift(projectId, true);
  if (!drift.data || drift.data.differences.length === 0) return null;

  return (
    <div role="alert" className="grid gap-1 rounded-md border border-warn/30 bg-warn-soft px-3 py-2 text-sm text-warn" data-testid="drift-banner">
      <p className="font-medium">
        The database was changed outside CoreFoundry
        {drift.data.sinceVersion !== null ? ` since schema version ${drift.data.sinceVersion}` : ""}.
      </p>
      <ul className="list-disc pl-5">
        {drift.data.differences.map((difference) => (
          <li key={difference}>{difference}</li>
        ))}
      </ul>
      <p>
        <Link href={`/projects/${projectId}/schema`} className="underline">
          Review the plan
        </Link>{" "}
        to see what applying the draft would change now.
      </p>
    </div>
  );
}
