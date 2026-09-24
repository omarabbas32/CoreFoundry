"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useState } from "react";
import { useAuth } from "@/lib/auth";
import { Button } from "./ui";

export function AppHeader() {
  const { user, logout } = useAuth();
  const router = useRouter();
  const [signingOut, setSigningOut] = useState(false);

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
      <div className="mx-auto flex h-14 max-w-5xl items-center justify-between gap-4 px-4">
        <Link href="/projects" className="font-semibold tracking-tight">
          Core<span className="text-accent">Foundry</span>
        </Link>
        <div className="flex min-w-0 items-center gap-3">
          <span className="truncate text-sm text-muted">{user?.email}</span>
          <Button variant="ghost" onClick={signOut} loading={signingOut}>
            Sign out
          </Button>
        </div>
      </div>
    </header>
  );
}
