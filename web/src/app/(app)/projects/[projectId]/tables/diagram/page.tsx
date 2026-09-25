"use client";

import Link from "next/link";
import { useParams } from "next/navigation";
import { FullPageSpinner } from "@/components/full-page-spinner";
import { DraftBanner } from "@/components/schema-badges";
import { Alert, Card } from "@/components/ui";
import { useProject, useSchema } from "@/lib/queries";
import { SchemaDiagram } from "./schema-diagram";

export default function DiagramPage() {
  const projectId = Number(useParams<{ projectId: string }>().projectId);
  const project = useProject(projectId);
  const schema = useSchema(projectId);

  if (schema.isPending) return <FullPageSpinner label="Loading schema…" />;

  const references = schema.data?.flatMap((table) => table.columns).filter((column) => column.referencesTableId !== null).length ?? 0;

  return (
    <div className="grid gap-6">
      <div className="grid gap-2">
        <Link href={`/projects/${projectId}/tables`} className="text-sm text-muted hover:text-foreground">
          ← {project.data?.name ? `${project.data.name} · tables` : "Tables"}
        </Link>
        <div>
          <h1 className="text-2xl font-semibold tracking-tight">Schema diagram</h1>
          <p className="text-sm text-muted">
            {schema.data?.length ?? 0} tables, {references} {references === 1 ? "relation" : "relations"}. Arrows point from a
            referencing column to the table whose id it holds. Drag tables to rearrange; the layout isn&apos;t saved.
          </p>
        </div>
      </div>

      <DraftBanner />
      {schema.error && <Alert>{schema.error.message}</Alert>}

      {schema.data?.length === 0 && (
        <Card className="px-6 py-14 text-center text-sm text-muted">No tables yet. Create some to see them here.</Card>
      )}

      {/* Keyed by the data's timestamp so an updated schema starts again from the automatic layout. */}
      {schema.data && schema.data.length > 0 && (
        <SchemaDiagram key={schema.dataUpdatedAt} tables={schema.data} projectId={projectId} />
      )}
    </div>
  );
}
