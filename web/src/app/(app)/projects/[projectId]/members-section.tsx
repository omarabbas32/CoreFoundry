"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import { useRouter } from "next/navigation";
import { useState } from "react";
import { useForm } from "react-hook-form";
import { z } from "zod";
import { RoleBadge } from "@/components/project-badges";
import { Alert, Badge, Button, Card, ConfirmDialog, Field, Input, Select } from "@/components/ui";
import { ApiError } from "@/lib/api";
import { useAuth } from "@/lib/auth";
import {
  useCancelInvitation,
  useChangeMemberRole,
  useInviteMember,
  useMembers,
  useProjectInvitations,
  useRemoveMember,
  useTransferOwnership,
} from "@/lib/queries";
import { atLeast, type Member, type Project, type ProjectRole } from "@/lib/types";

type PendingAction = { kind: "remove" | "leave" | "transfer"; member: Member } | null;

export function MembersSection({ project }: { project: Project }) {
  const router = useRouter();
  const { user } = useAuth();
  const members = useMembers(project.id);
  const changeRole = useChangeMemberRole(project.id);
  const remove = useRemoveMember(project.id);
  const transfer = useTransferOwnership(project.id);
  const [pending, setPending] = useState<PendingAction>(null);

  const canManage = atLeast(project.role, "Admin");
  const isOwner = project.role === "Owner";

  async function confirmPending() {
    if (!pending) return;
    if (pending.kind === "transfer") {
      await transfer.mutateAsync(pending.member.userId);
    } else {
      await remove.mutateAsync(pending.member.userId);
      if (pending.kind === "leave") {
        router.replace("/projects");
        return;
      }
    }
    setPending(null);
  }

  // Failures are shown inside the dialog via the mutation's error state.
  const confirmAndReport = () => void confirmPending().catch(() => undefined);

  function closePending() {
    setPending(null);
    remove.reset();
    transfer.reset();
  }

  return (
    <Card className="grid gap-4 p-5">
      <div>
        <h2 className="font-semibold">Members</h2>
        <p className="text-sm text-muted">
          Owners and admins manage members. Developers can design the schema and work with data.
        </p>
      </div>

      {members.error && <Alert>{members.error.message}</Alert>}
      {changeRole.error && <Alert>{changeRole.error.message}</Alert>}

      <ul className="divide-y divide-border rounded-md border border-border">
        {members.isPending && <li className="px-3 py-3 text-sm text-muted">Loading members…</li>}
        {members.data?.map((member) => {
          const isMe = member.userId === user?.id;
          const editable = canManage && member.role !== "Owner";
          return (
            <li key={member.userId} className="flex flex-wrap items-center gap-x-3 gap-y-2 px-3 py-2.5">
              <span className="min-w-0 flex-1 truncate text-sm">
                {member.email}
                {isMe && <span className="text-muted"> (you)</span>}
              </span>

              {editable ? (
                <Select
                  aria-label={`Role of ${member.email}`}
                  value={member.role}
                  disabled={changeRole.isPending}
                  onChange={(event) =>
                    changeRole.mutate({ userId: member.userId, role: event.target.value as ProjectRole })
                  }
                >
                  <option value="Admin">Admin</option>
                  <option value="Developer">Developer</option>
                </Select>
              ) : (
                <RoleBadge role={member.role} />
              )}

              {isOwner && !isMe && (
                <Button variant="ghost" onClick={() => setPending({ kind: "transfer", member })}>
                  Make owner
                </Button>
              )}
              {member.role !== "Owner" && isMe && (
                <Button variant="ghost" onClick={() => setPending({ kind: "leave", member })}>
                  Leave
                </Button>
              )}
              {member.role !== "Owner" && !isMe && canManage && (
                <Button variant="ghost" onClick={() => setPending({ kind: "remove", member })}>
                  Remove
                </Button>
              )}
            </li>
          );
        })}
      </ul>

      <PendingInvitations projectId={project.id} canManage={canManage} />
      {canManage && <InviteForm projectId={project.id} />}

      <ConfirmDialog
        open={pending?.kind === "remove"}
        title="Remove member?"
        description={
          <>
            <span className="font-medium text-foreground">{pending?.member.email}</span> loses access to {project.name}{" "}
            immediately.
          </>
        }
        confirmLabel="Remove member"
        danger
        pending={remove.isPending}
        error={remove.error?.message}
        onConfirm={confirmAndReport}
        onClose={closePending}
      />
      <ConfirmDialog
        open={pending?.kind === "leave"}
        title={`Leave ${project.name}?`}
        description="You lose access right away. An admin has to invite you again to come back."
        confirmLabel="Leave project"
        danger
        pending={remove.isPending}
        error={remove.error?.message}
        onConfirm={confirmAndReport}
        onClose={closePending}
      />
      <ConfirmDialog
        open={pending?.kind === "transfer"}
        title="Transfer ownership?"
        description={
          <>
            <span className="font-medium text-foreground">{pending?.member.email}</span> becomes the owner and can
            delete the project. You stay on as an admin.
          </>
        }
        confirmLabel="Transfer ownership"
        pending={transfer.isPending}
        error={transfer.error?.message}
        onConfirm={confirmAndReport}
        onClose={closePending}
      />
    </Card>
  );
}

const addSchema = z.object({
  email: z.email("Enter a valid email address."),
  role: z.enum(["Admin", "Developer"]),
});

/** Invitations nobody has answered yet; admins can take one back. */
function PendingInvitations({ projectId, canManage }: { projectId: number; canManage: boolean }) {
  const invitations = useProjectInvitations(projectId);
  const cancel = useCancelInvitation(projectId);
  if (!invitations.data || invitations.data.length === 0) return null;

  return (
    <div className="grid gap-2 border-t border-border pt-4" data-testid="pending-invitations">
      <h3 className="text-sm font-semibold">Waiting for an answer</h3>
      {cancel.error && <Alert>{cancel.error.message}</Alert>}
      <ul className="grid gap-2">
        {invitations.data.map((invitation) => (
          <li key={invitation.id} className="flex flex-wrap items-center gap-x-3 gap-y-1 text-sm">
            <span className="min-w-0 flex-1 truncate">{invitation.email}</span>
            <Badge tone="warn">Invited · {invitation.role}</Badge>
            <span className="text-xs text-muted">
              {new Date(invitation.createdAt).toLocaleDateString(undefined, { dateStyle: "medium" })}
            </span>
            {canManage && (
              <Button
                variant="ghost"
                className="h-8"
                loading={cancel.isPending && cancel.variables === invitation.id}
                aria-label={`Cancel the invitation for ${invitation.email}`}
                onClick={() => cancel.mutate(invitation.id)}
              >
                Cancel
              </Button>
            )}
          </li>
        ))}
      </ul>
    </div>
  );
}

function InviteForm({ projectId }: { projectId: number }) {
  const add = useInviteMember(projectId);
  const [sent, setSent] = useState<string | null>(null);
  const {
    register,
    handleSubmit,
    reset,
    setError,
    formState: { errors },
  } = useForm<z.infer<typeof addSchema>>({
    resolver: zodResolver(addSchema),
    defaultValues: { email: "", role: "Developer" },
  });

  const submit = handleSubmit(async (values) => {
    try {
      const invitation = await add.mutateAsync(values);
      setSent(invitation.email);
      reset();
    } catch (error) {
      // 404 (no account) and 409 (already a member or already invited) are about the email the user typed.
      if (error instanceof ApiError && (error.status === 404 || error.status === 409)) {
        setError("email", { message: error.message });
      }
    }
  });

  const otherError = add.error instanceof ApiError && ![404, 409].includes(add.error.status) ? add.error.message : null;

  return (
    <form onSubmit={submit} noValidate className="grid gap-3 border-t border-border pt-4">
      <h3 className="text-sm font-semibold">Invite a member</h3>
      {sent && (
        <p role="status" className="rounded-md bg-ok-soft px-3 py-2 text-sm text-ok">
          Invitation sent to <span className="font-medium">{sent}</span>. They join once they accept it from their
          notifications.
        </p>
      )}
      {otherError && <Alert>{otherError}</Alert>}
      <div className="flex flex-wrap items-start gap-3">
        <div className="min-w-56 flex-1">
          <Field label="Email" htmlFor="member-email" error={errors.email?.message}>
            <Input
              id="member-email"
              type="email"
              placeholder="teammate@example.com"
              aria-invalid={Boolean(errors.email)}
              {...register("email")}
            />
          </Field>
        </div>
        <Field label="Role" htmlFor="member-role">
          <Select id="member-role" {...register("role")}>
            <option value="Developer">Developer</option>
            <option value="Admin">Admin</option>
          </Select>
        </Field>
        <div className="grid gap-1.5">
          <span aria-hidden className="text-sm">&nbsp;</span>
          <Button type="submit" loading={add.isPending}>
            Send invitation
          </Button>
        </div>
      </div>
      <p className="text-xs text-muted">They need a CoreFoundry account first, and join when they accept.</p>
    </form>
  );
}
