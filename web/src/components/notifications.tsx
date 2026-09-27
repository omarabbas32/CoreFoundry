"use client";

import Link from "next/link";
import { useEffect, useId, useRef, useState } from "react";
import { useAnswerInvitation, useMyInvitations } from "@/lib/queries";
import type { Invitation } from "@/lib/types";
import { Alert, Button } from "./ui";

/**
 * The header's notifications: invitations to join projects, each with Accept and Decline. The bell shows how many
 * are waiting; the list opens below it and closes on Escape or a click outside.
 */
export function Notifications() {
  const invitations = useMyInvitations();
  const answer = useAnswerInvitation();
  const [open, setOpen] = useState(false);
  const [joined, setJoined] = useState<Invitation | null>(null);
  const panel = useRef<HTMLDivElement>(null);
  const panelId = useId();
  const count = invitations.data?.length ?? 0;

  useEffect(() => {
    if (!open) return;
    const onKey = (event: KeyboardEvent) => event.key === "Escape" && setOpen(false);
    const onClick = (event: MouseEvent) => {
      if (panel.current && !panel.current.contains(event.target as Node)) setOpen(false);
    };
    document.addEventListener("keydown", onKey);
    document.addEventListener("mousedown", onClick);
    return () => {
      document.removeEventListener("keydown", onKey);
      document.removeEventListener("mousedown", onClick);
    };
  }, [open]);

  function respond(invitation: Invitation, accept: boolean) {
    setJoined(null);
    answer.mutate({ invitation, accept }, { onSuccess: () => accept && setJoined(invitation) });
  }

  return (
    <div ref={panel} className="relative">
      <button
        type="button"
        aria-expanded={open}
        aria-controls={panelId}
        aria-label={count > 0 ? `Notifications: ${count} invitation${count === 1 ? "" : "s"} waiting` : "Notifications"}
        onClick={() => setOpen((value) => !value)}
        className="relative grid size-9 place-items-center rounded-md text-muted transition-colors hover:bg-surface-muted hover:text-foreground focus-visible:outline-2 focus-visible:outline-accent"
      >
        <svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden>
          <path d="M6 8a6 6 0 0 1 12 0c0 7 3 9 3 9H3s3-2 3-9" />
          <path d="M10.3 21a1.94 1.94 0 0 0 3.4 0" />
        </svg>
        {count > 0 && (
          <span className="absolute -top-0.5 -right-0.5 grid min-w-4.5 place-items-center rounded-full bg-danger px-1 text-[10px] leading-4.5 font-semibold text-on-danger tabular-nums">
            {count}
          </span>
        )}
      </button>

      {open && (
        <div
          id={panelId}
          role="region"
          aria-label="Notifications"
          className="absolute right-0 z-40 mt-2 grid w-[min(22rem,calc(100vw-2rem))] gap-3 rounded-lg border border-border bg-surface p-4 shadow-xl"
        >
          <h2 className="text-sm font-semibold">Notifications</h2>
          {answer.error && <Alert>{answer.error.message}</Alert>}
          {joined && (
            <p role="status" className="rounded-md bg-ok-soft px-3 py-2 text-sm text-ok">
              You joined{" "}
              <Link href={`/projects/${joined.projectId}`} className="font-medium underline" onClick={() => setOpen(false)}>
                {joined.projectName}
              </Link>
              .
            </p>
          )}
          {count === 0 && !joined && <p className="text-sm text-muted">Nothing new. Invitations to projects show up here.</p>}
          <ul className="grid gap-3">
            {invitations.data?.map((invitation) => {
              const busy = answer.isPending && answer.variables?.invitation.id === invitation.id;
              return (
                <li key={invitation.id} className="grid gap-2 rounded-md border border-border p-3">
                  <p className="text-sm">
                    <span className="font-medium">{invitation.invitedByEmail ?? "Someone"}</span> invited you to{" "}
                    <span className="font-medium">{invitation.projectName}</span> as {invitation.role === "Admin" ? "an Admin" : "a Developer"}.
                  </p>
                  <p className="text-xs text-muted">{new Date(invitation.createdAt).toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short" })}</p>
                  <div className="flex gap-2">
                    <Button className="h-8 flex-1" loading={busy && answer.variables?.accept} disabled={answer.isPending} onClick={() => respond(invitation, true)}>
                      Accept
                    </Button>
                    <Button
                      variant="secondary"
                      className="h-8 flex-1"
                      loading={busy && !answer.variables?.accept}
                      disabled={answer.isPending}
                      onClick={() => respond(invitation, false)}
                    >
                      Decline
                    </Button>
                  </div>
                </li>
              );
            })}
          </ul>
        </div>
      )}
    </div>
  );
}
