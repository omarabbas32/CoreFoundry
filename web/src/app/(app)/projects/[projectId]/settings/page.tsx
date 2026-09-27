"use client";

import { useParams } from "next/navigation";
import { PageSkeleton } from "@/components/page-skeleton";
import { useProject } from "@/lib/queries";
import { atLeast } from "@/lib/types";
import { DeleteProjectSection } from "../delete-project-section";
import { MembersSection } from "../members-section";
import { RenameProjectForm } from "../rename-project-form";

/** Members for everyone; renaming for admins and owners; deleting for the owner. */
export default function ProjectSettingsPage() {
  const projectId = Number(useParams<{ projectId: string }>().projectId);
  const project = useProject(projectId);

  // The layout loads the project and reports errors; this only waits for the shared query.
  if (!project.data) return <PageSkeleton label="Loading project…" variant="detail" />;

  const { data } = project;
  return (
    <div className="grid gap-6">
      <div>
        <h1 className="text-2xl font-semibold tracking-tight">Settings</h1>
        <p className="text-sm text-muted">Who works on this project, its name, and deleting it.</p>
      </div>

      <MembersSection project={data} />
      {atLeast(data.role, "Admin") && <RenameProjectForm project={data} />}
      {data.role === "Owner" && <DeleteProjectSection project={data} />}
    </div>
  );
}
