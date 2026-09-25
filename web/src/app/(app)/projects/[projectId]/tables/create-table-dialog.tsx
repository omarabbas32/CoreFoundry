"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import { useRouter } from "next/navigation";
import { useForm } from "react-hook-form";
import type { z } from "zod";
import { Alert, Button, Dialog, Field, Input } from "@/components/ui";
import { ApiError } from "@/lib/api";
import { useCreateTable } from "@/lib/queries";
import { tableNameSchema } from "@/lib/schema-rules";

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

  function close() {
    reset();
    create.reset();
    onClose();
  }

  const submit = handleSubmit(async ({ name }) => {
    try {
      const table = await create.mutateAsync(name.trim());
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
          Lower-case letters, digits and underscores, starting with a letter. You&apos;ll add columns next.
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
