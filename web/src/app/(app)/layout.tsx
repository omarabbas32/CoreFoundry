"use client";

import { usePathname, useRouter } from "next/navigation";
import { useEffect } from "react";
import { AppHeader } from "@/components/app-header";
import { FullPageSpinner } from "@/components/full-page-spinner";
import { useAuth } from "@/lib/auth";

/**
 * Signed-in area. The API is the real gatekeeper; this only avoids rendering pages that would
 * immediately get 401s, and sends the visitor to sign in with a way back.
 */
export default function AppLayout({ children }: LayoutProps<"/">) {
  const { status } = useAuth();
  const router = useRouter();
  const pathname = usePathname();

  useEffect(() => {
    if (status === "signedOut") router.replace(`/login?next=${encodeURIComponent(pathname)}`);
  }, [status, pathname, router]);

  if (status !== "signedIn") return <FullPageSpinner />;

  return (
    <>
      <AppHeader />
      <main className="mx-auto w-full max-w-5xl flex-1 px-4 py-8">{children}</main>
    </>
  );
}
