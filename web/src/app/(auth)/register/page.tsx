"use client";

import { z } from "zod";
import { useAuth } from "@/lib/auth";
import { AuthForm } from "../auth-form";

// Mirrors the API's rules (AuthService): 10–128 characters, no composition rules.
const schema = z
  .object({
    email: z.email("Enter a valid email address."),
    password: z
      .string()
      .min(10, "Use at least 10 characters.")
      .max(128, "Use at most 128 characters."),
    confirmPassword: z.string(),
  })
  .refine((values) => values.password === values.confirmPassword, {
    path: ["confirmPassword"],
    message: "Passwords don't match.",
  });

export default function RegisterPage() {
  const { register } = useAuth();

  return (
    <AuthForm
      title="Create your account"
      schema={schema}
      fields={[
        { name: "email", label: "Email", type: "email", autoComplete: "email" },
        { name: "password", label: "Password", type: "password", autoComplete: "new-password" },
        { name: "confirmPassword", label: "Confirm password", type: "password", autoComplete: "new-password" },
      ]}
      submitLabel="Create account"
      onSubmit={({ email, password }) => register(email, password)}
      footer={{ text: "Already have an account?", linkLabel: "Sign in", href: "/login" }}
    />
  );
}
