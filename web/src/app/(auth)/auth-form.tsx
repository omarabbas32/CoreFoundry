"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import Link from "next/link";
import { useState } from "react";
import { useForm, type FieldValues, type Path } from "react-hook-form";
import type { z } from "zod";
import { Alert, Button, Card, Field, Input } from "@/components/ui";
import { ApiError } from "@/lib/api";

type FieldSpec<T> = { name: Path<T>; label: string; type: "email" | "password"; autoComplete: string };

/**
 * Shared sign-in / register form: client-side zod validation, then server errors mapped back onto
 * the matching field (e.g. "email already taken") or shown above the form.
 */
export function AuthForm<T extends FieldValues>({
  title,
  schema,
  fields,
  submitLabel,
  onSubmit,
  footer,
}: {
  title: string;
  schema: z.ZodType<T, T>;
  fields: FieldSpec<T>[];
  submitLabel: string;
  onSubmit: (values: T) => Promise<void>;
  footer: { text: string; linkLabel: string; href: string };
}) {
  const [formError, setFormError] = useState<string | null>(null);
  const {
    register,
    handleSubmit,
    setError,
    formState: { errors, isSubmitting },
  } = useForm<T>({ resolver: zodResolver(schema) });

  const submit = handleSubmit(async (values) => {
    setFormError(null);
    try {
      await onSubmit(values);
    } catch (error) {
      if (error instanceof ApiError) {
        const matched = fields.filter((field) => error.fieldErrors[field.name]);
        matched.forEach((field) => setError(field.name, { message: error.fieldErrors[field.name][0] }));
        if (matched.length === 0) setFormError(error.message);
      } else {
        setFormError("Something went wrong. Try again.");
      }
    }
  });

  return (
    <Card className="grid gap-5 p-6">
      <h1 className="text-xl font-semibold">{title}</h1>
      {formError && <Alert>{formError}</Alert>}
      <form onSubmit={submit} noValidate className="grid gap-4">
        {fields.map((field) => {
          const error = errors[field.name]?.message as string | undefined;
          return (
            <Field key={field.name} label={field.label} htmlFor={field.name} error={error}>
              <Input
                id={field.name}
                type={field.type}
                autoComplete={field.autoComplete}
                aria-invalid={Boolean(error)}
                aria-describedby={error ? `${field.name}-error` : undefined}
                {...register(field.name)}
              />
            </Field>
          );
        })}
        <Button type="submit" loading={isSubmitting} className="mt-1 w-full">
          {submitLabel}
        </Button>
      </form>
      <p className="text-center text-sm text-muted">
        {footer.text}{" "}
        <Link href={footer.href} className="font-medium text-accent hover:underline">
          {footer.linkLabel}
        </Link>
      </p>
    </Card>
  );
}
