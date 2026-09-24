"use client";

import Link from "next/link";
import { useParams } from "next/navigation";
import { FullPageSpinner } from "@/components/full-page-spinner";
import { RoleBadge, StatusBadge } from "@/components/project-badges";
import { Alert, Card } from "@/components/ui";
import { ApiError } from "@/lib/api";
import { useProject } from "@/lib/queries";
import { atLeast } from "@/lib/types";
import { DeleteProjectSection } from "./delete-project-section";
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

      <MembersSection project={data} />

      {atLeast(data.role, "Admin") && <RenameProjectForm project={data} />}
      {data.role === "Owner" && <DeleteProjectSection project={data} />}
    </div>
  );
}
