import { connection } from "next/server";

type HealthReport = {
  status: string;
  checks: Record<string, { status: string; description: string | null }>;
};

const apiUrl = process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5172";

async function getHealth(): Promise<HealthReport | { error: string }> {
  try {
    const response = await fetch(`${apiUrl}/health`, { cache: "no-store" });
    return (await response.json()) as HealthReport;
  } catch {
    return { error: `Can't reach the API at ${apiUrl}. Is it running?` };
  }
}

export default async function Home() {
  // Render at request time so the status is always current.
  await connection();
  const health = await getHealth();

  return (
    <main className="mx-auto flex w-full max-w-xl flex-col gap-6 px-6 py-20">
      <h1 className="text-3xl font-semibold tracking-tight">CoreFoundry</h1>
      <p className="text-zinc-600 dark:text-zinc-400">
        Setup check: the dashboard can reach the API, and the API can reach MySQL with both accounts.
      </p>

      {"error" in health ? (
        <p className="rounded-md border border-red-300 bg-red-50 p-4 text-red-800 dark:border-red-900 dark:bg-red-950 dark:text-red-200">
          {health.error}
        </p>
      ) : (
        <section className="rounded-md border border-zinc-200 p-4 dark:border-zinc-800">
          <p className="mb-3 font-medium">
            API: <StatusBadge status={health.status} />
          </p>
          <ul className="flex flex-col gap-2 font-mono text-sm">
            {Object.entries(health.checks).map(([name, check]) => (
              <li key={name} className="flex justify-between gap-4">
                <span>{name}</span>
                <StatusBadge status={check.status} />
              </li>
            ))}
          </ul>
        </section>
      )}
    </main>
  );
}

function StatusBadge({ status }: { status: string }) {
  const healthy = status === "Healthy";
  return (
    <span
      className={
        healthy
          ? "rounded bg-green-100 px-2 py-0.5 text-green-800 dark:bg-green-950 dark:text-green-300"
          : "rounded bg-red-100 px-2 py-0.5 text-red-800 dark:bg-red-950 dark:text-red-300"
      }
    >
      {status}
    </span>
  );
}
