# CoreFoundry

Design a database schema in the browser, preview the exact SQL, and apply it
to real MySQL tables safely. A portfolio project focused on dynamic schema
management, safe SQL generation, and clean backend architecture.

> Status: **M1 — auth and projects done; members & frontend next**. See [the phases](docs/phases/README.md).

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
cp .env.example .env.local
npm install
npm run dev
```
Open http://localhost:3000. The page shows the API and database health.

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

After pulling new migrations: `dotnet ef database update --project src/CoreFoundry.Infrastructure --startup-project src/CoreFoundry.Api`

## Docs
- [Plan](intial-plan.md)
- [Phases](docs/phases/README.md)
- [Data model](docs/corefoundry-erd.html) · [Backend flows](docs/corefoundry-flows.html) (open in a browser)
