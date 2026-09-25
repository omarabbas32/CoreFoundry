"use client";

import Link from "next/link";
import { useParams, useRouter } from "next/navigation";
import { useEffect } from "react";
import { FullPageSpinner } from "@/components/full-page-spinner";
import { Alert, Card } from "@/components/ui";
import { useDataSchema } from "@/lib/queries";

/** Opens the first applied table, or explains that nothing has been applied yet. */
export default function DataIndexPage() {
  const projectId = Number(useParams<{ projectId: string }>().projectId);
  const router = useRouter();
  const schema = useDataSchema(projectId);
  const first = schema.data?.tables[0]?.name;

  useEffect(() => {
    if (first) router.replace(`/projects/${projectId}/data/${first}`);
  }, [first, projectId, router]);

  if (schema.isPending || first) return <FullPageSpinner label="Loading tables…" />;
  if (schema.error) return <Alert>{schema.error.message}</Alert>;

  return (
    <Card className="grid justify-items-center gap-2 px-6 py-12 text-center">
      <p className="font-medium">No tables have been applied yet</p>
      <p className="text-sm text-muted">Design tables, then apply the plan to add rows to them.</p>
      <div className="flex gap-4 text-sm font-medium">
        <Link href={`/projects/${projectId}/tables`} className="text-accent hover:underline">
          Open table designer
        </Link>
        <Link href={`/projects/${projectId}/schema`} className="text-accent hover:underline">
          Review the plan
        </Link>
      </div>
    </Card>
  );
}
