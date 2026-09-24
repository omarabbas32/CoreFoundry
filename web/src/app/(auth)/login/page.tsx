"use client";

import { z } from "zod";
import { useAuth } from "@/lib/auth";
import { AuthForm } from "../auth-form";

const schema = z.object({
  email: z.email("Enter a valid email address."),
  password: z.string().min(1, "Enter your password."),
});

export default function LoginPage() {
  const { login } = useAuth();

  return (
    <AuthForm
      title="Sign in"
      schema={schema}
      fields={[
        { name: "email", label: "Email", type: "email", autoComplete: "email" },
        { name: "password", label: "Password", type: "password", autoComplete: "current-password" },
      ]}
      submitLabel="Sign in"
      // The (auth) layout redirects once signed in.
      onSubmit={({ email, password }) => login(email, password)}
      footer={{ text: "No account yet?", linkLabel: "Create one", href: "/register" }}
    />
  );
}
