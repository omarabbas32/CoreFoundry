# CoreFoundry

Design a database schema in the browser, preview the exact SQL, and apply it
to real MySQL tables safely. A portfolio project focused on dynamic schema
management, safe SQL generation, and clean backend architecture.

> Status: **M7 built: schema templates**. Start a project from a ready E-commerce schema, with sample rows. Before that, M6: download any project as a deployable .NET backend. M4 (Data API) is built too; the hands-on UI checks of M4 and M6 are still open. Next: M5 polish. See [the phases](docs/phases/README.md).

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
type parameters and defaults, drag-to-reorder, delete with undo. Columns can reference other
tables, and the schema diagram shows the relations (pan, zoom, drag). Draft changes reach the
project's database only through **Review plan** (`/projects/<id>/schema`): it shows every
operation and the exact SQL, and Admins apply it from there. **History** lists every apply with
its SQL and any error, and the project page warns when the database was changed outside CoreFoundry.
**Browse data** (`/projects/<id>/data/<table>`) shows the rows of applied tables: sortable columns,
paging, and a side panel to add or edit a row. The form is generated from the applied columns,
reference columns get a picker that searches the other table, and deletes ask for confirmation first.
**Templates:** the New project dialog (and an empty project's designer) can start from a ready schema, for now
**E-commerce** (customers, addresses, categories, products, orders, order items, payments, reviews). The tables
are created as drafts to edit, review and apply like any other; optional sample rows are added right after the
first apply (or later with "Load sample data" on an empty table).
**Export code** (project page and API page) downloads the project as a standalone backend, enforcing each
table's read/write access level and generating a realtime hub for the tables it can read; see below.
The **API** page (`/projects/<id>/api`) documents the project's own endpoints: base URL, how to get a
token, and for every applied table its routes, fields and ready-to-copy curl and JavaScript examples.
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
| GET | `/api/projects/{id}/data/{table}?page=&pageSize=&sort=` | Developer+; `{ items, page, pageSize, total }`; `pageSize` 1–100 (default 25), `sort` a column, `-` first for descending |
| GET | `/api/projects/{id}/data/{table}/{rowId}` | Developer+; one row |
| POST | `/api/projects/{id}/data/{table}` | Developer+; JSON object of column values → 201 + the row |
| PUT | `/api/projects/{id}/data/{table}/{rowId}` | Developer+; full replace: columns left out get their default, or NULL |
| DELETE | `/api/projects/{id}/data/{table}/{rowId}` | Developer+; 204; 409 if other rows still reference it (`Restrict`) |
| GET | `/api/templates` | signed in; the ready schemas with their tables |
| POST | `/api/projects/{id}/templates/{key}` | Developer+; `{ withSampleData }` creates the template's draft tables; 409 if the project has tables |
| POST | `/api/projects/{id}/sample-data` | Developer+; inserts the template's sample rows into applied tables that are still empty |
| GET | `/api/projects/{id}/export` | Developer+; zip of a .NET backend for the applied tables; 409 if nothing is applied |
| GET | `/api/projects/{id}/data/{table}/lookup?q=&limit=` | Developer+; `[{ id, label }]` for reference pickers (label = first Varchar column) |

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
  `delete` notifications per table; a table's Read level decides who may subscribe to it.

Unapplied draft changes are not exported (the export matches the running database). A test exports a Bookshop,
builds it, checks its migration with `dotnet ef`, runs it against MySQL, uses it over HTTP and compares its tables
with CoreFoundry's (set `CF_SKIP_EXPORT_BUILD=1` to skip that slow test).

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
