"use client";

import Link from "next/link";
import { useState } from "react";
import { Alert, Button, Card } from "@/components/ui";
import { download } from "@/lib/api";
import { useDataSchema, useTables } from "@/lib/queries";

/**
 * Downloads the project as a standalone .NET backend. The export follows the applied schema, so
 * unapplied draft changes are pointed out before downloading.
 */
export function ExportCard({ projectId }: { projectId: number }) {
  const schema = useDataSchema(projectId);
  const tables = useTables(projectId);
  const [downloading, setDownloading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState<string | null>(null);

  const applied = schema.data?.tables.length ?? 0;
  const unapplied = (tables.data ?? []).filter((table) => table.state !== "Applied").length;
  // What the next export contains: every table that isn't only a draft (its access rules take effect without an apply).
  const exported = (tables.data ?? []).filter((table) => table.state !== "New");
  const publicRead = exported.filter((table) => table.readAccess === "Public").length;
  const adminRead = exported.filter((table) => table.readAccess === "Admin").length;
  const allDefaultAccess =
    exported.length > 0 && exported.every((table) => table.readAccess === "SignedIn" && table.writeAccess === "SignedIn");

  async function exportCode() {
    setDownloading(true);
    setError(null);
    setDone(null);
    try {
      const { blob, fileName } = await download(`/api/projects/${projectId}/export`, "backend.zip");
      const url = URL.createObjectURL(blob);
      const link = document.createElement("a");
      link.href = url;
      link.download = fileName;
      link.click();
      setTimeout(() => URL.revokeObjectURL(url), 1000);
      setDone(fileName);
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : "The export failed.");
    } finally {
      setDownloading(false);
    }
  }

  return (
    <Card className="grid gap-3 p-5" data-testid="export-card">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="grid gap-1">
          <h2 className="font-semibold">Export code</h2>
          <p className="max-w-2xl text-sm text-muted">
            Download a .NET 10 backend for the applied tables, in Clean Architecture (Domain, Application, Infrastructure,
            Api): EF Core with an initial migration, JWT sign-in, Swagger UI, a Dockerfile and docker-compose. It&apos;s
            yours to run, change and deploy.
          </p>
        </div>
        <Button onClick={() => void exportCode()} loading={downloading} disabled={schema.isPending || applied === 0}>
          Download .zip
        </Button>
      </div>

      {schema.data && applied === 0 && (
        <p className="text-sm text-muted">
          Nothing to export yet: apply a plan that creates tables first.{" "}
          <Link href={`/projects/${projectId}/schema`} className="font-medium text-accent hover:underline">
            Review the plan
          </Link>
        </p>
      )}
      {applied > 0 && unapplied > 0 && (
        <p className="rounded-md border border-warn/30 bg-warn-soft px-3 py-2 text-sm text-warn">
          {unapplied === 1 ? "1 table has" : `${unapplied} tables have`} draft changes that aren&apos;t applied. The export
          contains the applied schema;{" "}
          <Link href={`/projects/${projectId}/schema`} className="font-medium underline">
            apply the plan
          </Link>{" "}
          to include them.
        </p>
      )}
      {exported.length > 0 && (
        <p className="text-sm text-muted">
          {publicRead === 1 ? "1 public table" : `${publicRead} public tables`}, {adminRead} admin-only. Realtime:{" "}
          {exported.length === 1 ? "1 table" : `${exported.length} tables`} — {publicRead} public.
        </p>
      )}
      {allDefaultAccess && (
        <p className="rounded-md border border-warn/30 bg-warn-soft px-3 py-2 text-sm text-warn">
          Every table still uses the default access (signed-in read and write).{" "}
          <Link href={`/projects/${projectId}/api#access`} className="font-medium underline">
            Review access
          </Link>{" "}
          before exporting.
        </p>
      )}
      {done && (
        <p role="status" className="text-sm text-ok">
          Downloaded <span className="font-mono">{done}</span>. Its README explains how to run it.
        </p>
      )}
      {error && <Alert>{error}</Alert>}
    </Card>
  );
}
