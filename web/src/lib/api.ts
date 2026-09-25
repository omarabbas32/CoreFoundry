import type { AuthResponse } from "./types";

/**
 * Browser-side API client. The access token lives only in this module's memory (never in
 * localStorage); the refresh token is an HttpOnly cookie the browser sends to /api/auth/* on its own.
 */

let accessToken: string | null = null;
let refreshInFlight: Promise<AuthResponse | null> | null = null;
let onSessionExpired: (() => void) | null = null;

export function setAccessToken(token: string | null) {
  accessToken = token;
}

/** Called when a request stays unauthorized even after a refresh (e.g. the session was revoked). */
export function setSessionExpiredHandler(handler: (() => void) | null) {
  onSessionExpired = handler;
}

/** An API error with the ProblemDetails fields the UI needs. */
export class ApiError extends Error {
  constructor(
    readonly status: number,
    message: string,
    /** Field name (camelCase) → messages, from ValidationProblemDetails. */
    readonly fieldErrors: Record<string, string[]> = {},
    /** The ProblemDetails type's last segment, e.g. "plan-stale" (empty when absent). */
    readonly problemType: string = "",
    /** The whole ProblemDetails body, for extension members such as apply-failed's statement. */
    readonly problem: Record<string, unknown> = {},
  ) {
    super(message);
    this.name = "ApiError";
  }
}

type ProblemDetails = { type?: string; title?: string; detail?: string; errors?: Record<string, string[]> };

async function toApiError(response: Response): Promise<ApiError> {
  let problem: ProblemDetails = {};
  try {
    problem = (await response.json()) as ProblemDetails;
  } catch {
    // Not JSON (e.g. the proxy couldn't reach the API).
  }

  const fieldErrors: Record<string, string[]> = {};
  for (const [key, messages] of Object.entries(problem.errors ?? {})) {
    // Model-binding keys look like "Email" or "$.role"; our own ones are already "email".
    const field = key.replace(/^\$\./, "");
    fieldErrors[field.charAt(0).toLowerCase() + field.slice(1)] = messages;
  }

  const message =
    problem.detail ??
    problem.title ??
    (response.status === 502 || response.status === 504
      ? "Can't reach the API. Is it running?"
      : `Request failed (${response.status}).`);
  const problemType = problem.type?.startsWith("https://corefoundry.dev/problems/") ? problem.type.split("/").pop()! : "";
  return new ApiError(response.status, message, fieldErrors, problemType, problem as Record<string, unknown>);
}

/**
 * Exchanges the refresh cookie for a new access token. Concurrent callers share one request:
 * the server rotates the cookie on every refresh, so two parallel refreshes would make one fail.
 */
export function refreshSession(): Promise<AuthResponse | null> {
  refreshInFlight ??= (async () => {
    try {
      const response = await fetch("/api/auth/refresh", { method: "POST" });
      if (!response.ok) {
        setAccessToken(null);
        return null;
      }
      const session = (await response.json()) as AuthResponse;
      setAccessToken(session.accessToken);
      return session;
    } finally {
      refreshInFlight = null;
    }
  })();
  return refreshInFlight;
}

type RequestOptions = { method?: "GET" | "POST" | "PUT" | "PATCH" | "DELETE"; body?: unknown };

async function send(path: string, { method = "GET", body }: RequestOptions) {
  const headers: Record<string, string> = {};
  if (accessToken) headers.Authorization = `Bearer ${accessToken}`;
  if (body !== undefined) headers["Content-Type"] = "application/json";
  return fetch(path, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) });
}

/** Sends with the access token; on 401 refreshes the session once and retries. Throws {@link ApiError} on failure. */
async function authorized(path: string, options: RequestOptions): Promise<Response> {
  let response = await send(path, options);

  if (response.status === 401 && !path.startsWith("/api/auth/")) {
    const session = await refreshSession();
    if (session) {
      response = await send(path, options);
    }
    if (response.status === 401) {
      onSessionExpired?.();
    }
  }

  if (!response.ok) throw await toApiError(response);
  return response;
}

/** Calls the API and reads its JSON. Throws {@link ApiError} on failure. */
export async function api<T>(path: string, options: RequestOptions = {}): Promise<T> {
  const response = await authorized(path, options);
  if (response.status === 204) return undefined as T;
  return (await response.json()) as T;
}

/** Downloads a file (e.g. a zip) with the same auth as {@link api}; the name comes from Content-Disposition. */
export async function download(path: string, fallbackName: string): Promise<{ blob: Blob; fileName: string }> {
  const response = await authorized(path, {});
  const disposition = response.headers.get("Content-Disposition") ?? "";
  const encoded = /filename\*=UTF-8''([^;]+)/i.exec(disposition)?.[1];
  const plain = /filename="?([^";]+)"?/i.exec(disposition)?.[1];
  return { blob: await response.blob(), fileName: encoded ? decodeURIComponent(encoded) : (plain ?? fallbackName) };
}
