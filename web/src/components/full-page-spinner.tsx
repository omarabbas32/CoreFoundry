import { Spinner } from "./ui";

export function FullPageSpinner({ label = "Loading…" }: { label?: string }) {
  return (
    <div role="status" className="flex flex-1 items-center justify-center gap-2 py-24 text-sm text-muted">
      <Spinner />
      {label}
    </div>
  );
}
