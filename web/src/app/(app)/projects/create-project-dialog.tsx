"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import { useRouter } from "next/navigation";
import { useState } from "react";
import { useForm } from "react-hook-form";
import { z } from "zod";
import { TemplatePicker, type TemplateChoice } from "@/components/template-picker";
import { Alert, Button, Dialog, Field, Input } from "@/components/ui";
import { ApiError } from "@/lib/api";
import { useCreateProject, useApplyTemplate } from "@/lib/queries";

const schema = z.object({
  name: z.string().trim().min(1, "Give the project a name.").max(100, "Use at most 100 characters."),
});

export function CreateProjectDialog({ open, onClose }: { open: boolean; onClose: () => void }) {
  const router = useRouter();
  const create = useCreateProject();
  const applyTemplate = useApplyTemplate();
  const [choice, setChoice] = useState<TemplateChoice>({ key: null, withSampleData: true });
  const {
    register,
    handleSubmit,
    setError,
    reset,
    formState: { errors },
  } = useForm<z.infer<typeof schema>>({ resolver: zodResolver(schema) });

  function close() {
    reset();
    create.reset();
    applyTemplate.reset();
    setChoice({ key: null, withSampleData: true });
    onClose();
  }

  const submit = handleSubmit(async ({ name }) => {
    try {
      const project = await create.mutateAsync(name);
      if (choice.key) {
        try {
          await applyTemplate.mutateAsync({ projectId: project.id, key: choice.key, withSampleData: choice.withSampleData });
        } catch {
          // The project exists; its empty designer offers the template again.
        }
      }

      close();
      router.push(choice.key ? `/projects/${project.id}/tables` : `/projects/${project.id}`);
    } catch (error) {
      if (error instanceof ApiError && error.fieldErrors.name) setError("name", { message: error.fieldErrors.name[0] });
    }
  });

  return (
    <Dialog open={open} onClose={close} title="New project">
      <form onSubmit={submit} noValidate className="grid gap-4">
        <p className="text-sm text-muted">
          CoreFoundry creates a MySQL database for the project. You can design its tables next.
        </p>
        {create.error && !(create.error instanceof ApiError && create.error.fieldErrors.name) && (
          <Alert>{create.error.message}</Alert>
        )}
        <Field label="Name" htmlFor="project-name" error={errors.name?.message}>
          <Input
            id="project-name"
            placeholder="Bookshop"
            autoFocus
            aria-invalid={Boolean(errors.name)}
            {...register("name")}
          />
        </Field>
        <TemplatePicker value={choice} onChange={setChoice} />
        <div className="flex justify-end gap-2">
          <Button type="button" variant="secondary" onClick={close}>
            Cancel
          </Button>
          <Button type="submit" loading={create.isPending || applyTemplate.isPending}>
            Create project
          </Button>
        </div>
      </form>
    </Dialog>
  );
}
