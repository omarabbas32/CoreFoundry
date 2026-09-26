"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import { useRouter } from "next/navigation";
import { useState } from "react";
import { useForm } from "react-hook-form";
import type { z } from "zod";
import { Alert, Button, Dialog, Field, Input } from "@/components/ui";
import { ApiError } from "@/lib/api";
import { useCreateTable } from "@/lib/queries";
import { commonColumns } from "@/lib/column-suggestions";
import { tableNameSchema, toColumnInput } from "@/lib/schema-rules";

/** Columns a new table can start with; most tables want both. */
const starters = commonColumns.filter((preset) => preset.label === "name" || preset.label === "created_at");

export function CreateTableDialog({ projectId, open, onClose }: { projectId: number; open: boolean; onClose: () => void }) {
  const router = useRouter();
  const create = useCreateTable(projectId);
  const {
    register,
    handleSubmit,
    setError,
    reset,
    formState: { errors },
  } = useForm<z.infer<typeof tableNameSchema>>({ resolver: zodResolver(tableNameSchema), defaultValues: { name: "" } });
  const [startWith, setStartWith] = useState<string[]>(starters.map((preset) => preset.label));

  function close() {
    reset();
    create.reset();
    onClose();
  }

  const submit = handleSubmit(async ({ name }) => {
    try {
      const columns = starters.filter((preset) => startWith.includes(preset.label)).map((preset) => toColumnInput(preset.values));
      const table = await create.mutateAsync({ name: name.trim(), columns });
      close();
      router.push(`/projects/${projectId}/tables/${table.id}`);
    } catch (error) {
      if (error instanceof ApiError && error.fieldErrors.name) setError("name", { message: error.fieldErrors.name[0] });
    }
  });

  return (
    <Dialog open={open} onClose={close} title="New table">
      <form onSubmit={submit} noValidate className="grid gap-4">
        <p className="text-sm text-muted">
          Lower-case letters, digits and underscores, starting with a letter; a plural noun reads best (books, order_items).
        </p>
        {create.error && !(create.error instanceof ApiError && create.error.fieldErrors.name) && (
          <Alert>{create.error.message}</Alert>
        )}
        <Field label="Name" htmlFor="table-name" error={errors.name?.message}>
          <Input
            id="table-name"
            placeholder="books"
            autoFocus
            autoComplete="off"
            spellCheck={false}
            className="font-mono"
            aria-invalid={Boolean(errors.name)}
            {...register("name")}
          />
        </Field>
        <fieldset className="grid gap-1.5">
          <legend className="text-sm font-medium">Start with</legend>
          <p className="text-xs text-muted">
            <span className="font-mono">id</span> is always added. Untick what this table doesn&apos;t need.
          </p>
          <div className="flex flex-wrap gap-x-5 gap-y-1.5 pt-1">
            {starters.map((preset) => (
              <label key={preset.label} className="flex items-center gap-2 text-sm">
                <input
                  type="checkbox"
                  className="size-4 accent-accent"
                  checked={startWith.includes(preset.label)}
                  onChange={(event) =>
                    setStartWith((current) =>
                      event.target.checked ? [...current, preset.label] : current.filter((label) => label !== preset.label),
                    )
                  }
                />
                <span className="font-mono">{preset.label}</span>
                <span className="text-xs text-muted">
                  {preset.values.dataType === "DateTime" ? "DateTime, set when a row is added" : "Varchar(120), required"}
                </span>
              </label>
            ))}
          </div>
        </fieldset>
        <div className="flex justify-end gap-2">
          <Button type="button" variant="secondary" onClick={close}>
            Cancel
          </Button>
          <Button type="submit" loading={create.isPending}>
            Create table
          </Button>
        </div>
      </form>
    </Dialog>
  );
}
