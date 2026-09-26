"use client";

import Link from "next/link";
import { useParams } from "next/navigation";
import { FullPageSpinner } from "@/components/full-page-spinner";
import { Card } from "@/components/ui";
import { ApiError } from "@/lib/api";
import { useProject } from "@/lib/queries";
import { ProjectNav } from "./project-nav";

/**
 * Every project page: the project menu on the left (a strip on phones) and the page beside it. A project that
 * can't be loaded (not found, not a member) is reported here once, for all of its pages.
 */
export default function ProjectLayout({ children }: LayoutProps<"/projects/[projectId]">) {
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

  return (
    <div className="grid gap-6 md:grid-cols-[13rem_minmax(0,1fr)] md:gap-10">
      <ProjectNav project={project.data} />
      <div className="min-w-0">{children}</div>
    </div>
  );
}
