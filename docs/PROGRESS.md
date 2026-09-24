# CoreFoundry — Progress

_Last updated: 2026-09-24 · branch `m1-models`_

| Phase | Status | Summary |
|---|---|---|
| [M0 — Setup](phases/phase-0-setup.md) | ✅ Done (PR #1) | Solution skeleton, local MySQL accounts, health checks, web app, CI |
| [M1 — Auth, projects, members](phases/phase-1-auth-projects.md) | ✅ Done (PR #2 + `m1-models`) | Data model, auth, projects with real databases, members, dashboard |
| [M2 — Table designer](phases/phase-2-table-designer.md) | ⏭ Next | Draft tables and columns with full validation |
| [M3 — Schema engine](phases/phase-3-schema-engine.md) ⭐ | Planned | Plan / apply / history / drift |
| [M4 — Data API](phases/phase-4-data-api.md) | Planned | Row CRUD on generated tables |
| [M5 — Portfolio polish](phases/phase-5-polish.md) | Planned | One-command run, README, demo, deploy |

**Tests:** 162 .NET tests pass (112 unit, 50 integration, including 40 against a real MySQL database, none skipped),
plus a 15-check headless browser run of the dashboard. `npm run lint` and `npm run build` are clean.

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

## How it's verified

| Layer | What runs |
|---|---|
| Domain / Application | Unit tests with in-memory fakes (entity rules, `AuthService`, `ProjectService`, `MemberService`) |
| API + MySQL | Integration tests against a local `corefoundry_test` database, recreated each run; they create and drop real `cf_p_*` databases |
| Dashboard | Headless Chrome script (outside the repo) driving register → create → add member → promote → transfer → delete → sign out with two users |

Integration tests that need MySQL skip themselves when no connection string is configured (e.g. in CI).

## Changes from the original plan

All are recorded in the [plan's decisions log](../intial-plan.md) (D9–D17).

| Change | Why |
|---|---|
| EF provider is Oracle `MySql.EntityFrameworkCore` (D9, D15) | Pomelo has no EF Core 10 release |
| Local MySQL 9.2 instead of Docker; target 8.4+ (D10) | Uses the server already installed; containers come later |
| `Projects.DatabaseName` computed from the Id (D13) | The Id only exists after insert, and a derived name can never drift from it |
| `SchemaMigrations.StatementCount` added (D14) | The journal can refuse "Applied" before every statement ran |
| Web dev server on port **3100** (D16) | A local VPN service holds `127.0.0.1:3000` |
| `/api` proxied by Next.js; API trusts `X-Forwarded-For` from loopback only (D17) | Single origin for the cookie; per-client rate limiting still works behind the proxy |
| Test projects use ids ≥ 1,000,000 | Test `cf_p_*` databases share the MySQL server with dev ones and must never collide |

## Known gaps / follow-ups

- **Expired refresh tokens are never deleted.** They pile up, one per login and refresh. Cleanup is planned for M5 hardening.
- **Two users creating a project with the same slug at the same moment** can hit the unique index and get a 500. Rare; a retry on conflict would fix it.
- **Integration tests don't run in CI yet.** They need MySQL, and a container setup is deferred.
- **Windows MySQL stores table names in lower case** (`lower_case_table_names=1`). This is harmless unless a dump is moved to Linux.
- **The dev database contains test accounts** (`smoke@…`, `member-smoke@…`, `ui-…@test.dev`) from manual and UI checks.

## Next: M2 — Table designer

Draft `ProjectTables` / `ProjectColumns` editing with the identifier and data-type rules in Domain
(regex, MySQL reserved words, per-type length/precision/default), a `PendingDrop` state for deletes of
applied objects, and the designer UI. Nothing touches `cf_p_*` databases until M3.

## Commits on `m1-models` since M0

| Commit | Change |
|---|---|
| `257402f` | Domain entities with invariants + unit tests |
| `8514ea9` | EF Core model + `InitialMetadata` migration |
| `9fd136e` | Auth endpoints (merged in PR #2) |
| `5e0c65c` | Projects API, database provisioning, project authorization |
| `62679de` | Members API |
| `4af49d1` | Next.js dashboard |
