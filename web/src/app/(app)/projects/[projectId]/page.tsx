"use client";

import Link from "next/link";
import { useParams } from "next/navigation";
import { PageSkeleton } from "@/components/page-skeleton";
import { Alert, Card } from "@/components/ui";
import { useDrift, useProject, useTables } from "@/lib/queries";
import { ExportCard } from "./export-card";

/** The project at a glance: its details, what to do next with the schema, and the export. Settings has the rest. */
export default function ProjectPage() {
  const projectId = Number(useParams<{ projectId: string }>().projectId);
  const project = useProject(projectId);

  // The layout loads the project and reports errors; this only waits for the shared query.
  if (!project.data) return <PageSkeleton label="Loading project…" variant="detail" />;

  const { data } = project;
  const created = new Date(data.createdAt).toLocaleDateString(undefined, { dateStyle: "medium" });

  return (
    <div className="grid gap-6">
      <div className="grid gap-2">
        <h1 className="text-2xl font-semibold tracking-tight">{data.name}</h1>
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
      {data.status === "Active" && <SchemaStatus projectId={data.id} schemaVersion={data.schemaVersion} />}
      {data.status === "Active" && <ExportCard projectId={data.id} />}
    </div>
  );
}

const primaryLink =
  "inline-flex h-9 items-center rounded-md bg-accent-solid px-3.5 text-sm font-medium text-on-accent hover:bg-accent-solid-hover";
const secondaryLink =
  "inline-flex h-9 items-center rounded-md border border-border bg-surface px-3.5 text-sm font-medium hover:bg-surface-muted";

/** Where the schema stands and the one or two things to do next (the menu has everything else). */
function SchemaStatus({ projectId, schemaVersion }: { projectId: number; schemaVersion: number }) {
  const tables = useTables(projectId);
  if (!tables.data) return null;

  const base = `/projects/${projectId}`;
  const total = tables.data.length;
  const pending = tables.data.filter((table) => table.state !== "Applied").length;

  const [message, actions] =
    total === 0
      ? [
          "No tables yet. Describe your app to the AI assistant, start from a template, or design tables yourself.",
          <>
            <Link href={`${base}/assistant`} className={primaryLink}>
              Design with AI
            </Link>
            <Link href={`${base}/tables`} className={secondaryLink}>
              Create tables
            </Link>
          </>,
        ]
      : pending > 0
        ? [
            `${total} ${total === 1 ? "table" : "tables"}; ${pending} ${pending === 1 ? "has" : "have"} draft changes that aren't in the database yet.`,
            <>
              <Link href={`${base}/schema`} className={primaryLink}>
                Review plan &amp; apply
              </Link>
              <Link href={`${base}/tables`} className={secondaryLink}>
                Open tables
              </Link>
            </>,
          ]
        : [
            `${total} ${total === 1 ? "table" : "tables"}, all applied (schema version ${schemaVersion}).`,
            <>
              <Link href={`${base}/data`} className={primaryLink}>
                Browse data
              </Link>
              <Link href={`${base}/tables`} className={secondaryLink}>
                Open tables
              </Link>
            </>,
          ];

  return (
    <Card className="flex flex-wrap items-center justify-between gap-3 p-5" data-testid="schema-status">
      <div className="grid gap-1">
        <h2 className="font-semibold">Schema</h2>
        <p className="text-sm text-muted">{message}</p>
      </div>
      <div className="flex flex-wrap gap-2">{actions}</div>
    </Card>
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
