"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import { useState } from "react";
import { useForm, useWatch } from "react-hook-form";
import { Alert, Button, Dialog, Field, Input, Select } from "@/components/ui";
import { ApiError } from "@/lib/api";
import { commonColumns, referencePresets, suggestFromName, type ColumnPreset } from "@/lib/column-suggestions";
import { useTableChange } from "@/lib/queries";
import { columnFormSchema, toColumnInput, typeInfo, type ColumnFormValues } from "@/lib/schema-rules";
import { dataTypes, type Column, type DataType, type ReferenceAction, type TableSummary } from "@/lib/types";

const serverFields = [
  "name", "dataType", "length", "precision", "scale", "isUnique", "defaultValue", "referencesTableId", "onDelete",
] as const;

const onDeleteLabels: Record<ReferenceAction, string> = {
  Restrict: "Restrict: refuse to delete a referenced row",
  Cascade: "Cascade: delete the rows that reference it",
  SetNull: "Set null: clear this column",
};

const emptyForm: ColumnFormValues = {
  name: "",
  dataType: "Varchar",
  length: "255",
  precision: "",
  scale: "",
  isNullable: true,
  isUnique: false,
  defaultValue: "",
  referencesTableId: "",
  onDelete: "Restrict",
};

function formFor(column: Column | null): ColumnFormValues {
  if (!column) return emptyForm;
  return {
    name: column.name,
    dataType: column.dataType,
    length: column.length?.toString() ?? "",
    precision: column.precision?.toString() ?? "",
    scale: column.scale?.toString() ?? "",
    isNullable: column.isNullable,
    isUnique: column.isUnique,
    defaultValue: column.defaultValue ?? "",
    referencesTableId: column.referencesTableId?.toString() ?? "",
    onDelete: column.onDelete ?? "Restrict",
  };
}

/** Adds a column (column = null) or edits one. Validates in the browser first; the API's answer is final. */
export function ColumnDialog({
  projectId,
  tableId,
  column,
  tables,
  open,
  onClose,
}: {
  projectId: number;
  tableId: number;
  column: Column | null;
  /** The project's tables, for the References picker. */
  tables: TableSummary[];
  open: boolean;
  onClose: () => void;
}) {
  return (
    <Dialog open={open} onClose={onClose} title={column ? `Edit ${column.name}` : "Add column"}>
      {/* Keyed so each opening starts from the column's current values. */}
      <ColumnForm
        key={column?.id ?? "new"}
        projectId={projectId}
        tableId={tableId}
        column={column}
        tables={tables}
        onDone={onClose}
      />
    </Dialog>
  );
}

function ColumnForm({
  projectId,
  tableId,
  column,
  tables,
  onDone,
}: {
  projectId: number;
  tableId: number;
  column: Column | null;
  tables: TableSummary[];
  onDone: () => void;
}) {
  const change = useTableChange(projectId, tableId);
  const {
    register,
    handleSubmit,
    setError,
    setValue,
    getValues,
    reset,
    setFocus,
    control,
    formState: { errors, dirtyFields },
  } = useForm<ColumnFormValues>({ resolver: zodResolver(columnFormSchema), defaultValues: formFor(column) });
  // Which button submitted: "Add and add another" keeps the dialog open for the next column.
  const [action, setAction] = useState<"add" | "another">("add");
  const [added, setAdded] = useState<string[]>([]);
  const [suggested, setSuggested] = useState<string | null>(null);
  const adding = column === null;

  const dataType = useWatch({ control, name: "dataType" });
  const referencesTableId = useWatch({ control, name: "referencesTableId" });
  const isReference = referencesTableId !== "";
  const info = typeInfo[dataType];
  // Tables pending drop can't be referenced (the API refuses too); keep a column's current target listed.
  const targets = tables.filter((table) => table.state !== "PendingDrop" || table.id === column?.referencesTableId);

  // A reference holds the target's id: BigInt, no length/precision/scale, no default.
  function onReferenceChange(target: string) {
    if (target === "") return;
    setValue("dataType", "BigInt");
    setValue("defaultValue", "");
  }

  // Pre-fill the parameters a type needs, so switching to Varchar or Decimal starts from something valid,
  // and drop a default the new type can't have (its input is hidden, so its error would be invisible).
  function onTypeChange(type: DataType) {
    if (!typeInfo[type].defaultHint) setValue("defaultValue", "");
    if (type === "Varchar" && !getValues("length")) setValue("length", "255");
    if (type === "Decimal") {
      if (!getValues("precision")) setValue("precision", "10");
      if (!getValues("scale")) setValue("scale", "2");
    }
  }

  /** Fills the whole form from a common column; the user can still change anything before adding it. */
  function applyPreset(preset: ColumnPreset) {
    reset(preset.values);
    setSuggested(null);
    setFocus("name");
  }

  /** When adding, guesses the type from the name (email, *_at, price, author_id…) unless the user already chose one. */
  function onNameBlur(name: string) {
    if (!adding || dirtyFields.dataType || dirtyFields.referencesTableId) return;
    const suggestion = suggestFromName(name, tables, tableId);
    if (!suggestion) {
      setSuggested(null);
      return;
    }

    const { reason, ...fields } = suggestion;
    for (const [field, value] of Object.entries(fields) as [keyof ColumnFormValues, string | boolean][]) {
      setValue(field, value as never);
    }
    if (fields.dataType) onTypeChange(fields.dataType);
    setSuggested(reason);
  }

  const submit = handleSubmit(async (values, event) => {
    const submitter = (event?.nativeEvent as SubmitEvent | undefined)?.submitter;
    const again = submitter?.getAttribute("value") === "another";
    setAction(again ? "another" : "add");
    const input = toColumnInput(values);
    try {
      await change.mutateAsync(
        column ? { kind: "updateColumn", columnId: column.id, column: input } : { kind: "addColumn", column: input },
      );
      if (adding && again) {
        setAdded((names) => [...names, input.name]);
        setSuggested(null);
        reset(formFor(null));
        setFocus("name");
        return;
      }
      onDone();
    } catch (error) {
      if (!(error instanceof ApiError)) return;
      for (const field of serverFields) {
        const message = error.fieldErrors[field]?.[0];
        if (message) setError(field, { message });
      }
    }
  });

  // Field errors from the API are shown next to their inputs; anything else goes in the alert.
  const apiError = change.error instanceof ApiError ? change.error : null;
  const fieldErrorShown = apiError !== null && serverFields.some((field) => apiError.fieldErrors[field]);

  return (
    <form onSubmit={submit} noValidate className="grid gap-4">
      {added.length > 0 && (
        <p role="status" className="rounded-md bg-ok-soft px-3 py-2 text-sm text-ok">
          Added <span className="font-mono">{added.join(", ")}</span>. Add the next column, or close when you&apos;re done.
        </p>
      )}
      {change.error && !fieldErrorShown && <Alert>{change.error.message}</Alert>}

      {adding && <Presets tables={tables} tableId={tableId} onPick={applyPreset} />}

      <div className="grid gap-4 sm:grid-cols-2">
        <Field label="Name" htmlFor="column-name" error={errors.name?.message}>
          <Input
            id="column-name"
            placeholder="title"
            autoFocus
            autoComplete="off"
            spellCheck={false}
            className="font-mono"
            aria-invalid={Boolean(errors.name)}
            {...register("name", { onBlur: (event) => onNameBlur(event.target.value) })}
          />
        </Field>
        <Field label="Type" htmlFor="column-type" error={errors.dataType?.message}>
          {isReference ? (
            <p id="column-type" className="flex h-9 items-center text-sm text-muted">
              BigInt · holds the referenced id
            </p>
          ) : (
            <Select
              id="column-type"
              className="w-full"
              {...register("dataType", { onChange: (event) => onTypeChange(event.target.value as DataType) })}
            >
              {dataTypes.map((type) => (
                <option key={type} value={type}>
                  {type} · {typeInfo[type].sql}
                </option>
              ))}
            </Select>
          )}
        </Field>
      </div>

      {suggested && (
        <p className="-mt-2 text-xs text-muted">
          Set up as {suggested}, from the name. Change anything below.
        </p>
      )}

      <div className="grid gap-4 sm:grid-cols-2">
        <Field label="References" htmlFor="column-references" error={errors.referencesTableId?.message}>
          <Select
            id="column-references"
            className="w-full"
            {...register("referencesTableId", { onChange: (event) => onReferenceChange(event.target.value) })}
          >
            <option value="">Nothing (plain column)</option>
            {targets.map((table) => (
              <option key={table.id} value={table.id.toString()}>
                {table.name}
                {table.id === tableId ? " (this table)" : ""}
              </option>
            ))}
          </Select>
        </Field>
        {isReference && (
          <Field label="On delete" htmlFor="column-on-delete" error={errors.onDelete?.message}>
            <Select id="column-on-delete" className="w-full" {...register("onDelete")}>
              {(Object.keys(onDeleteLabels) as ReferenceAction[]).map((action) => (
                <option key={action} value={action}>
                  {onDeleteLabels[action]}
                </option>
              ))}
            </Select>
          </Field>
        )}
      </div>

      {!isReference && info.params === "length" && (
        <Field label="Length (characters)" htmlFor="column-length" error={errors.length?.message}>
          <Input id="column-length" inputMode="numeric" aria-invalid={Boolean(errors.length)} {...register("length")} />
        </Field>
      )}
      {!isReference && info.params === "precision" && (
        <div className="grid gap-4 sm:grid-cols-2">
          <Field label="Precision (total digits)" htmlFor="column-precision" error={errors.precision?.message}>
            <Input id="column-precision" inputMode="numeric" aria-invalid={Boolean(errors.precision)} {...register("precision")} />
          </Field>
          <Field label="Scale (digits after the point)" htmlFor="column-scale" error={errors.scale?.message}>
            <Input id="column-scale" inputMode="numeric" aria-invalid={Boolean(errors.scale)} {...register("scale")} />
          </Field>
        </div>
      )}

      <div className="grid gap-1.5">
        <div className="flex flex-wrap gap-x-6 gap-y-2">
          <label className="flex items-center gap-2 text-sm">
            <input type="checkbox" className="size-4 accent-accent" {...register("isNullable")} />
            Nullable
          </label>
          <label className="flex items-center gap-2 text-sm">
            <input type="checkbox" className="size-4 accent-accent" {...register("isUnique")} />
            Unique
          </label>
        </div>
        {errors.isUnique && <p className="text-sm text-danger">{errors.isUnique.message}</p>}
      </div>

      {isReference ? (
        <p className="text-sm text-muted">A column that references a table can&apos;t have a default.</p>
      ) : info.defaultHint ? (
        <Field label="Default (optional)" htmlFor="column-default" error={errors.defaultValue?.message}>
          <Input
            id="column-default"
            placeholder={info.defaultHint}
            autoComplete="off"
            spellCheck={false}
            className="font-mono"
            aria-invalid={Boolean(errors.defaultValue)}
            {...register("defaultValue")}
          />
        </Field>
      ) : (
        <p className="text-sm text-muted">{dataType} columns can&apos;t have a default.</p>
      )}

      <div className="flex justify-end gap-2">
        <Button type="button" variant="ghost" onClick={onDone}>
          {added.length > 0 ? "Done" : "Cancel"}
        </Button>
        {/* First submit button, so Enter in a field adds the column and keeps the dialog open for the next one. */}
        {adding && (
          <Button type="submit" name="action" value="another" variant="secondary" loading={change.isPending && action === "another"}>
            Add and add another
          </Button>
        )}
        <Button type="submit" name="action" value="add" loading={change.isPending && action === "add"}>
          {column ? "Save column" : "Add column"}
        </Button>
      </div>
    </form>
  );
}

/** One-click starting points for a new column: common ones, and a reference to each other table. */
function Presets({ tables, tableId, onPick }: { tables: TableSummary[]; tableId: number; onPick: (preset: ColumnPreset) => void }) {
  const references = referencePresets(tables, tableId);
  const chip =
    "rounded-full border border-border bg-surface px-2.5 py-1 font-mono text-xs transition-colors hover:border-accent/50 hover:text-accent";
  return (
    <div className="grid gap-2 rounded-md bg-surface-muted p-3">
      <div className="grid gap-1.5">
        <p className="text-xs text-muted">Common columns</p>
        <div className="flex flex-wrap gap-1.5">
          {commonColumns.map((preset) => (
            <button key={preset.label} type="button" className={chip} onClick={() => onPick(preset)}>
              {preset.label}
            </button>
          ))}
        </div>
      </div>
      {references.length > 0 && (
        <div className="grid gap-1.5">
          <p className="text-xs text-muted">Reference another table</p>
          <div className="flex flex-wrap gap-1.5">
            {references.map((preset) => (
              <button key={preset.label} type="button" className={chip} onClick={() => onPick(preset)}>
                {preset.label}
              </button>
            ))}
          </div>
        </div>
      )}
    </div>
  );
}
