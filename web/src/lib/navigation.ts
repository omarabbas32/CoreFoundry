/** Only same-site paths are allowed as a post-login destination (no "//evil.com" or "https://…"). */
export function safeNext(next: string | null | undefined, fallback = "/projects") {
  return next && next.startsWith("/") && !next.startsWith("//") && !next.startsWith("/\\") ? next : fallback;
}
