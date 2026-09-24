"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import { useForm } from "react-hook-form";
import { z } from "zod";
import { Alert, Button, Card, Field, Input } from "@/components/ui";
import { useRenameProject } from "@/lib/queries";
import type { Project } from "@/lib/types";

const schema = z.object({
  name: z.string().trim().min(1, "Give the project a name.").max(100, "Use at most 100 characters."),
});

export function RenameProjectForm({ project }: { project: Project }) {
  const rename = useRenameProject(project.id);
  const {
    register,
    handleSubmit,
    formState: { errors, isDirty },
  } = useForm<z.infer<typeof schema>>({ resolver: zodResolver(schema), values: { name: project.name } });

  const submit = handleSubmit(({ name }) => rename.mutate(name));

  return (
    <Card className="grid gap-4 p-5">
      <div>
        <h2 className="font-semibold">Project name</h2>
        <p className="text-sm text-muted">Renaming keeps the slug and the database name.</p>
      </div>
      {rename.error && <Alert>{rename.error.message}</Alert>}
      <form onSubmit={submit} noValidate className="flex flex-wrap items-end gap-3">
        <div className="min-w-56 flex-1">
          <Field label="Name" htmlFor="rename-project" error={errors.name?.message}>
            <Input id="rename-project" aria-invalid={Boolean(errors.name)} {...register("name")} />
          </Field>
        </div>
        <Button type="submit" variant="secondary" disabled={!isDirty} loading={rename.isPending}>
          Save name
        </Button>
      </form>
    </Card>
  );
}
