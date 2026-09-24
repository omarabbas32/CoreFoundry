"use client";

import { useRouter, useSearchParams } from "next/navigation";
import { Suspense, useEffect } from "react";
import { FullPageSpinner } from "@/components/full-page-spinner";
import { useAuth } from "@/lib/auth";
import { safeNext } from "@/lib/navigation";

/** Public pages (sign in / register). Already signed in? Go straight to where you were headed. */
function RedirectIfSignedIn({ children }: { children: React.ReactNode }) {
  const { status } = useAuth();
  const router = useRouter();
  const next = safeNext(useSearchParams().get("next"));

  useEffect(() => {
    if (status === "signedIn") router.replace(next);
  }, [status, next, router]);

  if (status !== "signedOut") return <FullPageSpinner />;

  return (
    <main className="flex flex-1 items-center justify-center px-4 py-16">
      <div className="grid w-full max-w-sm gap-6">
        <p className="text-center text-lg font-semibold tracking-tight">
          Core<span className="text-accent">Foundry</span>
        </p>
        {children}
      </div>
    </main>
  );
}

export default function AuthLayout({ children }: LayoutProps<"/">) {
  return (
    <Suspense fallback={<FullPageSpinner />}>
      <RedirectIfSignedIn>{children}</RedirectIfSignedIn>
    </Suspense>
  );
}
