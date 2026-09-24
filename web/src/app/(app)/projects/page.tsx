"use client";

import Link from "next/link";
import { useState } from "react";
import { FullPageSpinner } from "@/components/full-page-spinner";
import { RoleBadge, StatusBadge } from "@/components/project-badges";
import { Alert, Button, Card } from "@/components/ui";
import { useProjects, useRetryProvisioning } from "@/lib/queries";
import type { Project } from "@/lib/types";
import { CreateProjectDialog } from "./create-project-dialog";

export default function ProjectsPage() {
  const projects = useProjects();
  const [creating, setCreating] = useState(false);

  return (
    <div className="grid gap-6">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h1 className="text-2xl font-semibold tracking-tight">Projects</h1>
          <p className="text-sm text-muted">Each project has its own MySQL database.</p>
        </div>
        <Button onClick={() => setCreating(true)}>New project</Button>
      </div>

      {projects.isPending && <FullPageSpinner label="Loading projects…" />}
      {projects.error && <Alert>{projects.error.message}</Alert>}

      {projects.data?.length === 0 && (
        <Card className="grid justify-items-center gap-3 px-6 py-14 text-center">
          <p className="font-medium">No projects yet</p>
          <p className="max-w-sm text-sm text-muted">
            Create a project to get its own database, then invite teammates and design tables.
          </p>
          <Button onClick={() => setCreating(true)}>Create your first project</Button>
        </Card>
      )}

      {projects.data && projects.data.length > 0 && (
        <ul className="grid gap-3">
          {projects.data.map((project) => (
            <ProjectRow key={project.id} project={project} />
          ))}
        </ul>
      )}

      <CreateProjectDialog open={creating} onClose={() => setCreating(false)} />
    </div>
  );
}

function ProjectRow({ project }: { project: Project }) {
  const retry = useRetryProvisioning();
  const canRetry = project.status === "Failed" && project.role === "Owner";

  return (
    <li>
      <Card className="flex flex-wrap items-center gap-x-4 gap-y-2 px-4 py-3 transition-colors hover:border-accent/50">
        <Link href={`/projects/${project.id}`} className="grid min-w-0 flex-1 gap-0.5">
          <span className="truncate font-medium">{project.name}</span>
          <span className="truncate font-mono text-xs text-muted">
            {project.slug} · {project.databaseName}
          </span>
        </Link>
        <div className="flex items-center gap-2">
          <StatusBadge status={project.status} />
          <RoleBadge role={project.role} />
          {canRetry && (
            <Button variant="secondary" loading={retry.isPending} onClick={() => retry.mutate(project.id)}>
              Retry
            </Button>
          )}
        </div>
        {retry.error && (
          <p className="basis-full text-sm text-danger" role="alert">
            {retry.error.message}
          </p>
        )}
      </Card>
    </li>
  );
}
