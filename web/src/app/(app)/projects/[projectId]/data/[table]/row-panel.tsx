"use client";

import { useEffect, useId, useRef, useState, type ReactNode } from "react";
import { Alert, Button, Input, Select } from "@/components/ui";
import { ApiError } from "@/lib/api";
import { buildBody, isRequired, placeholderFor, toText, type RowTexts } from "@/lib/data-form";
import { useLookup, useSaveRow } from "@/lib/queries";
import type { DataColumn, DataRow, DataTable } from "@/lib/types";

/**
 * Add or edit a row in a panel on the right. The form is generated from the applied columns:
 * an input per type, required marks, and the server's errors under each field.
 */
export function RowPanel({
  projectId,
  table,
  row,
  onClose,
  onSaved,
}: {
  projectId: number;
  table: DataTable;
  /** Null to add a row. */
  row: DataRow | null;
  onClose: () => void;
  onSaved: (row: DataRow, created: boolean) => void;
}) {
  const ref = useRef<HTMLDialogElement>(null);
  const titleId = useId();
  const save = useSaveRow(projectId, table.name);
  const [texts, setTexts] = useState<RowTexts>(() =>
    Object.fromEntries(table.columns.map((column) => [column.name, row ? toText(column, row[column.name]) : ""])),
  );
  const [clientErrors, setClientErrors] = useState<Record<string, string>>({});

  // A modal <dialog>: the browser traps focus and closes it on Esc.
  useEffect(() => {
    const dialog = ref.current;
    if (dialog && !dialog.open) dialog.showModal();
  }, []);

  const serverErrors = save.error instanceof ApiError ? save.error.fieldErrors : {};
  const fieldError = (name: string) => clientErrors[name] ?? serverErrors[name]?.join(" ");
  const knownFields = new Set(table.columns.map((column) => column.name));
  const otherErrors =
    save.error instanceof ApiError
      ? Object.entries(serverErrors)
          .filter(([field]) => !knownFields.has(field))
          .map(([field, messages]) => (field ? `${field}: ${messages.join(" ")}` : messages.join(" ")))
      : [];
  // Errors that belong to fields are shown under them; anything else (409, 500, body errors) at the top.
  const onlyFieldErrors = Object.keys(serverErrors).length > 0 && otherErrors.length === 0;

  async function submit(event: React.FormEvent) {
    event.preventDefault();
    save.reset();
    const { body, errors } = buildBody(table.columns, texts, row ? "edit" : "create");
    setClientErrors(errors);
    if (Object.keys(errors).length > 0) return;
    const saved = await save.mutateAsync({ id: row?.id ?? null, values: body });
    onSaved(saved, row === null);
  }

  const set = (name: string) => (value: string) => setTexts((current) => ({ ...current, [name]: value }));

  return (
    <dialog
      ref={ref}
      onClose={onClose}
      onClick={(event) => event.target === ref.current && onClose()}
      aria-labelledby={titleId}
      className="m-0 ml-auto h-dvh max-h-none w-[min(30rem,100vw)] border-l border-border bg-surface p-0 text-foreground shadow-xl"
      data-testid="row-panel"
    >
      <form onSubmit={(event) => void submit(event).catch(() => undefined)} className="flex h-full flex-col" noValidate>
        <div className="flex items-center justify-between gap-2 border-b border-border px-5 py-4">
          <h2 id={titleId} className="text-lg font-semibold">
            {row ? (
              <>
                Edit row {row.id} <span className="font-mono text-sm font-normal text-muted">· {table.name}</span>
              </>
            ) : (
              <>
                Add a row <span className="font-mono text-sm font-normal text-muted">· {table.name}</span>
              </>
            )}
          </h2>
          <Button type="button" variant="ghost" className="h-8" onClick={onClose} aria-label="Close">
            ✕
          </Button>
        </div>

        <div className="grid flex-1 content-start gap-4 overflow-y-auto px-5 py-4">
          {save.error && !onlyFieldErrors && <Alert>{otherErrors.length > 0 ? otherErrors.join(" ") : save.error.message}</Alert>}
          {row && (
            <p className="text-xs text-muted">
              Saving replaces the whole row: fields you empty get their default, or NULL.
            </p>
          )}
          {table.columns.map((column) => (
            <ColumnField
              key={column.name}
              projectId={projectId}
              column={column}
              value={texts[column.name] ?? ""}
              onChange={set(column.name)}
              error={fieldError(column.name)}
              creating={row === null}
            />
          ))}
        </div>

        <div className="flex justify-end gap-2 border-t border-border px-5 py-4">
          <Button type="button" variant="secondary" onClick={onClose}>
            Cancel
          </Button>
          <Button type="submit" loading={save.isPending}>
            {row ? "Save row" : "Add row"}
          </Button>
        </div>
      </form>
    </dialog>
  );
}

function ColumnField({
  projectId,
  column,
  value,
  onChange,
  error,
  creating,
}: {
  projectId: number;
  column: DataColumn;
  value: string;
  onChange: (value: string) => void;
  error?: string;
  creating: boolean;
}) {
  const id = `field-${column.name}`;
  const required = isRequired(column);
  const common = {
    id,
    "aria-invalid": error ? true : undefined,
    "aria-describedby": error ? `${id}-error` : `${id}-hint`,
    disabled: !column.isWritable,
  } as const;

  let control: ReactNode;
  if (column.references && column.isWritable) {
    control = <ReferencePicker projectId={projectId} column={column} value={value} onChange={onChange} controlProps={common} />;
  } else if (column.dataType === "Bool") {
    control = (
      <Select {...common} value={value} onChange={(event) => onChange(event.target.value)} className="w-full">
        <option value="">{column.isNullable ? "NULL" : creating && column.default !== null ? `Default (${column.default})` : "Choose…"}</option>
        <option value="true">true</option>
        <option value="false">false</option>
      </Select>
    );
  } else if (column.dataType === "Text" || column.dataType === "Json") {
    control = (
      <textarea
        {...common}
        value={value}
        onChange={(event) => onChange(event.target.value)}
        placeholder={placeholderFor(column)}
        rows={column.dataType === "Json" ? 5 : 3}
        className="w-full rounded-md border border-border bg-surface px-3 py-2 font-mono text-sm text-foreground placeholder:text-muted focus:border-accent focus:outline-none focus:ring-2 focus:ring-accent-soft aria-invalid:border-danger disabled:opacity-60"
      />
    );
  } else {
    control = (
      <Input
        {...common}
        type={column.dataType === "Date" ? "date" : "text"}
        inputMode={column.dataType === "Int" || column.dataType === "BigInt" ? "numeric" : column.dataType === "Decimal" ? "decimal" : undefined}
        maxLength={column.dataType === "Varchar" ? (column.length ?? undefined) : undefined}
        value={value}
        onChange={(event) => onChange(event.target.value)}
        placeholder={placeholderFor(column)}
        className={column.dataType === "Uuid" || column.dataType === "DateTime" ? "font-mono" : undefined}
      />
    );
  }

  const hints = [
    column.type,
    column.isUnique ? "unique" : null,
    column.default !== null ? `default ${column.default}` : null,
    column.isNullable ? "optional" : null,
    !column.isWritable ? "read-only: its type was changed outside CoreFoundry" : null,
  ].filter(Boolean);

  return (
    <div className="grid gap-1.5">
      <label htmlFor={id} className="flex items-baseline gap-1 text-sm font-medium">
        <span className="font-mono">{column.name}</span>
        {required && (
          <span className="text-danger" aria-label="required">
            *
          </span>
        )}
      </label>
      {control}
      <p id={`${id}-hint`} className="text-xs text-muted">
        {hints.join(" · ")}
      </p>
      {error && (
        <p id={`${id}-error`} className="text-sm text-danger">
          {error}
        </p>
      )}
    </div>
  );
}

/** Pick the referenced row by searching its label (or typing its id). */
function ReferencePicker({
  projectId,
  column,
  value,
  onChange,
  controlProps,
}: {
  projectId: number;
  column: DataColumn;
  value: string;
  onChange: (value: string) => void;
  controlProps: { id: string; "aria-invalid"?: true; "aria-describedby": string; disabled: boolean };
}) {
  const target = column.references!;
  const [search, setSearch] = useState("");
  const [query, setQuery] = useState("");
  const lookup = useLookup(projectId, target, query, true);

  // Search as the user types, without a request per keystroke.
  useEffect(() => {
    const timer = setTimeout(() => setQuery(search.trim()), 250);
    return () => clearTimeout(timer);
  }, [search]);

  const items = lookup.data ?? [];
  const selectedListed = items.some((item) => String(item.id) === value);

  return (
    <div className="grid gap-1.5" data-testid={`reference-${column.name}`}>
      <Input
        type="search"
        value={search}
        onChange={(event) => setSearch(event.target.value)}
        placeholder={`Search ${target} by name or id`}
        aria-label={`Search ${target}`}
        disabled={controlProps.disabled}
      />
      <Select {...controlProps} value={value} onChange={(event) => onChange(event.target.value)} className="w-full">
        <option value="">{column.isNullable ? "None (NULL)" : `Choose a ${target} row…`}</option>
        {value !== "" && !selectedListed && <option value={value}>{`#${value} (current)`}</option>}
        {items.map((item) => (
          <option key={item.id} value={String(item.id)}>
            {item.label === null ? `#${item.id}` : `#${item.id} · ${item.label}`}
          </option>
        ))}
      </Select>
      {lookup.error && <p className="text-xs text-danger">{lookup.error.message}</p>}
      {lookup.data && items.length === 0 && (
        <p className="text-xs text-muted">{query ? `No ${target} rows match.` : `${target} has no rows yet.`}</p>
      )}
    </div>
  );
}
