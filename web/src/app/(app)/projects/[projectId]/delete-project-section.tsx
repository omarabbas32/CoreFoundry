"use client";

import { useRouter } from "next/navigation";
import { useState } from "react";
import { Alert, Button, Card, Dialog, Field, Input } from "@/components/ui";
import { useDeleteProject } from "@/lib/queries";
import type { Project } from "@/lib/types";

export function DeleteProjectSection({ project }: { project: Project }) {
  const router = useRouter();
  const remove = useDeleteProject(project.id);
  const [open, setOpen] = useState(false);
  const [typed, setTyped] = useState("");

  function close() {
    setOpen(false);
    setTyped("");
    remove.reset();
  }

  async function confirm() {
    await remove.mutateAsync();
    router.replace("/projects");
  }

  return (
    <Card className="grid gap-3 border-danger/40 p-5">
      <div>
        <h2 className="font-semibold text-danger">Delete project</h2>
        <p className="text-sm text-muted">
          Drops the <span className="font-mono">{project.databaseName}</span> database with all its tables and data,
          and removes every member. This can&apos;t be undone.
        </p>
      </div>
      <div>
        <Button variant="danger" onClick={() => setOpen(true)}>
          Delete project…
        </Button>
      </div>

      <Dialog open={open} onClose={close} title={`Delete ${project.name}?`}>
        <form
          className="grid gap-4"
          onSubmit={(event) => {
            event.preventDefault();
            confirm().catch(() => undefined); // error shown from remove.error
          }}
        >
          <p className="text-sm text-muted">
            Type <span className="font-mono font-medium text-foreground">{project.name}</span> to confirm.
          </p>
          {remove.error && <Alert>{remove.error.message}</Alert>}
          <Field label="Project name" htmlFor="confirm-delete">
            <Input id="confirm-delete" value={typed} onChange={(event) => setTyped(event.target.value)} autoComplete="off" />
          </Field>
          <div className="flex justify-end gap-2">
            <Button type="button" variant="secondary" onClick={close}>
              Cancel
            </Button>
            <Button type="submit" variant="danger" disabled={typed !== project.name} loading={remove.isPending}>
              Delete project and database
            </Button>
          </div>
        </form>
      </Dialog>
    </Card>
  );
}
