# CoreFoundry — Progress

_Last updated: 2026-09-24 · branch `m2-table-designer`_

| Phase | Status | Summary |
|---|---|---|
| [M0 — Setup](phases/phase-0-setup.md) | ✅ Done (PR #1) | Solution skeleton, local MySQL accounts, health checks, web app, CI |
| [M1 — Auth, projects, members](phases/phase-1-auth-projects.md) | ✅ Done (PR #2 + `m1-models`) | Data model, auth, projects with real databases, members, dashboard |
| [M2 — Table designer](phases/phase-2-table-designer.md) | ✅ Done (`m2-table-designer`) | Draft tables and columns with full validation, designer UI |
| [M2.5 — Relations + diagram](phases/phase-2b-relations.md) | ✅ Done (`m2-table-designer`) | Column references (foreign keys) with on-delete rules, schema diagram |
| [M3 — Schema engine](phases/phase-3-schema-engine.md) ⭐ | ⏭ Next | Plan / apply / history / drift |
| [M4 — Data API](phases/phase-4-data-api.md) | Planned | Row CRUD on generated tables |
| [M5 — Portfolio polish](phases/phase-5-polish.md) | Planned | One-command run, README, demo, deploy |

**Tests:** 392 .NET tests pass (317 unit, 75 integration, of which 54 run against a real MySQL database; none skipped),
plus headless browser runs of the dashboard (15 checks, M1), the table designer (29 checks, M2) and
relations + diagram (15 checks, M2.5).
`npm run lint` and `npm run build` are clean.

---

## M0 — Setup

- .NET 10 solution with Clean Architecture layers (`Api`, `Application`, `Domain`, `Infrastructure`) and
  architecture tests that fail the build if Domain/Application reference EF Core, MySqlConnector or Dapper.
- Central package management, warnings as errors, `latest-recommended` analyzers.
- Local MySQL (no Docker): `db/setup-local.sql` creates the `corefoundry` database and two least-privilege
  accounts: `cf_meta` (only `corefoundry`) and `cf_engine` (only `cf_p_*` databases).
- Secrets in .NET user-secrets (`corefoundry-api-dev`), never in the repo.
- `/health/live` (process up) and `/health` (both MySQL accounts connect).
- GitHub Actions: API build + tests, web lint + build.

## M1 — Auth, projects, members

### Data model
All 7 metadata tables from the [data model](corefoundry-erd.html), created by EF Core migrations
(`InitialMetadata`, `RefreshTokenRevokedAtConcurrency`). Rules such as "exactly one Owner" and allowed
project status changes are enforced inside the entities.

### API
| Area | Endpoints | Highlights |
|---|---|---|
| Auth | `POST /api/auth/register · login · refresh · logout`, `GET /api/auth/me` | 15-min JWT; refresh token only in an HttpOnly/Secure/SameSite=Strict cookie and stored as a SHA-256 hash; rotation on every refresh; replaying a used token revokes its whole chain; same 401 for unknown email and wrong password; rate-limited per client IP |
| Projects | `GET/POST /api/projects`, `GET/PATCH/DELETE /api/projects/{id}`, `POST …/retry-provisioning` | Creating a project creates its `cf_p_<id>` MySQL database (as `cf_engine`); Provisioning/Failed/Deleting states plus startup recovery handle crashes; slugs get `-2`, `-3` on collisions |
| Members | `GET/POST /api/projects/{id}/members`, `PUT/DELETE …/members/{userId}`, `POST …/transfer-ownership` | Add existing accounts as Admin/Developer; Admins manage others; anyone may leave; ownership only moves by transfer |
| Authorization | policies `Project.Developer/Admin/Owner` | Role read from the database on every request (not in the JWT), so removal takes effect immediately; non-members get **404**, too-low roles **403** |

### Dashboard (Next.js 16)
- Sign in / register, projects list, project page with members, rename and delete.
- Access token kept in memory only; the session is restored from the refresh cookie on reload.
- `/api/*` is proxied to the API by `next.config.ts`, so the browser uses a single origin.
- Only the controls the caller's role allows are shown; the API enforces the same rules.

## M2 — Table designer

Nothing in M2 touches a `cf_p_*` database: the designer edits draft metadata only.

### Domain rules (pure code, reused by M3)
| Rule | What it enforces |
|---|---|
| `IdentifierRules` | `[a-z][a-z0-9_]`, max 64, lower-cased (so `Books` = `books`), no MySQL reserved word (list generated from `INFORMATION_SCHEMA.KEYWORDS` by `db/reserved-words.sql`), no `id`, no `cf_` prefix |
| `ColumnDefinitionRules` | Length only for Varchar (1–4000), precision/scale only for Decimal (1–65, 0–30, s ≤ p), UNIQUE not on Text/Json or Varchar > 768 |
| Row size | MySQL's exact row-size formula for the types M3 will create, checked against MySQL at the 65,535-byte boundary for every type |
| `ColumnDefault` | Defaults parsed into typed values (`IntegerValue`, `DecimalValue`, `CurrentTimestamp`, `GeneratedUuid` …); the canonical text is stored, so M3 renders SQL from the parsed value |
| `ProjectTable` aggregate | Add/update/delete/restore/reorder columns; names unique including pending drops; 100 columns per table; `Version` bumped on every change |

### API
Eleven endpoints under `/api/projects/{id}/tables` (Developer+). Every lookup is scoped by project id,
so another project's table or column is a 404. Writes carry the table's `version`; a stale one, or losing
a race at save, is a 409. Validation errors are `ValidationProblemDetails` keyed by field (`columns[2].length`).
Deleting a never-applied table or column removes it; an applied one is marked `PendingDrop` and can be restored.

### Designer UI
- Tables list with state badges and a "New table" dialog; designer page with the read-only `id` row,
  the columns grid, add/edit dialog (type parameters shown only where they apply), rename/delete table.
- Drag-to-reorder with @dnd-kit (mouse, touch, keyboard); the new order is saved in one request.
- Pending drops are struck through with "Undo delete"; a hard-deleted new column gets an "Undo" notice.
- zod rules mirror the Domain for as-you-type errors; server errors land on the same fields.
- A 409 shows "someone else changed this table" with a Reload button.
- Row-size meter and the "Draft changes aren't applied yet" banner.

## M2.5 — Relations and schema diagram

Added after M2 at the user's request (D25 replaces D6, which had left foreign keys on the roadmap),
before M3 so the schema engine is designed with constraints from the start.

- **Model:** `ProjectColumns.ReferencesTableId` + `OnDelete` (`Restrict`/`Cascade`/`SetNull`), migration `AddColumnReferences`.
- **Rules:** a reference column is BigInt with no default; SetNull needs a nullable column; the target is in the same
  project and not pending drop (self-references allowed); a referenced table can't be deleted, and the error names
  the referencing columns; restoring can't bring back a reference to a table pending drop (`ReferenceRules`).
- **API:** reference fields on column requests/responses (plus `referencesTableName`), `GET /api/projects/{id}/schema`.
- **UI:** "References" and "On delete" pickers in the column dialog (type locked to BigInt), `BigInt → authors` in the
  grid, and a React Flow **diagram** page with automatic left-to-right layout, labelled edges, pan/zoom/drag and
  links into the designer.
- **Fix found on the way:** concurrent column inserts on one table could deadlock in MySQL (each insert holds a
  shared lock on the parent row, then needs to update its `Version`); a deadlock on save is now a 409, like any lost race.

## How it's verified

| Layer | What runs |
|---|---|
| Domain / Application | Unit tests with in-memory fakes (entity rules, identifier/column/default/row-size rules, `AuthService`, `ProjectService`, `MemberService`, `TableService`) |
| API + MySQL | Integration tests against a local `corefoundry_test` database, recreated each run; they create and drop real `cf_p_*` databases. Table tests simulate "applied" by setting `AppliedName` in the database and assert the project database stays empty |
| Dashboard | Headless Chrome scripts (outside the repo). M1: register → create → add member → promote → transfer → delete → sign out with two users. M2: design Bookshop's `authors` and `books` (9 types), invalid names and defaults, duplicate name from the API, keyboard and mouse reorder persisted across reload, delete + undo, two-tab conflict → 409 banner, delete a draft table. M2.5: `books.author_id → authors` (cascade), a self-reference, SetNull on NOT NULL rejected, deleting `authors` refused naming `books.author_id`, diagram with 3 tables and 2 labelled edges, drag, open a table |

Integration tests that need MySQL skip themselves when no connection string is configured (e.g. in CI).

## Changes from the original plan

All are recorded in the [plan's decisions log](../intial-plan.md) (D9–D25).

| Change | Why |
|---|---|
| EF provider is Oracle `MySql.EntityFrameworkCore` (D9, D15) | Pomelo has no EF Core 10 release |
| Local MySQL 9.2 instead of Docker; target 8.4+ (D10) | Uses the server already installed; containers come later |
| `Projects.DatabaseName` computed from the Id (D13) | The Id only exists after insert, and a derived name can never drift from it |
| `SchemaMigrations.StatementCount` added (D14) | The journal can refuse "Applied" before every statement ran |
| Web dev server on port **3100** (D16) | A local VPN service holds `127.0.0.1:3000` |
| `/api` proxied by Next.js; API trusts `X-Forwarded-For` from loopback only (D17) | Single origin for the cookie; per-client rate limiting still works behind the proxy |
| Test projects use ids ≥ 1,000,000 | Test `cf_p_*` databases share the MySQL server with dev ones and must never collide |
| Identifier regex allows 64 characters, not 63 (D18) | The planned `{0,62}` contradicted MySQL's limit and the 64/65-character tests |
| Exact row-size calculation and UNIQUE limits (D19) | "Sum of Varchar × 4" let through tables MySQL rejects |
| Table concurrency via a `Version` counter (D20) | MySQL has no rowversion; one version per table covers column edits too |
| Table pending drop doesn't flag its columns (D21) | Restoring the table can't resurrect separately deleted columns |
| Undo for hard-deleted new columns (D22) | Before M3 every delete is a hard delete; the Definition of done still needs undo |
| Duplicate-key errors → 409 (D24) | Name races on unique indexes were 500s |
| Foreign keys in scope, added as M2.5 with a schema diagram (D25, replaces D6) | Requested by the user; cheaper to design M3's differ with constraint ordering from the start |
| Deadlocks on save → 409 | Found by the concurrency test once columns had a second FK to `ProjectTables` |

## Known gaps / follow-ups

- **Expired refresh tokens are never deleted.** They pile up, one per login and refresh. Cleanup is planned for M5 hardening.
- **Two users creating a project with the same slug at the same moment** now get a 409 instead of a 500 (D24); an automatic retry with the next suffix would be nicer.
- **The web keeps a copy of the reserved-word list** (`web/src/lib/mysql-reserved-words.ts`) for as-you-type hints. It must be regenerated with the Domain's list; the API's list is the one enforced.
- **The `Changed` state** (applied but edited since) needs the M3 snapshot to compare against; until then tables and columns are `New`, `Applied` or `PendingDrop`.
- **Diagram positions aren't saved**; every visit starts from the automatic layout. A self-reference edge loops behind its table box.
- **Undo of a hard-deleted column re-creates it with a new id.** Harmless before apply; once columns are applied they use `PendingDrop` instead.
- **Integration tests don't run in CI yet.** They need MySQL, and a container setup is deferred.
- **Windows MySQL stores table names in lower case** (`lower_case_table_names=1`). This is harmless unless a dump is moved to Linux.
- **The dev database contains test accounts and projects** (`smoke@…`, `member-smoke@…`, `ui-…@test.dev`, `ui-m2-…@test.dev` and `ui-m25-…@test.dev` with "Bookshop" projects) from manual and UI checks.

## Next: M3 — Schema engine ⭐

Diff the draft (M2, M2.5) against the last applied snapshot, render DDL from the typed definitions and
`ColumnDefault` values, order foreign-key constraints correctly, show the plan, apply it with the
`SchemaMigrations` journal, and detect drift.

## Commits on `m2-table-designer`

| Commit | Change |
|---|---|
| `baec1e8` | Domain rules: identifiers, column definitions, defaults, row size, `ProjectTable` aggregate |
| `8c397c0` | `AddTableDesigner` migration, `TableRepository`, duplicate key → 409, `TableService` |
| `0f897db` | Table designer API endpoints + integration tests |
| `8ca8249` | Table designer UI |
| `784e30a` | M2 docs: progress, decisions D18–D24 |
| `ab01a63` | M2.5 backend: references, rules, migration, `/schema`, deadlock → 409 |
| `329eeb6` | M2.5 UI: references in the designer, schema diagram; docs |

## Commits on `m1-models` since M0

| Commit | Change |
|---|---|
| `257402f` | Domain entities with invariants + unit tests |
| `8514ea9` | EF Core model + `InitialMetadata` migration |
| `9fd136e` | Auth endpoints (merged in PR #2) |
| `5e0c65c` | Projects API, database provisioning, project authorization |
| `62679de` | Members API |
| `4af49d1` | Next.js dashboard |
