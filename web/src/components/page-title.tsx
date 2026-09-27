"use client";

import { useEffect } from "react";

/**
 * Names the browser tab (and the history entry, and what screen readers announce on arrival) for a client page.
 * Sets document.title directly: a <title> element would come after the app's own and be ignored.
 */
export function PageTitle({ title }: { title: string }) {
  useEffect(() => {
    document.title = title;
  }, [title]);
  return null;
}
