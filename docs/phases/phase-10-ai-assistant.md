# M10 — AI schema assistant (built)

**Goal:** designing a good schema is the hardest step for a new user, and templates only cover e-commerce. M10 adds
an assistant powered by **Groq** (an OpenAI-compatible API) that interviews the user **one question at a time**, **saves every question and
answer**, then **proposes schema changes and asks for confirmation** before anything is created.

**Status:** built on `m10-ai-assistant` (off `realtime-per-table`, so a proposal can set realtime).
**Decided by the user (2026-09-26):**
- **Provider (changed the same day):** Groq, not xAI Grok — the user's key is a Groq `gsk_` key. The client speaks
  the OpenAI-compatible `chat/completions` API, so `Ai:BaseUrl` and `Ai:Model` can point at another such provider.
- **Key:** a user may add their own Groq key; without one, the server's default key (the owner's) is used.
- **On confirm:** create **draft tables only**; the user reviews the plan and applies it as usual.
- **Scope:** new projects **and** extending existing schemas.
- **History:** conversations are **saved in the database** and resume where they stopped.

## 1. A conversation

1. **Design with AI** (empty project) or **Extend with AI** (on the tables page) → the user writes a one-line goal.
2. The server sends the model the rules (types, limits, naming, access, realtime), the project's **current draft schema**
   (names, types, references, access; never row data) and the conversation so far.
3. The model answers with **strict structured JSON** (`response_format: json_schema`; a model that refuses that
   format is asked once more in `json_object` mode with the schema in the prompt): either `question` (one question and
   up to 5 suggested answers, shown as buttons; free text always works) or `proposal` (a summary, **new tables** and
   **new columns on existing tables**). It can only **add**: nothing is renamed, dropped or changed.
4. After **8 questions** the server tells the model to propose; a question after that is refused.
5. **Repair loop:** every proposal is dry-run through the real domain rules (`ProposalValidator` builds scratch
   `ProjectTable`s: names, reserved words, types, lengths, references, access, row size, limits, clashes). Problems go
   back to the model as a message, up to **3 calls per turn**; only a valid proposal reaches the user.
6. The user **confirms**, **asks for changes** (feedback → a new question or proposal), or **cancels**.
7. **Confirm** re-validates against the drafts as they are now (someone may have changed them), then writes through
   `DraftSchemaWriter` — the same code templates use, all or nothing — and closes the conversation.

The model's output is data: nothing runs until the user confirms, and then only as draft metadata that passes the
same validation as a designer edit. The system prompt tells the model to treat user messages as answers, not rules.

## 2. Resilience

Each user message is **saved before** the model is called. If the call fails (timeout, provider error, repair loop
exhausted), the answer stays; the session reports `awaitingAssistant` and **Try again** (`POST …/continue`) reruns
the turn. Every change takes the conversation's `version` (409 when stale, e.g. two tabs).

## 3. Keys, cost and limits

| | |
|---|---|
| Default key | `Ai:ApiKey` in user-secrets / `Ai__ApiKey` in the environment. Empty: users must add their own. |
| Own key | `PUT /api/me/ai-key` — encrypted with ASP.NET Core **Data Protection** (key ring in the metadata DB, table `DataProtectionKeys`), shown only as its last 4 characters, never returned or logged. |
| Daily cap | `Ai:DailyCallsPerUser` (60) calls per UTC day **on the default key**, counted atomically in `AssistantUsage`; own keys aren't capped. |
| Rate limit | policy `assistant`: `RateLimiting:AssistantPermitLimit` (20) per `AssistantWindowSeconds` (60) **per user**. The rate limiter now runs after authentication so it can partition by user. |
| Model | `Ai:Model` (default `openai/gpt-oss-120b`), `Ai:BaseUrl` (default `https://api.groq.com/openai/v1/`), `Ai:TimeoutSeconds` (90). |

Provider failures become ProblemDetails: not configured / unavailable → 503, key rejected → 400, rate limited or daily
cap → 429, unusable answer → 502.

## 4. API

`/api/projects/{id}/assistant/sessions` (Developer): `GET` list · `POST {goal}` start (409 if one is open) ·
`GET /{sid}` · `POST /{sid}/answers {version,text}` · `/revise {version,feedback}` · `/continue {version}` ·
`/confirm {version}` · `/cancel {version}`. `/api/me/ai-key` (signed in): `GET`, `PUT {apiKey}`, `DELETE`.

## 5. Data

`AssistantSessions` (goal, status, proposal JSON, question count, version), `AssistantMessages` (sequence, kind
Question/Answer/Proposal/Feedback, text, suggested answers), `AssistantUsage` (user, day, calls), `Users.AiKeyCiphertext`
/ `AiKeyHint`, `DataProtectionKeys`. Migration `AiAssistant`.

## 6. Not in v1

- Renaming, dropping or changing existing columns (the assistant mentions such ideas in its summary instead).
- Streaming answers; the UI waits for the whole reply ("The AI is thinking…").
- Other providers: `IAiChatClient` is the port; `OpenAiCompatibleChatClient` covers Groq and other OpenAI-compatible APIs.

## 7. Verified

Unit: `ProposalValidator`, the session's turn rules, `OpenAiCompatibleChatClient` (request shape, strict schema and
its json_object fallback, key choice, error mapping, timeout). Integration (scripted model in place of the provider): the interview to confirmed drafts, the repair loop
and its retry, provider failures, extending a schema, re-validation at confirm, revisions, one open conversation,
membership, the daily cap and own keys. **Checked by the user against the real Groq API (2026-09-26): it works.**
