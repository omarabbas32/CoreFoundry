"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import { useForm } from "react-hook-form";
import type { z } from "zod";
import { Alert, Button, Dialog, Field, Input } from "@/components/ui";
import { ApiError } from "@/lib/api";
import { useTableChange } from "@/lib/queries";
import { tableNameSchema } from "@/lib/schema-rules";
import type { Table } from "@/lib/types";

export function RenameTableDialog({
  projectId,
  table,
  open,
  onClose,
}: {
  projectId: number;
  table: Table;
  open: boolean;
  onClose: () => void;
}) {
  return (
    <Dialog open={open} onClose={onClose} title="Rename table">
      <RenameForm projectId={projectId} table={table} onDone={onClose} />
    </Dialog>
  );
}

function RenameForm({ projectId, table, onDone }: { projectId: number; table: Table; onDone: () => void }) {
  const change = useTableChange(projectId, table.id);
  const {
    register,
    handleSubmit,
    setError,
    formState: { errors },
  } = useForm<z.infer<typeof tableNameSchema>>({ resolver: zodResolver(tableNameSchema), defaultValues: { name: table.name } });

  const submit = handleSubmit(async ({ name }) => {
    try {
      await change.mutateAsync({ kind: "rename", name: name.trim() });
      onDone();
    } catch (error) {
      if (error instanceof ApiError && error.fieldErrors.name) setError("name", { message: error.fieldErrors.name[0] });
    }
  });

  return (
    <form onSubmit={submit} noValidate className="grid gap-4">
      {table.state === "Applied" && (
        <p className="text-sm text-muted">The table exists in the database; the schema engine will rename it there at the next apply.</p>
      )}
      {change.error && !(change.error instanceof ApiError && change.error.fieldErrors.name) && <Alert>{change.error.message}</Alert>}
      <Field label="Name" htmlFor="rename-table" error={errors.name?.message}>
        <Input
          id="rename-table"
          autoFocus
          autoComplete="off"
          spellCheck={false}
          className="font-mono"
          aria-invalid={Boolean(errors.name)}
          {...register("name")}
        />
      </Field>
      <div className="flex justify-end gap-2">
        <Button type="button" variant="secondary" onClick={onDone}>
          Cancel
        </Button>
        <Button type="submit" loading={change.isPending}>
          Rename
        </Button>
      </div>
    </form>
  );
}
