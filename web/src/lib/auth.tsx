"use client";

import { useQueryClient } from "@tanstack/react-query";
import { createContext, useCallback, useContext, useEffect, useMemo, useState } from "react";
import { api, refreshSession, setAccessToken, setSessionExpiredHandler } from "./api";
import type { AuthResponse, User } from "./types";

type AuthState =
  | { status: "loading"; user: null }
  | { status: "signedIn"; user: User }
  | { status: "signedOut"; user: null };

type AuthContextValue = AuthState & {
  login: (email: string, password: string) => Promise<void>;
  register: (email: string, password: string) => Promise<void>;
  logout: () => Promise<void>;
};

const AuthContext = createContext<AuthContextValue | null>(null);

export function AuthProvider({ children }: { children: React.ReactNode }) {
  const queryClient = useQueryClient();
  const [state, setState] = useState<AuthState>({ status: "loading", user: null });

  const signedOut = useCallback(() => {
    setAccessToken(null);
    queryClient.clear();
    setState({ status: "signedOut", user: null });
  }, [queryClient]);

  const signedIn = useCallback((session: AuthResponse) => {
    setAccessToken(session.accessToken);
    setState({ status: "signedIn", user: session.user });
  }, []);

  // On load, the access token is gone (memory only); the refresh cookie restores the session.
  useEffect(() => {
    let active = true;
    refreshSession().then((session) => {
      if (!active) return;
      if (session) signedIn(session);
      else setState({ status: "signedOut", user: null });
    });
    setSessionExpiredHandler(signedOut);
    return () => {
      active = false;
      setSessionExpiredHandler(null);
    };
  }, [signedIn, signedOut]);

  const value = useMemo<AuthContextValue>(
    () => ({
      ...state,
      login: async (email, password) =>
        signedIn(await api<AuthResponse>("/api/auth/login", { method: "POST", body: { email, password } })),
      register: async (email, password) =>
        signedIn(await api<AuthResponse>("/api/auth/register", { method: "POST", body: { email, password } })),
      logout: async () => {
        try {
          await api<void>("/api/auth/logout", { method: "POST" });
        } finally {
          signedOut();
        }
      },
    }),
    [state, signedIn, signedOut],
  );

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth() {
  const context = useContext(AuthContext);
  if (!context) throw new Error("useAuth must be used inside <AuthProvider>");
  return context;
}
