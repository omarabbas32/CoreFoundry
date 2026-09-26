"use client";

import Link from "next/link";
import { useParams } from "next/navigation";
import { useState, useSyncExternalStore } from "react";
import { AccessSelects, accessLevelHint } from "@/components/access-controls";
import { FullPageSpinner } from "@/components/full-page-spinner";
import { Alert, Badge, Button, Card } from "@/components/ui";
import { ApiError, refreshSession } from "@/lib/api";
import { useAuth } from "@/lib/auth";
import { isRequired } from "@/lib/data-form";
import { useDataSchema, useProject, useSetTableAccess, useTables } from "@/lib/queries";
import type { DataColumn, DataTable } from "@/lib/types";
import { ExportCard } from "../export-card";

/** The browser's origin, or "" while rendering on the server (the page is a client page, but still prerendered). */
function useOrigin() {
  return useSyncExternalStore(
    () => () => undefined,
    () => window.location.origin,
    () => "",
  );
}

/** How to call the project's Data API: auth, then every applied table's endpoints, fields and examples. */
export default function ApiPage() {
  const projectId = Number(useParams<{ projectId: string }>().projectId);
  const project = useProject(projectId);
  const schema = useDataSchema(projectId);
  const { user } = useAuth();
  const origin = useOrigin();
  const base = `${origin}/api/projects/${projectId}/data`;

  if (schema.isPending || project.isPending) return <FullPageSpinner label="Loading the API…" />;

  const tables = schema.data?.tables ?? [];

  return (
    <div className="grid gap-6">
      <div className="grid gap-2">
        <Link href={`/projects/${projectId}`} className="text-sm text-muted hover:text-foreground">
          ← {project.data?.name ?? "Project"}
        </Link>
        <div>
          <h1 className="text-2xl font-semibold tracking-tight">API</h1>
          <p className="text-sm text-muted">
            Every applied table gets REST endpoints automatically. They follow the last applied schema
            {schema.data && <> (version {schema.data.schemaVersion})</>}: apply a plan and they change with it.
          </p>
        </div>
      </div>

      {schema.error && <Alert>{schema.error.message}</Alert>}

      <Card className="grid gap-3 p-5">
        <h2 className="font-semibold">Base URL</h2>
        <CodeBlock code={base} />
        <p className="text-sm text-muted">
          Requests and responses are JSON. Replace <span className="font-mono">{"{table}"}</span> with a table name and{" "}
          <span className="font-mono">{"{id}"}</span> with a row id.
        </p>
      </Card>

      <AuthCard origin={origin} email={user?.email ?? "you@example.com"} />

      <ExportCard projectId={projectId} />

      <AccessSection projectId={projectId} />

      {tables.length === 0 ? (
        <Card className="grid justify-items-center gap-2 px-6 py-12 text-center">
          <p className="font-medium">No endpoints yet</p>
          <p className="text-sm text-muted">Design tables and apply the plan: each applied table gets its endpoints right away.</p>
          <div className="flex gap-4 text-sm font-medium">
            <Link href={`/projects/${projectId}/tables`} className="text-accent hover:underline">
              Open table designer
            </Link>
            <Link href={`/projects/${projectId}/schema`} className="text-accent hover:underline">
              Review the plan
            </Link>
          </div>
        </Card>
      ) : (
        <>
          <nav aria-label="Tables" className="flex flex-wrap gap-2 text-sm">
            {tables.map((table) => (
              <a
                key={table.name}
                href={`#table-${table.name}`}
                className="rounded-md border border-border bg-surface px-2.5 py-1 font-mono hover:bg-surface-muted"
              >
                {table.name}
              </a>
            ))}
          </nav>
          {tables.map((table) => (
            <TableApi key={table.name} projectId={projectId} base={base} table={table} />
          ))}
          <RulesCard />
        </>
      )}
    </div>
  );
}

/** One row per table: who may read and write it in the exported API, reviewed and set in one place. */
function AccessSection({ projectId }: { projectId: number }) {
  const tables = useTables(projectId);
  const access = useSetTableAccess(projectId);
  const conflict = access.error instanceof ApiError && access.error.status === 409;

  return (
    <Card id="access" className="grid gap-4 p-5">
      <div>
        <h2 className="font-semibold">Access</h2>
        <p className="text-sm text-muted">
          Who may read and write each table in the exported API. Read also decides who may subscribe to the table&apos;s
          changes over the realtime hub in the exported backend.
        </p>
      </div>

      {access.error &&
        (conflict ? (
          <div role="alert" className="flex flex-wrap items-center justify-between gap-3 rounded-md border border-danger/30 bg-danger-soft px-3 py-2 text-sm text-danger">
            <span>{access.error.message}</span>
            <Button
              variant="secondary"
              onClick={() => {
                access.reset();
                void tables.refetch();
              }}
            >
              Reload
            </Button>
          </div>
        ) : (
          <Alert>{access.error.message}</Alert>
        ))}

      {tables.error && <Alert>{tables.error.message}</Alert>}

      {tables.isPending ? (
        <p className="text-sm text-muted">Loading tables…</p>
      ) : tables.data && tables.data.length === 0 ? (
        <p className="text-sm text-muted">No tables yet.</p>
      ) : (
        <div className="grid gap-3">
          {tables.data?.map((table) => (
            <div
              key={table.id}
              className="flex flex-wrap items-center justify-between gap-3 border-b border-border pb-3 last:border-0 last:pb-0"
            >
              <span className="font-mono text-sm">{table.name}</span>
              <AccessSelects
                idPrefix={`access-${table.id}`}
                read={table.readAccess}
                write={table.writeAccess}
                disabled={access.isPending || table.state === "PendingDrop"}
                onChange={({ read, write }) => {
                  access.reset();
                  access.mutate({ tableId: table.id, version: table.version, read, write });
                }}
              />
            </div>
          ))}
        </div>
      )}

      <p className="text-xs text-muted">{accessLevelHint}</p>
    </Card>
  );
}

function AuthCard({ origin, email }: { origin: string; email: string }) {
  const [state, setState] = useState<"idle" | "copying" | "copied" | "failed">("idle");

  async function copyToken() {
    setState("copying");
    try {
      const session = await refreshSession(); // a fresh token, valid for its full 15 minutes
      if (!session) throw new Error("Signed out");
      await navigator.clipboard.writeText(session.accessToken);
      setState("copied");
      setTimeout(() => setState("idle"), 2000);
    } catch {
      setState("failed");
    }
  }

  return (
    <Card className="grid gap-3 p-5">
      <h2 className="font-semibold">Authentication</h2>
      <p className="text-sm text-muted">
        Send an access token as <span className="font-mono">Authorization: Bearer …</span>. Get one by signing in; it
        lasts 15 minutes. Any project member (Developer and up) can read and write rows.
      </p>
      <CodeBlock
        code={`curl -s ${origin}/api/auth/login \\
  -H "Content-Type: application/json" \\
  -d '{"email":"${email}","password":"…"}'
# → {"accessToken":"eyJ…","expiresAt":"…","user":{…}}
TOKEN=eyJ…   # the accessToken`}
      />
      <div className="flex flex-wrap items-center gap-3">
        <Button variant="secondary" loading={state === "copying"} onClick={() => void copyToken()}>
          {state === "copied" ? "Copied" : "Copy a fresh access token"}
        </Button>
        <span className="text-xs text-muted">
          For trying the examples below. It is your own token: don&apos;t share it.
        </span>
      </div>
      {state === "failed" && <Alert>Couldn&apos;t get a token. Sign in again and retry.</Alert>}
      <p className="text-xs text-muted">Long-lived API keys for apps aren&apos;t available yet.</p>
    </Card>
  );
}

const methodTone = { GET: "accent", POST: "ok", PUT: "warn", DELETE: "danger" } as const;

function TableApi({ projectId, base, table }: { projectId: number; base: string; table: DataTable }) {
  const url = `${base}/${table.name}`;
  const writable = table.columns.filter((column) => column.isWritable);
  const example = JSON.stringify(Object.fromEntries(writable.map((column) => [column.name, sampleValue(column)])), null, 2);
  const [tab, setTab] = useState<"curl" | "fetch">("curl");

  const endpoints: { method: keyof typeof methodTone; path: string; does: string }[] = [
    { method: "GET", path: "", does: "List rows: ?page=1&pageSize=25 (max 100), ?sort=column or ?sort=-column" },
    { method: "GET", path: "/{id}", does: "One row" },
    { method: "POST", path: "", does: "Add a row → 201 with the stored row" },
    { method: "PUT", path: "/{id}", does: "Replace a row: fields left out get their default, or NULL" },
    { method: "DELETE", path: "/{id}", does: "Delete a row → 204" },
  ];
  if (table.labelColumn) {
    endpoints.push({ method: "GET", path: "/lookup", does: `Id and ${table.labelColumn} of rows, ?q= to search (for pickers)` });
  }

  const curl = `# List, sorted by id
curl -s "${url}?page=1&pageSize=25" -H "Authorization: Bearer $TOKEN"

# Add a row
curl -s -X POST ${url} \\
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \\
  -d '${JSON.stringify(JSON.parse(example))}'

# Replace row 1, then delete it
curl -s -X PUT ${url}/1 \\
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \\
  -d '${JSON.stringify(JSON.parse(example))}'
curl -s -X DELETE ${url}/1 -H "Authorization: Bearer $TOKEN"`;

  const fetchCode = `const headers = { Authorization: \`Bearer \${token}\`, "Content-Type": "application/json" };

// List
const page = await fetch("${url}?page=1&pageSize=25", { headers }).then((r) => r.json());
// page = { items: [...], page: 1, pageSize: 25, total: … }

// Add a row
const response = await fetch("${url}", {
  method: "POST",
  headers,
  body: JSON.stringify(${example.replaceAll("\n", "\n  ")}),
});
if (!response.ok) console.error(await response.json()); // errors: { column: ["message"] }`;

  return (
    <Card id={`table-${table.name}`} className="grid scroll-mt-6 gap-4 p-5">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h2 className="font-mono text-lg font-semibold">{table.name}</h2>
        <Link href={`/projects/${projectId}/data/${table.name}`} className="text-sm font-medium text-accent hover:underline">
          Browse data
        </Link>
      </div>

      <ul className="grid gap-1.5 text-sm">
        {endpoints.map((endpoint) => (
          <li key={endpoint.method + endpoint.path} className="flex flex-wrap items-center gap-2">
            <span className="w-16">
              <Badge tone={methodTone[endpoint.method]}>{endpoint.method}</Badge>
            </span>
            <span className="font-mono text-xs">
              /{table.name}
              {endpoint.path}
            </span>
            <span className="text-muted">{endpoint.does}</span>
          </li>
        ))}
      </ul>

      <div className="overflow-x-auto">
        <table className="w-full text-sm">
          <thead className="border-b border-border text-left text-xs text-muted">
            <tr>
              <th className="py-1.5 pr-3 font-medium">Field</th>
              <th className="py-1.5 pr-3 font-medium">Type</th>
              <th className="py-1.5 pr-3 font-medium">JSON</th>
              <th className="py-1.5 font-medium">Notes</th>
            </tr>
          </thead>
          <tbody>
            <tr className="border-b border-border">
              <td className="py-1.5 pr-3 font-mono">id</td>
              <td className="py-1.5 pr-3 font-mono text-xs">BigInt</td>
              <td className="py-1.5 pr-3 text-muted">number</td>
              <td className="py-1.5 text-muted">Set by the database; never send it.</td>
            </tr>
            {table.columns.map((column) => (
              <tr key={column.name} className="border-b border-border last:border-0">
                <td className="py-1.5 pr-3 font-mono">
                  {column.name}
                  {isRequired(column) && column.isWritable && (
                    <span className="text-danger" title="Required when adding a row">
                      {" "}
                      *
                    </span>
                  )}
                </td>
                <td className="py-1.5 pr-3 font-mono text-xs">{column.type}</td>
                <td className="py-1.5 pr-3 text-muted">{jsonShape(column)}</td>
                <td className="py-1.5 text-muted">{notes(column).join(" · ")}</td>
              </tr>
            ))}
          </tbody>
        </table>
        <p className="mt-1.5 text-xs text-muted">
          <span className="text-danger">*</span> required when adding a row.
        </p>
      </div>

      <div className="grid gap-2">
        <div role="tablist" aria-label="Example language" className="flex gap-1">
          {(["curl", "fetch"] as const).map((name) => (
            <button
              key={name}
              type="button"
              role="tab"
              aria-selected={tab === name}
              onClick={() => setTab(name)}
              className={`rounded-md px-2.5 py-1 text-sm ${tab === name ? "bg-accent-soft font-medium text-accent" : "text-muted hover:bg-surface-muted"}`}
            >
              {name === "curl" ? "curl" : "JavaScript"}
            </button>
          ))}
        </div>
        <CodeBlock code={tab === "curl" ? curl : fetchCode} />
      </div>
    </Card>
  );
}

function RulesCard() {
  return (
    <Card className="grid gap-2 p-5 text-sm">
      <h2 className="font-semibold">Errors</h2>
      <ul className="grid gap-1 text-muted">
        <li>
          <span className="font-mono text-foreground">400</span> invalid fields, all at once:{" "}
          <span className="font-mono">{`{ "errors": { "price": ["Must have at most 2 decimals."] } }`}</span>
        </li>
        <li>
          <span className="font-mono text-foreground">404</span> unknown table (or not applied yet), or no row with that id
        </li>
        <li>
          <span className="font-mono text-foreground">409</span> a unique value that already exists, a row other rows still
          reference, or the database changed outside CoreFoundry
        </li>
        <li>
          <span className="font-mono text-foreground">401</span> missing or expired token: sign in again
        </li>
      </ul>
    </Card>
  );
}

function jsonShape(column: DataColumn) {
  switch (column.dataType) {
    case "Int":
    case "BigInt":
      return "integer";
    case "Decimal":
      return "string (or number)";
    case "Bool":
      return "true / false";
    case "Date":
      return "\"yyyy-MM-dd\"";
    case "DateTime":
      return "ISO-8601 string";
    case "Json":
      return "any JSON";
    case null:
      return "string (read-only)";
    default:
      return "string";
  }
}

function notes(column: DataColumn) {
  return [
    column.references ? `id of a ${column.references} row` : null,
    column.dataType === "Varchar" ? `up to ${column.length} characters` : null,
    column.dataType === "Decimal" ? `returned as a string, at most ${column.scale} decimals` : null,
    column.dataType === "DateTime" ? "an offset is converted to UTC" : null,
    column.isUnique ? "unique" : null,
    column.default !== null ? `default ${column.default}` : null,
    column.isNullable ? "may be null" : null,
    !column.isWritable ? "read-only (changed outside CoreFoundry)" : null,
  ].filter((note): note is string => note !== null);
}

/** A value that the API would accept for the column, for the examples. */
function sampleValue(column: DataColumn): unknown {
  if (column.references) return 1;
  switch (column.dataType) {
    case "Int":
      return 42;
    case "BigInt":
      return 1000;
    case "Decimal": {
      const scale = column.scale ?? 0;
      const integerDigits = Math.min(2, (column.precision ?? 1) - scale); // Decimal(2,2) allows none: "0.99"
      return `${integerDigits > 0 ? "9".repeat(integerDigits) : "0"}${scale > 0 ? `.${"9".repeat(Math.min(scale, 2))}` : ""}`;
    }
    case "Bool":
      return true;
    case "Varchar":
      return "text".slice(0, column.length ?? 4);
    case "Text":
      return "Some longer text";
    case "Date":
      return "2024-02-29";
    case "DateTime":
      return "2024-02-29T13:45:00";
    case "Uuid":
      return "3f2504e0-4f89-11d3-9a0c-0305e82c3301";
    case "Json":
      return { key: "value" };
    default:
      return null;
  }
}

function CodeBlock({ code }: { code: string }) {
  const [copied, setCopied] = useState(false);

  async function copy() {
    await navigator.clipboard.writeText(code);
    setCopied(true);
    setTimeout(() => setCopied(false), 1500);
  }

  return (
    <div className="relative">
      <pre className="overflow-auto rounded-md border border-border bg-surface-muted p-3 pr-20 font-mono text-xs leading-relaxed">{code}</pre>
      <Button
        variant="secondary"
        className="absolute top-2 right-2 h-7 px-2 text-xs"
        onClick={() => void copy().catch(() => undefined)}
      >
        {copied ? "Copied" : "Copy"}
      </Button>
    </div>
  );
}
