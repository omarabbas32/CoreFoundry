"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useState } from "react";
import { useAuth } from "@/lib/auth";
import { AiKeyDialog } from "./ai-key-dialog";
import { Logo } from "./logo";
import { Notifications } from "./notifications";
import { Button } from "./ui";

export function AppHeader() {
  const { user, logout } = useAuth();
  const router = useRouter();
  const [signingOut, setSigningOut] = useState(false);
  const [editingKey, setEditingKey] = useState(false);

  async function signOut() {
    setSigningOut(true);
    try {
      await logout();
    } finally {
      router.replace("/login");
    }
  }

  return (
    <header className="border-b border-border bg-surface">
      <div className="mx-auto flex h-14 max-w-6xl items-center justify-between gap-4 px-4">
        <Link href="/projects" aria-label="CoreFoundry, all projects" className="rounded-md focus-visible:outline-2 focus-visible:outline-accent">
          <Logo />
        </Link>
        <div className="flex min-w-0 items-center gap-3">
          <span className="hidden truncate text-sm text-muted sm:inline">{user?.email}</span>
          <Notifications />
          <Button variant="ghost" onClick={() => setEditingKey(true)}>
            AI key
          </Button>
          <Button variant="ghost" onClick={signOut} loading={signingOut}>
            Sign out
          </Button>
        </div>
      </div>
      <AiKeyDialog open={editingKey} onClose={() => setEditingKey(false)} />
    </header>
  );
}
