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
  ) {
    super(message);
    this.name = "ApiError";
  }
}

type ProblemDetails = { title?: string; detail?: string; errors?: Record<string, string[]> };

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
  return new ApiError(response.status, message, fieldErrors);
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

/** Calls the API; on 401 refreshes the session once and retries. Throws {@link ApiError} on failure. */
export async function api<T>(path: string, options: RequestOptions = {}): Promise<T> {
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
  if (response.status === 204) return undefined as T;
  return (await response.json()) as T;
}
