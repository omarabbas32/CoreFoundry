"use client";

import { useRouter, useSearchParams } from "next/navigation";
import { Suspense, useEffect } from "react";
import { FoundryScene } from "@/components/foundry-scene";
import { FullPageSpinner } from "@/components/full-page-spinner";
import { Logo } from "@/components/logo";
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
    <main className="flex flex-1 items-center justify-center px-4 py-12">
      <div className="grid w-full max-w-sm items-center gap-10 lg:max-w-6xl lg:grid-cols-[minmax(0,1.35fr)_minmax(20rem,24rem)] lg:gap-14">
        {/* Wide screens: what CoreFoundry does, shown as the foundry casting a backend. */}
        <section className="hidden gap-5 lg:grid" aria-labelledby="hero-title">
          <Logo size={40} className="text-xl" />
          <div className="grid gap-2">
            <h2 id="hero-title" className="text-3xl font-semibold tracking-tight">
              Design your database. Cast your backend.
            </h2>
            <p className="max-w-xl text-muted">
              Design tables in the browser or with the AI assistant, apply them to MySQL safely, and export a C# Clean
              Architecture API: Domain, Application, Infrastructure and controllers, ready to run.
            </p>
          </div>
          <FoundryScene />
        </section>

        <div className="grid gap-6">
          <div className="grid justify-items-center gap-2 text-center lg:hidden">
            <Logo size={44} className="flex-col gap-3 text-xl" />
            <p className="text-sm text-muted">Design a database, apply it safely, export the backend.</p>
          </div>
          {children}
        </div>
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
