# CoreFoundry

Design a database schema in the browser, preview the exact SQL, and apply it
to real MySQL tables safely. A portfolio project focused on dynamic schema
management, safe SQL generation, and clean backend architecture.

> Status: **M1 done — auth, projects, members, and the dashboard**. Next: M2 table designer. See [the phases](docs/phases/README.md).

## Stack
- **API:** ASP.NET Core (.NET 10), Clean Architecture (Api / Application / Domain / Infrastructure)
- **Data:** MySQL 8.4+ (developed on 9.2). EF Core for CoreFoundry's own metadata,
  MySqlConnector + Dapper for the tables users design
- **Web:** Next.js (App Router, TypeScript, Tailwind)

## Run locally

### 1. MySQL (one time)
Needs a local MySQL 8.4+ server. Create the metadata database and the two
least-privilege accounts. Choose your own passwords:

```bash
mysql -u root -p -e "SET @meta_pwd='<meta-password>'; SET @engine_pwd='<engine-password>'; SOURCE db/setup-local.sql;"
```

| Account | Can access |
|---|---|
| `cf_meta` | only the `corefoundry` metadata database |
| `cf_engine` | only `cf_p_*` project databases (never `corefoundry`) |

### 2. API
Secrets are kept in .NET user-secrets, outside the repo:

```bash
cd src/CoreFoundry.Api
dotnet user-secrets set "ConnectionStrings:Metadata" "Server=127.0.0.1;Port=3306;Database=corefoundry;User=cf_meta;Password=<meta-password>"
dotnet user-secrets set "ConnectionStrings:Engine"   "Server=127.0.0.1;Port=3306;User=cf_engine;Password=<engine-password>"
dotnet run --launch-profile http
```

- `GET http://localhost:5172/health/live`: the API process is up
- `GET http://localhost:5172/health`: both MySQL accounts can connect

### 3. Web
```bash
cd web
cp .env.example .env.local   # API_ORIGIN, defaults to http://localhost:5172
npm install
npm run dev
```
The browser only talks to the web app: `next.config.ts` proxies `/api/*` to the API, so the
refresh cookie and CORS behave like the single-origin production setup.

Pages: sign in / register, your projects (status, role, retry failed database creation, create),
and a project page with details, members (add, change role, remove, leave, transfer ownership),
rename, and delete (confirmed by typing the project name). Controls your role can't use are hidden;
the API enforces the same rules.
The table designer (`/projects/<id>/tables`) edits the draft schema: tables, columns with their
type parameters and defaults, drag-to-reorder, delete with undo. Nothing is sent to the project's
database yet; applying drafts comes with the schema engine (M3).
Open http://localhost:3100 (port 3000 is avoided: it is often taken by other local services).

### Tests
```bash
dotnet test
```
Auth and other database tests use a separate local database, `corefoundry_test`, which is
dropped and re-created on every run (your `corefoundry` dev data is never touched). One-time setup:

```bash
mysql -u root -p < db/setup-test.sql
```
The tests reuse the API's `ConnectionStrings:Metadata` and `Engine` user-secrets, with the metadata
database swapped to `corefoundry_test`. Test projects get ids from 1,000,000, so their `cf_p_<id>`
databases never collide with dev ones; leftovers are dropped at the start of each run. Without it (e.g. in CI) those tests are skipped.

## API so far
| Method | Route | Notes |
|---|---|---|
| POST | `/api/auth/register` | `{ email, password }` → 201 + access token; refresh token in `cf_refresh` cookie |
| POST | `/api/auth/login` | same response; rate-limited per IP |
| POST | `/api/auth/refresh` | cookie → new access token + rotated cookie |
| POST | `/api/auth/logout` | revokes the cookie's token → 204 |
| GET | `/api/auth/me` | Bearer token → `{ id, email }` |
| GET | `/api/projects` | projects I'm a member of, with my role |
| POST | `/api/projects` | `{ name }` → 201; creates the `cf_p_<id>` database; caller becomes Owner |
| GET | `/api/projects/{id}` | Developer+; non-members get 404 |
| PATCH | `/api/projects/{id}` | `{ name }`; Admin+ |
| DELETE | `/api/projects/{id}` | Owner; drops the `cf_p_<id>` database |
| POST | `/api/projects/{id}/retry-provisioning` | Owner; for projects whose database creation failed |
| GET | `/api/projects/{id}/members` | Developer+; Owner first, then Admins, then Developers |
| POST | `/api/projects/{id}/members` | `{ email, role }` Admin+; existing accounts only; role Admin or Developer |
| PUT | `/api/projects/{id}/members/{userId}` | `{ role }` Admin+; Admin ↔ Developer, never the Owner |
| DELETE | `/api/projects/{id}/members/{userId}` | Admin+ for others; any member may remove themselves |
| POST | `/api/projects/{id}/transfer-ownership` | `{ userId }` Owner; the old Owner becomes Admin |
| GET | `/api/projects/{id}/tables` | Developer+; draft tables with `state` (`New`, `Applied`, `PendingDrop`) and column count |
| GET | `/api/projects/{id}/tables/{tableId}` | Developer+; the table with its columns and `version` |
| POST | `/api/projects/{id}/tables` | `{ name, columns? }` → 201; errors keyed like `columns[2].length` |
| PUT | `/api/projects/{id}/tables/{tableId}` | `{ version, name }` rename |
| DELETE | `/api/projects/{id}/tables/{tableId}?version=` | never applied → 204 (deleted); applied → 200, marked `PendingDrop` |
| POST | `/api/projects/{id}/tables/{tableId}/restore` | `{ version }` undoes a pending drop |
| POST | `/api/projects/{id}/tables/{tableId}/columns` | `{ version, name, dataType, length, precision, scale, isNullable, isUnique, defaultValue }` |
| PUT | `/api/projects/{id}/tables/{tableId}/columns/{columnId}` | same body; update |
| DELETE | `/api/projects/{id}/tables/{tableId}/columns/{columnId}?version=` | never applied → removed; applied → `PendingDrop` |
| POST | `/api/projects/{id}/tables/{tableId}/columns/{columnId}/restore` | `{ version }` |
| PUT | `/api/projects/{id}/tables/{tableId}/columns/order` | `{ version, columnIds }`, every column exactly once |

Every table change carries the `version` the client last saw and returns the whole table with
its new version; a stale version gets **409**.

**Schema limits:** at most 50 tables per project and 100 columns per table. Names are
`[a-z][a-z0-9_]`, at most 64 characters, lower-cased, not a MySQL reserved word, not `id` and not
starting with `cf_`. A table's row must fit MySQL's 65,535-byte limit (computed exactly, including
the `id` column); Text/Json columns can't be unique or have a default, and a unique Varchar is at
most 768 characters.

After pulling new migrations: `dotnet ef database update --project src/CoreFoundry.Infrastructure --startup-project src/CoreFoundry.Api`

## Docs
- [Plan](intial-plan.md)
- [Progress](docs/PROGRESS.md): what's done, how it's verified, and what's next
- [Phases](docs/phases/README.md)
- [Data model](docs/corefoundry-erd.html) · [Backend flows](docs/corefoundry-flows.html) (open in a browser)
