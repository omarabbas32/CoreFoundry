import { Card, Spinner } from "./ui";

const bar = "animate-pulse rounded bg-surface-muted motion-reduce:animate-none";

/**
 * A page's shape while its data loads: a title, then the kind of content the page will show. Feels faster than a
 * centered spinner and doesn't jump when the content arrives. The label says what's loading (visible and announced).
 */
export function PageSkeleton({
  label,
  variant = "list",
}: {
  label: string;
  variant?: "list" | "table" | "cards" | "diagram" | "detail";
}) {
  return (
    <div className="grid gap-6" role="status" aria-busy="true">
      <div className="grid gap-2">
        <div className={`h-7 w-48 ${bar}`} />
        <p className="flex items-center gap-2 text-sm text-muted">
          <Spinner />
          {label}
        </p>
      </div>

      {variant === "list" && (
        <Card className="divide-y divide-border">
          {[0, 1, 2, 3, 4].map((index) => (
            <div key={index} className="flex items-center gap-4 px-4 py-3.5">
              <div className={`h-4 ${index % 2 ? "w-40" : "w-56"} ${bar}`} />
              <div className={`ml-auto h-3 w-24 ${bar}`} />
            </div>
          ))}
        </Card>
      )}

      {variant === "table" && (
        <Card className="grid gap-0 overflow-hidden">
          <div className="flex gap-6 border-b border-border bg-surface-muted px-4 py-2.5">
            {[0, 1, 2, 3].map((index) => (
              <div key={index} className={`h-3 w-20 ${bar} bg-border`} />
            ))}
          </div>
          {[0, 1, 2, 3, 4, 5].map((index) => (
            <div key={index} className="flex gap-6 border-b border-border px-4 py-3 last:border-0">
              {[0, 1, 2, 3].map((cell) => (
                <div key={cell} className={`h-3 ${cell === 0 ? "w-10" : "w-24"} ${bar}`} />
              ))}
            </div>
          ))}
        </Card>
      )}

      {variant === "cards" && (
        <div className="grid gap-4 sm:grid-cols-2">
          {[0, 1, 2, 3].map((index) => (
            <Card key={index} className="grid gap-3 p-5">
              <div className={`h-4 w-1/3 ${bar}`} />
              <div className={`h-3 w-2/3 ${bar}`} />
              <div className={`h-3 w-1/2 ${bar}`} />
            </Card>
          ))}
        </div>
      )}

      {variant === "detail" && (
        <>
          <Card className="grid gap-3 p-5">
            <div className={`h-4 w-1/4 ${bar}`} />
            <div className={`h-3 w-3/4 ${bar}`} />
          </Card>
          <Card className="grid gap-3 p-5">
            {[0, 1, 2, 3].map((index) => (
              <div key={index} className={`h-8 ${bar}`} />
            ))}
          </Card>
        </>
      )}

      {variant === "diagram" && <Card className={`h-96 ${bar}`} />}
    </div>
  );
}
