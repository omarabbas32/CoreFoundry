"use client";

import Link from "next/link";
import { useState } from "react";
import { FoundryScene } from "@/components/foundry-scene";
import { PageTitle } from "@/components/page-title";
import { RoleBadge, StatusBadge } from "@/components/project-badges";
import { Alert, Badge, Button, Card, Input } from "@/components/ui";
import { useProjects, useRetryProvisioning } from "@/lib/queries";
import type { Project } from "@/lib/types";
import { CreateProjectDialog } from "./create-project-dialog";

const templateNames: Record<string, string> = { ecommerce: "E-commerce" };

export default function ProjectsPage() {
  const projects = useProjects();
  const [creating, setCreating] = useState(false);
  const [search, setSearch] = useState("");

  const all = projects.data ?? [];
  const query = search.trim().toLowerCase();
  const shown = query ? all.filter((project) => project.name.toLowerCase().includes(query) || project.slug.includes(query)) : all;

  return (
    <div className="grid gap-6">
      <PageTitle title="Projects · CoreFoundry" />
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h1 className="text-2xl font-semibold tracking-tight">Projects</h1>
          <p className="text-sm text-muted">Each project has its own MySQL database and its own exported backend.</p>
        </div>
        <Button onClick={() => setCreating(true)}>New project</Button>
      </div>

      {projects.isPending && <ProjectsSkeleton />}
      {projects.error && <Alert>{projects.error.message}</Alert>}

      {projects.data?.length === 0 && <Welcome onCreate={() => setCreating(true)} />}

      {all.length > 6 && (
        <div className="flex flex-wrap items-center gap-3">
          <label htmlFor="project-search" className="sr-only">
            Find a project
          </label>
          <Input
            id="project-search"
            type="search"
            placeholder="Find a project…"
            className="max-w-xs"
            value={search}
            onChange={(event) => setSearch(event.target.value)}
          />
          <span className="text-xs text-muted tabular-nums">
            {shown.length} of {all.length}
          </span>
        </div>
      )}

      {shown.length > 0 && (
        <ul className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
          {shown.map((project) => (
            <ProjectCard key={project.id} project={project} />
          ))}
        </ul>
      )}
      {all.length > 0 && shown.length === 0 && <p className="text-sm text-muted">No project matches “{search}”.</p>}

      <CreateProjectDialog open={creating} onClose={() => setCreating(false)} />
    </div>
  );
}

/** One project: what it is, where it stands, and your role. The whole card opens it. */
function ProjectCard({ project }: { project: Project }) {
  const retry = useRetryProvisioning();
  const canRetry = project.status === "Failed" && project.role === "Owner";
  const created = new Date(project.createdAt).toLocaleDateString(undefined, { dateStyle: "medium" });
  const applied = project.schemaVersion > 0;

  return (
    <li className="relative">
      <Card className="group grid h-full content-between gap-4 p-4 transition-colors hover:border-accent/50">
        <div className="grid gap-1">
          <div className="flex items-start justify-between gap-2">
            {/* The link covers the card (after:inset-0), so the whole card opens the project. */}
            <Link
              href={`/projects/${project.id}`}
              className="truncate font-semibold group-hover:text-accent after:absolute after:inset-0 after:rounded-lg focus-visible:outline-none focus-visible:after:outline-2 focus-visible:after:outline-accent"
            >
              {project.name}
            </Link>
            <RoleBadge role={project.role} />
          </div>
          <p className="truncate font-mono text-xs text-muted">{project.databaseName}</p>
        </div>

        <div className="flex flex-wrap items-center gap-1.5 text-xs text-muted">
          {project.status !== "Active" && <StatusBadge status={project.status} />}
          {project.status === "Active" &&
            (applied ? <Badge tone="ok">Schema v{project.schemaVersion}</Badge> : <Badge>Nothing applied yet</Badge>)}
          {project.templateKey && <Badge tone="accent">{templateNames[project.templateKey] ?? project.templateKey} template</Badge>}
          <span className="ml-auto">Created {created}</span>
        </div>

        {canRetry && (
          <div className="relative z-10 flex items-center gap-2">
            <Button variant="secondary" className="h-8" loading={retry.isPending} onClick={() => retry.mutate(project.id)}>
              Retry creating the database
            </Button>
          </div>
        )}
        {retry.error && (
          <p className="relative z-10 text-sm text-danger" role="alert">
            {retry.error.message}
          </p>
        )}
      </Card>
    </li>
  );
}

/** First visit: what CoreFoundry does, in three steps, and one button. */
function Welcome({ onCreate }: { onCreate: () => void }) {
  const steps = [
    ["Create a project", "It gets its own MySQL database."],
    ["Design the tables", "With the AI assistant, a template, or by hand."],
    ["Apply and export", "Review the SQL, apply it, and download a C# Clean Architecture backend."],
  ];
  return (
    <Card className="grid gap-6 p-6 lg:grid-cols-[minmax(0,1fr)_minmax(0,1.2fr)] lg:items-center">
      <div className="grid gap-4">
        <div className="grid gap-1">
          <h2 className="text-lg font-semibold">Welcome to CoreFoundry</h2>
          <p className="text-sm text-muted">From an idea to a running backend in three steps.</p>
        </div>
        <ol className="grid gap-3">
          {steps.map(([title, text], index) => (
            <li key={title} className="flex gap-3">
              <span className="grid size-7 shrink-0 place-items-center rounded-full bg-accent-soft text-sm font-semibold text-accent">
                {index + 1}
              </span>
              <span className="grid">
                <span className="font-medium">{title}</span>
                <span className="text-sm text-muted">{text}</span>
              </span>
            </li>
          ))}
        </ol>
        <div>
          <Button onClick={onCreate}>Create your first project</Button>
        </div>
      </div>
      <FoundryScene className="hidden sm:block" />
    </Card>
  );
}

/** The grid's shape while the list loads, instead of a full-page spinner. */
function ProjectsSkeleton() {
  return (
    <ul className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3" aria-busy="true" aria-label="Loading projects">
      {[0, 1, 2].map((index) => (
        <li key={index}>
          <Card className="grid gap-4 p-4">
            <div className="h-4 w-2/3 animate-pulse rounded bg-surface-muted motion-reduce:animate-none" />
            <div className="h-3 w-1/2 animate-pulse rounded bg-surface-muted motion-reduce:animate-none" />
            <div className="h-5 w-1/3 animate-pulse rounded bg-surface-muted motion-reduce:animate-none" />
          </Card>
        </li>
      ))}
    </ul>
  );
}
