"use client";

import { useEffect, useRef, useState, type ReactNode } from "react";
import { FoundryPour } from "@/components/foundry-scene";
import { Button } from "@/components/ui";

export const textareaClass =
  "w-full resize-none rounded-md border border-border bg-surface px-3 py-2 text-sm text-foreground placeholder:text-muted focus:border-accent focus:outline-none focus:ring-2 focus:ring-accent-soft";

/** One chat turn with who said it: the AI on the left, the user on the right. */
export function Bubble({
  from,
  label,
  children,
  pending = false,
}: {
  from: "ai" | "you";
  /** A small caption above the text, e.g. "Proposal" or "Change request". */
  label?: string;
  children: ReactNode;
  /** Sent, not yet saved: shown slightly faded. */
  pending?: boolean;
}) {
  const you = from === "you";
  return (
    <li className={`flex items-end gap-2 ${you ? "flex-row-reverse" : ""}`}>
      <span
        aria-hidden
        className={`grid size-7 shrink-0 place-items-center rounded-full text-[11px] font-semibold ${
          you ? "bg-accent-solid text-on-accent" : "bg-surface-muted text-muted ring-1 ring-border"
        }`}
      >
        {you ? "You" : "AI"}
      </span>
      <div
        className={`max-w-[85%] rounded-2xl px-3.5 py-2 text-sm whitespace-pre-wrap ${
          you ? "rounded-br-sm bg-accent-solid text-on-accent" : "rounded-bl-sm border border-border bg-surface-muted"
        } ${pending ? "opacity-70" : ""}`}
      >
        <span className="sr-only">{you ? "You: " : "AI: "}</span>
        {label && <span className={`block text-xs ${you ? "opacity-80" : "text-muted"}`}>{label}</span>}
        {children}
      </div>
    </li>
  );
}

/** The AI's typing bubble, with how long it has been thinking (answers can take a while). */
export function TypingBubble() {
  const [seconds, setSeconds] = useState(0);
  useEffect(() => {
    const timer = setInterval(() => setSeconds((value) => value + 1), 1000);
    return () => clearInterval(timer);
  }, []);

  return (
    <li className="flex items-end gap-2" role="status">
      <span aria-hidden className="grid size-7 shrink-0 place-items-center rounded-full bg-surface-muted text-[11px] font-semibold text-muted ring-1 ring-border">
        AI
      </span>
      <div className="flex items-center gap-3 rounded-2xl rounded-bl-sm border border-border bg-surface-muted px-3.5 py-2.5 text-sm text-muted">
        <FoundryPour size={22} />
        <span>
          Thinking{seconds >= 3 ? ` · ${seconds}s` : "…"}
          {seconds >= 20 && <span className="block text-xs">Designing a schema can take up to a minute.</span>}
        </span>
      </div>
    </li>
  );
}

/**
 * The message box: Enter sends, Shift+Enter adds a line. Grows with the text, and takes focus when it appears
 * (a new question arrived), so the user can just type.
 */
export function Composer({
  id,
  label,
  placeholder,
  sending,
  onSend,
  onBack,
  initialText = "",
  maxLength = 4000,
}: {
  id: string;
  label: string;
  placeholder: string;
  sending: boolean;
  /** Resolves once saved: the box is cleared then, and keeps the text if sending failed. */
  onSend: (text: string) => Promise<unknown>;
  /** Shown as a "Back" button when set (leaving the change request). */
  onBack?: () => void;
  /** Pre-filled text, e.g. a picked example; the user can edit it before sending. */
  initialText?: string;
  maxLength?: number;
}) {
  const [text, setText] = useState(initialText);
  const ref = useRef<HTMLTextAreaElement>(null);

  useEffect(() => {
    ref.current?.focus({ preventScroll: true });
  }, []);

  useEffect(() => {
    const box = ref.current;
    if (!box) return;
    box.style.height = "auto";
    box.style.height = `${Math.min(box.scrollHeight, 200)}px`;
  }, [text]);

  function send() {
    const trimmed = text.trim();
    if (!trimmed || sending) return;
    onSend(trimmed).then(
      () => setText(""),
      () => undefined, // the error is shown by the conversation
    );
  }

  return (
    <form
      className="grid gap-1.5"
      onSubmit={(event) => {
        event.preventDefault();
        send();
      }}
    >
      <label htmlFor={id} className="sr-only">
        {label}
      </label>
      <div className="flex items-end gap-2">
        <textarea
          id={id}
          ref={ref}
          rows={1}
          maxLength={maxLength}
          className={textareaClass}
          placeholder={placeholder}
          value={text}
          disabled={sending}
          onChange={(event) => setText(event.target.value)}
          onKeyDown={(event) => {
            if (event.key === "Enter" && !event.shiftKey && !event.nativeEvent.isComposing) {
              event.preventDefault();
              send();
            }
          }}
        />
        {onBack && (
          <Button type="button" variant="ghost" onClick={onBack}>
            Back
          </Button>
        )}
        <Button type="submit" disabled={!text.trim() || sending}>
          Send
        </Button>
      </div>
      <p className="text-xs text-muted">Enter to send · Shift+Enter for a new line</p>
    </form>
  );
}
