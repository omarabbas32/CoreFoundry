# CoreFoundry

Design a database schema in the browser (by hand, from a template, or with an AI assistant), preview the exact
SQL, apply it to real MySQL tables safely, and export a C# Clean Architecture backend for it. A portfolio project
focused on dynamic schema management, safe SQL generation, and clean backend architecture.

> Status: **M0–M4 and M6–M10 built**: schema designer, schema engine, Data API, code export, templates, access
> rules, realtime in the export, and the AI schema assistant; plus team invitations with notifications and a UI
> pass (project menu, ocean-blue theme, logo). Next: M5 polish. See [the phases](docs/phases/README.md).

## What you can do

- **Projects and teams.** Each project gets its own MySQL database (`cf_p_<id>`). Invite teammates as Admin or
  Developer: they get a notification (the bell in the header) and join when they accept. Roles decide what each
  member can do; the API enforces them.
- **Design the schema** three ways:
  - **Table designer:** tables and typed columns, references between tables (foreign keys with on-delete rules),
    drag to reorder, undo. Adding a column offers common columns (`email`, `created_at`, `price`…) and guesses the
    type from the name. A **diagram** shows the tables and their relations.
  - **Templates:** start from a ready E-commerce schema (8 tables), optionally with sample rows.
  - **AI assistant:** describe your app; it asks a few questions, one at a time, then proposes tables and columns
    (checked against the same rules as the designer). Confirm and they're created as drafts. Each member has their
    own conversations. See [AI assistant](#ai-assistant).
- **Plan & apply.** Draft changes reach the database only through a reviewed plan: a plain summary (with data loss
  called out), each operation, and the exact SQL. Applies are locked, journaled and resumable; changes made to the
  database outside CoreFoundry are detected.
- **Use the data.** Browse, search, sort and edit the rows of applied tables (references show the referenced row's
  name), or call the project's REST Data API.
- **Export a backend.** Download a .NET 10 Clean Architecture solution (Domain, Application, Infrastructure, Api)
  with EF Core, JWT, Swagger UI and Docker. Each table's **access rules** (who may read and write) and **realtime**
  switch (a SignalR hub that pushes changes) are written into it.

## Stack
- **API:** ASP.NET Core (.NET 10), Clean Architecture (Api / Application / Domain / Infrastructure)
- **Data:** MySQL 8.4+ (developed on 9.2). EF Core for CoreFoundry's own metadata,
  MySqlConnector + Dapper for the tables users design
- **Web:** Next.js (App Router, TypeScript, Tailwind)

## Run with Docker (one command)

Needs only Docker. From the repository root:

```bash
cp .env.example .env      # then change every password and the signing key
docker compose up --build
```

In Docker the AI assistant has no shared key: each user adds their own [Groq](https://console.groq.com) key in the
app (**AI key** in the header).

Open http://localhost:3100 (change `WEB_PORT` in `.env` if that port is taken). The first start builds the images
and creates the database: MySQL 8.4, the `corefoundry` metadata database and the two least-privilege accounts
(`docker/mysql/init.sh`), then the API applies its migrations. Data is kept in the `db-data` volume;
`docker compose down -v` removes it.

Only the web app is published: it proxies `/api` to the API container, so the browser sees a single origin and the
API isn't reachable directly. curl works through the same origin: `http://localhost:3100/api/...`.
For a public deployment, put HTTPS in front of the web port (the refresh cookie is `Secure`).

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

`cf_engine` needs `REFERENCES` to create foreign keys. The script grants it; if you set up MySQL before M3, run once as root:

```sql
GRANT REFERENCES ON `cf\_p\_%`.* TO 'cf_engine'@'localhost';
```

### 2. API
Secrets are kept in .NET user-secrets, outside the repo:

```bash
cd src/CoreFoundry.Api
dotnet user-secrets set "ConnectionStrings:Metadata" "Server=127.0.0.1;Port=3306;Database=corefoundry;User=cf_meta;Password=<meta-password>"
dotnet user-secrets set "ConnectionStrings:Engine"   "Server=127.0.0.1;Port=3306;User=cf_engine;Password=<engine-password>"
dotnet user-secrets set "Jwt:SigningKey" "$(openssl rand -base64 48)"   # at least 32 characters; the API refuses to start without it
dotnet user-secrets set "Ai:ApiKey" "<your Groq key, gsk_...>"   # optional: the AI assistant's default key (users can add their own)
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

Open http://localhost:3100 (port 3000 is avoided: it is often taken by other local services).

### Demo data (optional)
```bash
dotnet run --project src/CoreFoundry.Api --launch-profile http -- seed     # locally
docker compose exec api dotnet CoreFoundry.Api.dll seed                    # in Docker, while it runs
```
Adds `demo@corefoundry.dev` (Owner) and `dev@corefoundry.dev` (Developer), both with the password
`corefoundry-demo`, and a **Demo shop** project: the E-commerce template applied, with its sample rows, and one
pending change (a rename, a new column and a dropped one) waiting in Plan & apply. Runs once: if the demo account
exists, it does nothing. For local use only: the password is public.

**Where things are.** Sign in or register, then **Projects** (a card per project; create one, or retry a failed
database). Inside a project, the menu on the left (a strip on phones) has:

| Menu | Page | What it's for |
|---|---|---|
| Overview | `/projects/<id>` | details, what to do next with the schema, export |
| Tables | `/projects/<id>/tables` | the table designer (a table: `/tables/<tableId>`) |
| Design with AI | `/projects/<id>/assistant` | the AI schema assistant |
| Diagram | `/projects/<id>/tables/diagram` | tables and relations (pan, zoom, drag) |
| Plan & apply | `/projects/<id>/schema` | the reviewed plan and its SQL; Admins apply |
| History | `/projects/<id>/schema/history` | every apply with its SQL and any error |
| Data | `/projects/<id>/data` | browse, search and edit rows |
| API & access | `/projects/<id>/api` | try the Data API; access and realtime for the export; download |
| Settings | `/projects/<id>/settings` | members, invitations, rename, delete |

The header has **AI key** (your own Groq key, optional) and the **notifications** bell (invitations to answer).
Controls your role can't use are hidden; the API enforces the same rules.

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

## API
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
| GET | `/api/projects/{id}/invitations` | Developer+; invitations waiting for an answer |
| POST | `/api/projects/{id}/invitations` | `{ email, role }` Admin+ → 201; existing accounts only; role Admin or Developer; 409 if already a member or invited |
| DELETE | `/api/projects/{id}/invitations/{invitationId}` | Admin+; withdraws a pending invitation |
| GET | `/api/me/invitations` | signed in; my pending invitations (the notifications), with project and inviter |
| POST | `/api/me/invitations/{invitationId}/accept` | joins the project with the invited role → the project |
| POST | `/api/me/invitations/{invitationId}/decline` | 204; someone else's invitation is 404 |
| PUT | `/api/projects/{id}/members/{userId}` | `{ role }` Admin+; Admin ↔ Developer, never the Owner |
| DELETE | `/api/projects/{id}/members/{userId}` | Admin+ for others; any member may remove themselves |
| POST | `/api/projects/{id}/transfer-ownership` | `{ userId }` Owner; the old Owner becomes Admin |
| GET | `/api/projects/{id}/tables` | Developer+; draft tables with `state` (`New`, `Applied`, `PendingDrop`) and column count |
| GET | `/api/projects/{id}/tables/{tableId}` | Developer+; the table with its columns and `version` |
| POST | `/api/projects/{id}/tables` | `{ name, columns? }` → 201; errors keyed like `columns[2].length` |
| PUT | `/api/projects/{id}/tables/{tableId}` | `{ version, name }` rename |
| DELETE | `/api/projects/{id}/tables/{tableId}?version=` | never applied → 204 (deleted); applied → 200, marked `PendingDrop` |
| POST | `/api/projects/{id}/tables/{tableId}/restore` | `{ version }` undoes a pending drop |
| PUT | `/api/projects/{id}/tables/{tableId}/access` | `{ version, read, write }`: `Public` / `SignedIn` / `Admin`, write never wider than read (for the export) |
| PUT | `/api/projects/{id}/tables/{tableId}/realtime` | `{ version, enabled }`: whether the exported backend sends realtime events for the table |
| POST | `/api/projects/{id}/tables/{tableId}/columns` | `{ version, name, dataType, length, precision, scale, isNullable, isUnique, defaultValue, referencesTableId?, onDelete? }` |
| PUT | `/api/projects/{id}/tables/{tableId}/columns/{columnId}` | same body; update |
| DELETE | `/api/projects/{id}/tables/{tableId}/columns/{columnId}?version=` | never applied → removed; applied → `PendingDrop` |
| POST | `/api/projects/{id}/tables/{tableId}/columns/{columnId}/restore` | `{ version }` |
| PUT | `/api/projects/{id}/tables/{tableId}/columns/order` | `{ version, columnIds }`, every column exactly once |
| GET | `/api/projects/{id}/schema` | Developer+; every table with its columns and references (the diagram's data) |
| GET | `/api/projects/{id}/schema/plan` | Developer+; `{ planHash, schemaVersion, operations, statements, warnings, unmanagedTables, unmanagedColumns, hasDestructive }`, reads only |
| POST | `/api/projects/{id}/schema/apply` | Admin+; `{ planHash, acknowledgeDestructive }` → 200, or 409 `plan-stale` / 409 `apply-in-progress` / 422 `destructive-not-acknowledged` / 500 `apply-failed` |
| GET | `/api/projects/{id}/schema/migrations?page=&pageSize=` | Developer+; applies, newest first |
| GET | `/api/projects/{id}/schema/migrations/{migrationId}` | Developer+; statements, status, `statementsApplied`, `failedStatement`, error |
| GET | `/api/projects/{id}/schema/drift` | Developer+; changes made to the database outside CoreFoundry since the last apply |
| GET | `/api/projects/{id}/data` | Developer+; the applied tables and their columns (type, nullable, unique, default, references, writable) |
| GET | `/api/projects/{id}/data/{table}?page=&pageSize=&sort=&q=` | Developer+; `{ items, page, pageSize, total }`; `pageSize` 1–100 (default 25), `sort` a column, `-` first for descending; `q` keeps rows whose text columns contain it, or whose id it is |
| GET | `/api/projects/{id}/data/{table}/{rowId}` | Developer+; one row |
| POST | `/api/projects/{id}/data/{table}` | Developer+; JSON object of column values → 201 + the row |
| PUT | `/api/projects/{id}/data/{table}/{rowId}` | Developer+; full replace: columns left out get their default, or NULL |
| DELETE | `/api/projects/{id}/data/{table}/{rowId}` | Developer+; 204; 409 if other rows still reference it (`Restrict`) |
| GET | `/api/templates` | signed in; the ready schemas with their tables |
| POST | `/api/projects/{id}/templates/{key}` | Developer+; `{ withSampleData }` creates the template's draft tables; 409 if the project has tables |
| POST | `/api/projects/{id}/sample-data` | Developer+; inserts the template's sample rows into applied tables that are still empty |
| GET | `/api/projects/{id}/export` | Developer+; zip of a .NET backend for the applied tables; 409 if nothing is applied |
| GET | `/api/projects/{id}/data/{table}/lookup?q=&limit=` | Developer+; `[{ id, label }]` for reference pickers (label = first Varchar column) |
| GET | `/api/projects/{id}/data/{table}/lookup?ids=3&ids=7` | Developer+; the labels of exactly those rows (at most 100), to show references by name |
| GET | `/api/projects/{id}/assistant/sessions` | Developer+; **my** conversations with the AI assistant in this project |
| POST | `/api/projects/{id}/assistant/sessions` | `{ goal }` → 201 with the first question; 409 if I already have an open one |
| GET | `/api/projects/{id}/assistant/sessions/{sessionId}` | my conversation: messages, current question or proposal; anyone else's is 404 |
| POST | `/api/projects/{id}/assistant/sessions/{sessionId}/answers` | `{ version, text }` → the next question or a proposal |
| POST | `/api/projects/{id}/assistant/sessions/{sessionId}/revise` | `{ version, feedback }` → a new proposal |
| POST | `/api/projects/{id}/assistant/sessions/{sessionId}/continue` | `{ version }` retries the AI's turn after a failed call |
| POST | `/api/projects/{id}/assistant/sessions/{sessionId}/confirm` | `{ version }` creates the proposal as draft tables |
| POST | `/api/projects/{id}/assistant/sessions/{sessionId}/cancel` | `{ version }` |
| GET · PUT · DELETE | `/api/me/ai-key` | my own Groq key: status (last 4 characters only), `{ apiKey }` to set, remove |

Every table change carries the `version` the client last saw and returns the whole table with
its new version; a stale version gets **409**.

**Schema limits:** at most 50 tables per project and 100 columns per table. Names are
`[a-z][a-z0-9_]`, at most 64 characters, lower-cased, not a MySQL reserved word, not `id` and not
starting with `cf_`. A table's row must fit MySQL's 65,535-byte limit (computed exactly, including
the `id` column); Text/Json columns can't be unique or have a default, and a unique Varchar is at
most 768 characters.

**Relations:** a column can reference another table of the project (or its own table): it holds
that table's `id`, so it is BigInt with no default, and `onDelete` is `Restrict`, `Cascade` or
`SetNull` (SetNull needs a nullable column). A table can't be deleted while other tables'
columns reference it. The **Diagram** page (`/projects/<id>/tables/diagram`) draws the tables
and their relations.

**How applying works:** MySQL commits each DDL statement on its own, so an apply can't be one
transaction. Instead, the API takes the project's lock (`GET_LOCK` on one dedicated connection),
so two applies never overlap. It rebuilds the plan under the lock, and the plan must hash to the
`planHash` you reviewed; otherwise nothing runs (409 `plan-stale`). A journal row records each
statement as it runs. The draft is marked applied only after every statement succeeded, together
with a snapshot of the real schema. If a statement fails, the journal row keeps the failed
statement and MySQL's error. Planning again compares the draft with the real database, so the new
plan contains only the changes that are still missing. Tables and columns that CoreFoundry didn't
create are reported but never dropped.

### Code export

`GET /api/projects/{id}/export` (the **Export code** button) returns `<project>-backend.zip`: a .NET 10 solution
in Clean Architecture generated from the applied tables.

- `Domain` (one entity per table), `Application` (DTOs, validation, services), `Infrastructure` (EF Core `DbContext`,
  one configuration per table, a generated `InitialCreate` migration, JWT and password hashing) and `Api` (one
  controller per table, register/login, Swagger UI at `/swagger`).
- `Dockerfile`, `docker-compose.yml` (API + MySQL 8.4, migrations applied on start), `.env.example` and a README
  with every table's fields.
- The generated API follows the Data API's contract: JSON names are the column names, decimals are strings,
  paging/sorting are the same, and so are the 400/404/409 answers.
- **Access rules:** each table's Read and Write level (Public / Signed-in / Admin, set in the designer or the
  API page) becomes `[AllowAnonymous]` / `[Authorize]` / `[Authorize(Roles = "Admin")]` on its endpoints; the
  first account to register the exported API becomes Admin.
- **Realtime:** the export also generates a SignalR hub at `/hubs/realtime` that pushes `insert` / `update` /
  `delete` notifications per table; a table's Read level decides who may subscribe to it, and a table with its
  Realtime switch off sends none.

Unapplied draft changes are not exported (the export matches the running database). A test exports a Bookshop,
builds it, checks its migration with `dotnet ef`, runs it against MySQL, uses it over HTTP and compares its tables
with CoreFoundry's. Two more exports are built and run the same way: one checks the access levels and roles
(`AccessEndpointsTests`), the other the realtime hub with SignalR clients (`RealtimeEndpointsTests`). Set
`CF_SKIP_EXPORT_BUILD=1` to skip these three slow tests.

### AI assistant

**Design with AI** interviews you about your app, one question at a time with suggested answers, then proposes
new tables and new columns on existing tables, with access levels and realtime. It only adds; nothing existing is
renamed or dropped. Every proposal is checked against the designer's rules, and problems are sent back to the model
to fix before you see it. **Confirm** creates the changes as drafts, which you review and apply like any other.
Each member's conversations are their own, and are saved, so you can leave and resume.

It uses [Groq](https://console.groq.com) (an OpenAI-compatible API; model `openai/gpt-oss-120b` by default):

- **Server key** (optional, local runs only): `Ai:ApiKey` in user-secrets (see step 2 above). Each user may make
  up to `Ai:DailyCallsPerUser` (60) calls a day on it. Docker doesn't set one.
- **Your own key:** **AI key** in the header. It's stored encrypted (ASP.NET Core Data Protection, keys in the
  metadata database) and never shown again; your own key isn't capped.
- Other settings: `Ai:Model`, `Ai:BaseUrl` (any OpenAI-compatible API), `Ai:TimeoutSeconds`, and the per-user rate
  limit `RateLimiting:AssistantPermitLimit` per `AssistantWindowSeconds`.

Details: [phase 10](docs/phases/phase-10-ai-assistant.md).

### Data API with curl

Once a plan has created `authors` and `books` in project 7, rows can be written with any HTTP
client. The access token comes from register/login and lasts 15 minutes:

```bash
API=http://localhost:5172
curl -s $API/api/auth/login -H "Content-Type: application/json" \
  -d '{"email":"me@example.com","password":"correct horse battery"}'
# → {"accessToken":"eyJ…","expiresAt":"…","user":{"id":1,"email":"me@example.com"}}
TOKEN=eyJ…   # paste the accessToken
AUTH="Authorization: Bearer $TOKEN"

# Insert: 201 with the stored row
curl -s -X POST $API/api/projects/7/data/authors -H "$AUTH" -H "Content-Type: application/json" \
  -d '{"name":"Frank Herbert"}'
# → {"id":1,"name":"Frank Herbert"}
curl -s -X POST $API/api/projects/7/data/books -H "$AUTH" -H "Content-Type: application/json" \
  -d '{"title":"Dune","price_usd":"19.99","published_on":"1965-08-01","author_id":1}'

# List, most expensive first, 10 per page
curl -s "$API/api/projects/7/data/books?sort=-price_usd&pageSize=10&page=1" -H "$AUTH"
# → {"items":[{"id":1,"title":"Dune","price_usd":"19.99",…}],"page":1,"pageSize":10,"total":1}

# Replace (columns left out get their default, or NULL), then delete
curl -s -X PUT $API/api/projects/7/data/books/1 -H "$AUTH" -H "Content-Type: application/json" \
  -d '{"title":"Dune","price_usd":"17.50","author_id":1}'
curl -s -X DELETE $API/api/projects/7/data/books/1 -H "$AUTH" -o /dev/null -w "%{http_code}\n"   # → 204
```

**Values:** Int/BigInt are JSON integers (the API returns ids as numbers; above 2^53 JavaScript
loses precision). Decimals may be sent as numbers or strings but are always returned as **strings**,
so no digits are lost, and more decimals than the column has is an error, never rounded. Bool is
`true`/`false`, Date is `yyyy-MM-dd`, DateTime is ISO-8601 (a value with an offset is stored in UTC;
values come back without an offset), Uuid is the canonical form and Json takes any JSON value.
Invalid fields come back together as a 400 `ValidationProblemDetails` keyed by column. A duplicate
unique value or a row that others still reference is a 409; a reference to a missing row is a 400
on that column.

**What the Data API sees:** the tables and columns as they were after the last successful apply,
never the draft. A column you just renamed in the designer keeps its old name here until you apply.
Tables and columns created outside CoreFoundry aren't served.

After pulling new migrations: `dotnet ef database update --project src/CoreFoundry.Infrastructure --startup-project src/CoreFoundry.Api`

## Docs
- [Plan](intial-plan.md)
- [Progress](docs/PROGRESS.md): what's done, how it's verified, and what's next
- [Phases](docs/phases/README.md)
- [Data model](docs/corefoundry-erd.html) · [Backend flows](docs/corefoundry-flows.html) (open in a browser)
