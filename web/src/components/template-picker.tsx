"use client";

import { useId } from "react";
import { useTemplates } from "@/lib/queries";

/** `key` null = no template (an empty project). */
export type TemplateChoice = { key: string | null; withSampleData: boolean };

/** Radio list of ready schemas, with a preview of their tables and the sample-data option. */
export function TemplatePicker({
  value,
  onChange,
  allowEmpty = true,
}: {
  value: TemplateChoice;
  onChange: (value: TemplateChoice) => void;
  allowEmpty?: boolean;
}) {
  const templates = useTemplates();
  const name = useId();
  const selected = templates.data?.find((template) => template.key === value.key);

  const option = (key: string | null, title: string, detail: string) => (
    <label
      key={key ?? "empty"}
      className={`flex cursor-pointer gap-2.5 rounded-md border px-3 py-2 text-sm ${value.key === key ? "border-accent bg-accent-soft/40" : "border-border hover:bg-surface-muted"}`}
    >
      <input
        type="radio"
        name={name}
        className="mt-0.5 accent-accent"
        checked={value.key === key}
        onChange={() => onChange({ key, withSampleData: key !== null && value.withSampleData })}
      />
      <span className="grid gap-0.5">
        <span className="font-medium">{title}</span>
        <span className="text-xs text-muted">{detail}</span>
      </span>
    </label>
  );

  return (
    <fieldset className="grid gap-2">
      <legend className="mb-1.5 text-sm font-medium">Start with</legend>
      {allowEmpty && option(null, "Empty project", "Design every table yourself.")}
      {templates.isPending && <p className="text-xs text-muted">Loading templates…</p>}
      {templates.error && <p className="text-xs text-danger">Couldn&apos;t load the templates: {templates.error.message}</p>}
      {templates.data?.map((template) =>
        option(template.key, `${template.name} · ${template.tables.length} tables`, template.description),
      )}

      {selected && (
        <div className="grid gap-2 rounded-md border border-border bg-surface-muted px-3 py-2">
          <p className="text-xs text-muted">Tables (created as drafts: edit them, then review the plan and apply):</p>
          <ul className="flex flex-wrap gap-1.5" data-testid="template-tables">
            {selected.tables.map((table) => (
              <li
                key={table.name}
                className="rounded bg-surface px-1.5 py-0.5 font-mono text-xs"
                title={table.references.length ? `References ${table.references.join(", ")}` : undefined}
              >
                {table.name}
              </li>
            ))}
          </ul>
          <label className="flex items-start gap-2 text-sm">
            <input
              type="checkbox"
              className="mt-0.5 size-4 accent-accent"
              checked={value.withSampleData}
              onChange={(event) => onChange({ ...value, withSampleData: event.target.checked })}
            />
            <span>
              Add sample data after the first apply{" "}
              <span className="text-muted">({selected.sampleRowCount} made-up rows)</span>
            </span>
          </label>
        </div>
      )}
    </fieldset>
  );
}
