"use client";

import { useState, type FormEvent } from "react";
import { useAiKey, useSetAiKey } from "@/lib/queries";
import { Alert, Button, Dialog, Field, Input } from "./ui";

/**
 * The user's own Groq key for the AI assistant. Without one, CoreFoundry's key is used (with a daily limit). The
 * key is write-only: the server encrypts it and only ever shows its last characters.
 */
export function AiKeyDialog({ open, onClose }: { open: boolean; onClose: () => void }) {
  const status = useAiKey();
  const save = useSetAiKey();
  const [key, setKey] = useState("");

  function close() {
    save.reset();
    setKey("");
    onClose();
  }

  function submit(event: FormEvent) {
    event.preventDefault();
    if (key.trim()) save.mutate(key.trim(), { onSuccess: () => setKey("") });
  }

  const current = status.data;
  return (
    <Dialog open={open} onClose={close} title="AI key">
      {status.error && <Alert>{status.error.message}</Alert>}
      {current && (
        <p className="text-sm text-muted" role="status">
          {current.hasOwnKey ? (
            <>
              The assistant uses <span className="font-medium text-foreground">your key</span> (…{current.hint}).
            </>
          ) : current.hasDefaultKey ? (
            <>
              The assistant uses CoreFoundry&apos;s key, up to {current.dailyCallsOnDefaultKey} calls a day. Add your own Groq
              key for more.
            </>
          ) : (
            <>CoreFoundry has no key of its own: add your Groq key to use the assistant.</>
          )}
        </p>
      )}
      <form onSubmit={submit} className="grid gap-3">
        <Field label={current?.hasOwnKey ? "Replace your Groq key" : "Your Groq key"} htmlFor="ai-key" error={save.error?.message}>
          <Input
            id="ai-key"
            type="password"
            autoComplete="off"
            placeholder="gsk_…"
            value={key}
            onChange={(event) => setKey(event.target.value)}
          />
        </Field>
        <p className="text-xs text-muted">Stored encrypted; it&apos;s never shown again, only its last 4 characters.</p>
        <div className="flex flex-wrap justify-end gap-2">
          {current?.hasOwnKey && (
            <Button type="button" variant="secondary" loading={save.isPending && save.variables === null} onClick={() => save.mutate(null)}>
              Remove my key
            </Button>
          )}
          <Button type="submit" loading={save.isPending && save.variables !== null} disabled={!key.trim()}>
            Save key
          </Button>
        </div>
      </form>
    </Dialog>
  );
}
