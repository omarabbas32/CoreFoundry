"use client";

import { useState } from "react";
import { TemplatePicker, type TemplateChoice } from "@/components/template-picker";
import { Alert, Button, Dialog } from "@/components/ui";
import { useApplyTemplate } from "@/lib/queries";

/** Fills an empty project with a template's draft tables. */
export function TemplateDialog({ projectId, open, onClose }: { projectId: number; open: boolean; onClose: () => void }) {
  const applyTemplate = useApplyTemplate();
  const [choice, setChoice] = useState<TemplateChoice>({ key: "ecommerce", withSampleData: true });

  function close() {
    applyTemplate.reset();
    onClose();
  }

  async function create() {
    if (!choice.key) return;
    await applyTemplate.mutateAsync({ projectId, key: choice.key, withSampleData: choice.withSampleData });
    close();
  }

  return (
    <Dialog open={open} onClose={close} title="Start from a template">
      <TemplatePicker value={choice} onChange={setChoice} allowEmpty={false} />
      {applyTemplate.error && <Alert>{applyTemplate.error.message}</Alert>}
      <div className="flex justify-end gap-2">
        <Button type="button" variant="secondary" onClick={close}>
          Cancel
        </Button>
        <Button
          type="button"
          loading={applyTemplate.isPending}
          disabled={!choice.key}
          onClick={() => void create().catch(() => undefined)} // shown in the dialog
        >
          Create tables
        </Button>
      </div>
    </Dialog>
  );
}
