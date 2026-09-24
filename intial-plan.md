# CoreFoundry — Portfolio Project Plan

**Goal:** a job-hunting showcase project that proves you can design and ship a
production-minded backend, not another CRUD demo.

**Positioning:** a developer tool that lets a user define a database schema
through a UI, preview the exact SQL it will run, apply it to real MySQL, and
then immediately read/write data through an auto-generated API —
demonstrating dynamic schema management, safe SQL generation, and clean
backend architecture.

> This is intentionally a **leaner slice** than the full multi-phase roadmap
> from the original brainstorm. The goal here is a strong, finishable,
> interview-ready project — not a SaaS company.

---

## 0. Decisions log

| # | Decision | Why |
|---|----------|-----|
| D1 | **Draft → Plan → Apply** model for schema changes | Metadata is the *desired* state; the real MySQL schema is the *actual* state. A diff between them produces a reviewable plan. This is the answer to the "metadata ↔ real schema" hard problem. |
| D2 | **One MySQL database per project** (`cf_p_<id>`) instead of table-name prefixes | Database = namespace in MySQL, so it's nearly free. No name collisions, no eating into the 64-char identifier limit, project delete = one `DROP DATABASE`, clean isolation story. |
| D3 | **No reliance on DDL transactions** — atomic single statements + a `SchemaMigrations` journal | MySQL implicitly commits on every DDL statement; you cannot roll back a batch of DDL. Design around it instead of pretending. |
| D4 | Every generated table gets a **system `id BIGINT AUTO_INCREMENT PRIMARY KEY`** column (not editable/deletable) | Gives the Data API a guaranteed row identity; removes "composite / missing PK" edge cases from v1. |
| D5 | **Data viewer + generic CRUD API** for generated tables is in scope | The demo shouldn't end at "a table exists." Reuses the same safe-identifier layer. |
| D6 | **Foreign keys / relationships: roadmap only** | FKs complicate diffing, drop ordering and type changes considerably. |
| D7 | Stack: **.NET 10 LTS, MySQL 8.4 LTS, Next.js (latest, App Router, TS)** | Current LTS versions. |
| D8 | Columns and tables have **stable metadata Ids + `AppliedName`** | Lets the differ tell a *rename* (`RENAME COLUMN`, keeps data) from a *drop + add* (loses data). |
| D9 | EF Core provider: **Oracle `MySql.EntityFrameworkCore` 10.x** (decided in M0) | Pomelo has no EF Core 10 release (latest is 9.0.0). |
| D10 | **Local MySQL for development, no Docker for now**. Develop on MySQL 9.2, stay compatible with 8.4 LTS | Uses the MySQL server already installed. A test container setup for the full app comes later. |
| D11 | Dev secrets in **.NET user-secrets** (`corefoundry-api-dev`), never in the repo | Standard for ASP.NET Core local development. |
| D12 | Tests run on **Microsoft.Testing.Platform** (`global.json` `test.runner`) with xunit v3 + Shouldly | .NET 10 SDK no longer runs VSTest for these packages. FluentAssertions v8 is paid. |
| D13 | `Projects.DatabaseName` is **computed** (`cf_p_{Id}`), not a column | The Id only exists after insert; a derived name can never drift from it. |
| D14 | `SchemaMigrations.StatementCount` column added | Lets the journal validate progress and refuse `Applied` before every statement ran. |
| D15 | EF Core's provider runs on Oracle's **MySql.Data** driver; the schema engine/Data API use **MySqlConnector** | Consequence of D9: two drivers, one per data-access strategy. |
| D16 | Web dev server on **port 3100** | A local VPN service holds `127.0.0.1:3000`, which made `:3000` only intermittently reachable. |
| D17 | Next.js **proxies `/api/*`** to the API (`next.config.ts` rewrites); the API trusts `X-Forwarded-For` from loopback proxies only | One origin for the SameSite=Strict refresh cookie (as planned for production), while the login rate limit stays per client IP. |
| D18 | Identifier regex is `^[a-z][a-z0-9_]{0,63}$` (64 characters, MySQL's limit), not `{0,62}`; the reserved-word list is generated from MySQL 9.2's `INFORMATION_SCHEMA.KEYWORDS` (`db/reserved-words.sql`) | `{0,62}` allowed only 63 characters, contradicting the 64/65-character tests. A newer server's list is a superset of 8.4's, so it only rejects more. |
| D19 | The row-size guard computes **MySQL's exact row size** (per-type bytes, length prefixes, the `id` column, NULL bitmap), verified against MySQL at the 65,535-byte boundary for every type; UNIQUE is refused on Text/Json and on Varchar > 768 | The planned "sum of Varchar × 4" passed tables MySQL rejects. Text/Json can't be indexed without a prefix, and InnoDB keys are capped at 3,072 bytes. |
| D20 | Optimistic concurrency via **`ProjectTables.Version`** (int), bumped by every change to the table or its columns; every write sends the version it last saw (`?version=` on DELETE) | MySQL has no rowversion type; a whole-table version makes two people editing different columns of one table conflict instead of producing a mix neither saw. |
| D21 | A table pending drop does **not** set `PendingDrop` on its columns; they are reported as `PendingDrop` while their table is | Restoring the table then can't also resurrect a column that was deleted on its own. Same UI result. |
| D22 | Deleting a never-applied column (a hard delete) offers an **Undo** in the designer that re-adds it at the same position | In M2 nothing is applied yet, so every delete is a hard delete; the Definition of done still needs "delete one and undo it". The re-added column gets a new id, which is harmless before apply. |
| D23 | Column reordering uses **@dnd-kit** (core + sortable) | Animated sorting with pointer, touch and keyboard support built in. |
| D24 | MySQL duplicate-key errors (1062) on save map to **409** | Unique indexes are the backstop for name races the services can't see; they were surfacing as 500s. |

---

## 1. Scope (Portfolio MVP)

Build:
- Auth (JWT access tokens + rotating refresh tokens)
- Projects with multi-tenancy (ProjectMembers + roles — Owner/Admin/Developer)
- Database Designer: create/edit tables & columns via UI (draft state)
- Dynamic Schema Engine: diff draft vs. real schema → preview SQL plan →
  apply → migration history
- Data API + data viewer: browse/insert/edit/delete rows of generated tables
- Next.js dashboard: login → projects → table designer → schema plan/apply
  → data viewer

Explicitly **cut** (mention as "future roadmap" in the README, don't build):
- Foreign keys / relationships between generated tables
- Indexes beyond `UNIQUE` (composite indexes, full-text)
- Redis caching, background jobs
- Full code generator (schema → downloadable backend project)
- Realtime, file storage, CLI, one-click deployment, billing
- Database-server-per-tenant isolation

Cutting these is what makes this finishable while you're also working full
time and freelancing.

---

## 2. Architecture

```
Next.js (TypeScript, App Router)
        │ HTTPS (JWT in memory, refresh token in httpOnly cookie)
        ▼
ASP.NET Core Web API (.NET 10)
   ├── Api             controllers, auth handlers, problem-details errors
   ├── Application     use cases, DTOs, validation, authorization rules
   ├── Domain          entities, SchemaModel, SchemaDiffer (pure), DataType rules
   └── Infrastructure  EF Core (metadata), MySqlConnector/Dapper (dynamic),
                       SqlRenderer, SchemaIntrospector, JWT, hashing
        │
        ▼
      MySQL 8.4
   ├── corefoundry          metadata DB (EF Core, migrations)
   ├── cf_p_1               generated tables for project 1
   ├── cf_p_2               generated tables for project 2
   └── ...
```

### Two data-access strategies (and why)
- **EF Core** for CoreFoundry's **own** metadata tables — shape known at
  compile time, migrations, change tracking.
- **MySqlConnector + Dapper** for the generated tables — their shape is
  defined by end users at runtime, so EF Core models/migrations can't
  describe them. SQL is built from *validated metadata only*.

### Two MySQL accounts (least privilege)
- `cf_meta` — full rights on `corefoundry` only.
- `cf_engine` — `CREATE/ALTER/DROP/SELECT/INSERT/UPDATE/DELETE` on
  `cf_p\_%` databases only. Even a bug in the engine can't touch metadata.

### Schema engine pipeline (the core)
```
Draft metadata (EF)          INFORMATION_SCHEMA (real DB)
        │                               │
        ▼                               ▼
  SchemaModel (desired)        SchemaModel (actual)
        └──────────┬────────────────────┘
                   ▼
          SchemaDiffer  (pure function, heavily unit-tested)
                   ▼
        List<SchemaOperation>  (CreateTable, DropTable, RenameTable,
                   │            AddColumn, DropColumn, RenameColumn,
                   │            ModifyColumn) + destructive flags
                   ▼
          SqlRenderer   (pure function: ops → DDL strings)
                   ▼
   Plan preview (UI)  ──user confirms──►  SchemaApplier
                                            ├─ acquire per-project lock
                                            ├─ write SchemaMigrations(Pending)
                                            ├─ run statements one by one
                                            ├─ update progress / AppliedName
                                            └─ mark Applied or Failed
```

Keep Clean Architecture honest but not over-abstracted: no
`IGenericRepository<T>` for the sake of it. `SchemaDiffer` and `SqlRenderer`
being pure (no I/O) is the most important structural choice — it's what
makes the dangerous part testable.

---

## 3. Database design (metadata DB `corefoundry`)

**Users**
```
Id, Email (unique), PasswordHash, CreatedAt, UpdatedAt
```

**RefreshTokens**
```
Id, UserId, TokenHash, ExpiresAt, CreatedAt, RevokedAt, ReplacedByTokenId
```
Rotation on every refresh; reuse of a revoked token revokes the whole chain
(token-theft detection — good interview point).

**Projects**
```
Id, Name, Slug (unique), OwnerId,
SchemaVersion, Status, CreatedAt, UpdatedAt
```

**ProjectMembers**
```
ProjectId, UserId, Role (Owner | Admin | Developer), CreatedAt
PK (ProjectId, UserId)
```

**ProjectTables** (draft)
```
Id, ProjectId, Name, AppliedName (null until applied), PendingDrop,
CreatedAt, UpdatedAt
UNIQUE (ProjectId, Name)
```

**ProjectColumns** (draft)
```
Id, TableId, Name, AppliedName (null until applied), DataType,
Length, Precision, Scale, IsNullable, IsUnique, DefaultValue,
OrdinalPosition, PendingDrop, CreatedAt, UpdatedAt
UNIQUE (TableId, Name)
```
No `IsPrimaryKey` — the system `id` column is always the PK (D4).

**SchemaMigrations**
```
Id, ProjectId, Version, Status (Pending | Applied | Failed),
StatementsJson, StatementCount, StatementsApplied, SnapshotJson, Error,
RequestedBy, CreatedAt, CompletedAt
```
`SnapshotJson` = full applied schema after success. The Data API reads the
latest applied snapshot (cached), **never the draft**.

### Allowed data types (whitelist → MySQL)
| CoreFoundry | MySQL | Params |
|---|---|---|
| Int | `INT` | — |
| BigInt | `BIGINT` | — |
| Decimal | `DECIMAL(p,s)` | Precision 1–65, Scale 0–30, ≤ p |
| Bool | `TINYINT(1)` | — |
| Varchar | `VARCHAR(n)` | Length 1–4000 (utf8mb4 row-size aware) |
| Text | `TEXT` | no default allowed |
| DateTime | `DATETIME(6)` | — |
| Date | `DATE` | — |
| Json | `JSON` | no default allowed |
| Uuid | `CHAR(36)` | — |

`DefaultValue` is validated/parsed per type and rendered as a typed literal
(DDL defaults can't be parameterized).

### Identifier rules
- Regex `^[a-z][a-z0-9_]{0,63}$` (64 characters, D18)
- Reject MySQL reserved words and system names (`id`, anything `cf_*`)
- Always rendered backtick-quoted, and backticks are impossible anyway
  because of the regex — defense in depth.
- Physical database names are generated (`cf_p_<id>`), never user-supplied.

---

## 4. API surface

```
POST   /api/auth/register
POST   /api/auth/login
POST   /api/auth/refresh
POST   /api/auth/logout

GET    /api/projects
POST   /api/projects
GET    /api/projects/{projectId}
PATCH  /api/projects/{projectId}
DELETE /api/projects/{projectId}

GET    /api/projects/{projectId}/members
POST   /api/projects/{projectId}/members
PUT    /api/projects/{projectId}/members/{userId}
DELETE /api/projects/{projectId}/members/{userId}

# Draft schema
GET    /api/projects/{projectId}/tables
POST   /api/projects/{projectId}/tables
PUT    /api/projects/{projectId}/tables/{tableId}
DELETE /api/projects/{projectId}/tables/{tableId}
POST   /api/projects/{projectId}/tables/{tableId}/columns
PUT    /api/projects/{projectId}/tables/{tableId}/columns/{columnId}
DELETE /api/projects/{projectId}/tables/{tableId}/columns/{columnId}

# Schema engine
GET    /api/projects/{projectId}/schema/plan         # diff + SQL preview
POST   /api/projects/{projectId}/schema/apply        # body: planHash
GET    /api/projects/{projectId}/schema/migrations
GET    /api/projects/{projectId}/schema/drift        # snapshot vs real DB

# Data API (against applied schema only)
GET    /api/projects/{projectId}/data/{table}?page=&pageSize=&sort=
POST   /api/projects/{projectId}/data/{table}
GET    /api/projects/{projectId}/data/{table}/{id}
PUT    /api/projects/{projectId}/data/{table}/{id}
DELETE /api/projects/{projectId}/data/{table}/{id}
```

Everything is nested under `projectId` so one authorization handler covers
every route.

`apply` takes the `planHash` returned by `plan`, and refuses if the draft
changed in between — the user applies exactly what they reviewed.

### Roles
| Action | Owner | Admin | Developer |
|---|:-:|:-:|:-:|
| Delete project / transfer ownership | ✔ | | |
| Manage members | ✔ | ✔ (not Owner) | |
| Edit draft schema, view plan | ✔ | ✔ | ✔ |
| **Apply schema** | ✔ | ✔ | |
| Data read/write | ✔ | ✔ | ✔ |

Implemented as ASP.NET Core policy-based authorization with a
`ProjectRoleRequirement` handler reading `ProjectMembers` — not just
`[Authorize]`.

---

## 5. The part that makes this a strong interview story

1. **Safe dynamic SQL** — identifiers can't be parameterized, so they go
   through whitelist regex + reserved-word check + backtick quoting, and
   only ever come from validated metadata. Values in the Data API are
   always parameterized. The client never sends SQL fragments.
2. **Honest atomicity with MySQL DDL** — MySQL implicitly commits DDL, so:
   - `CREATE TABLE` includes all columns in one statement (atomic).
   - Multiple column changes on one table are combined into a single
     `ALTER TABLE ... ADD ..., MODIFY ..., DROP ...` (atomic in InnoDB 8.x).
   - A `SchemaMigrations` journal records Pending → progress → Applied/Failed.
   - Recovery after a failure is natural: the next `plan` re-diffs against
     the **real** `INFORMATION_SCHEMA`, so it only proposes what's still
     missing.
   - A per-project lock (`GET_LOCK`) prevents two concurrent applies.
3. **Metadata ↔ real schema consistency** — Draft/Plan/Apply (D1):
   edits never touch MySQL directly; the plan shows exact SQL and flags
   destructive ops (drop, narrowing type, NULL → NOT NULL on a populated
   table); renames are detected via stable Ids (D8); drift detection
   compares the last applied snapshot to the real DB.

Plus: *why not EF Core migrations?* — the schema is defined by end users at
runtime, not by developers at build time.

---

## 6. Testing strategy

- **Unit tests** (xUnit): identifier validator, type/default validator,
  `SchemaDiffer`, `SqlRenderer`. These are pure — aim for very high coverage
  including nasty inputs (injection attempts, reserved words, max lengths).
- **Integration tests** with **Testcontainers (MySQL 8.4)**: apply plans
  against a real server, then assert via `INFORMATION_SCHEMA`. Include a
  "failure mid-apply then re-plan" test.
- **API tests** with `WebApplicationFactory`: auth flow, refresh rotation,
  role checks (Developer can't apply, non-member gets 404).
- **CI**: GitHub Actions running build + all tests on every push.

---

## 7. What to study / focus on

- **MySQL**: `INFORMATION_SCHEMA` (`TABLES`, `COLUMNS`, `STATISTICS`);
  implicit commit behaviour of DDL; online DDL / `ALGORITHM=INSTANT`;
  identifier rules and reserved words; `GET_LOCK`; utf8mb4 row-size limits.
- **Data access**: EF Core code-first + migrations; MySqlConnector/Dapper;
  justifying two strategies in one app. Check EF Core 10 provider support
  at kickoff (Pomelo vs. Oracle's `MySql.EntityFrameworkCore`).
- **Security**: dynamic-identifier SQL injection; JWT + refresh-token
  rotation; policy-based authorization; least-privilege DB accounts.
- **Architecture**: Clean Architecture without ceremony; keeping the
  dangerous logic pure and testable.
- **Frontend**: Next.js App Router + TypeScript; forms with validation
  (zod), a table-builder UI, a diff/SQL preview view, a paginated data grid.
- **Delivery**: Docker Compose (api + web + mysql); README with
  architecture diagram — for a portfolio project, not optional.

---

## 8. Repository layout

```
CoreFoundry/
├── src/
│   ├── CoreFoundry.Api/
│   ├── CoreFoundry.Application/
│   ├── CoreFoundry.Domain/
│   └── CoreFoundry.Infrastructure/
├── tests/
│   ├── CoreFoundry.UnitTests/
│   └── CoreFoundry.IntegrationTests/
├── web/                      # Next.js app
├── docker-compose.yml
├── .github/workflows/ci.yml
└── README.md
```

---

## 9. Milestones (not fixed weeks — pace it around Wink + freelance work)

### M0 — Setup
- [ ] `git init`, `.gitignore`, push to GitHub early (history matters)
- [ ] .NET 10 solution with the 4 projects + 2 test projects
- [ ] `docker-compose.yml` with MySQL 8.4 + the two DB accounts
- [ ] Next.js app scaffold in `web/`
- [ ] GitHub Actions CI (build + test)

### M1 — Auth + projects + members
- [ ] Users, RefreshTokens, Projects, ProjectMembers + EF migrations
- [ ] Register / login / refresh (rotation + reuse detection) / logout
- [ ] Projects CRUD; creating a project creates `cf_p_<id>`
- [ ] Members management + `ProjectRoleRequirement` policy handler
- [ ] Next.js: login, projects list, create project, members page
- [ ] API tests for auth + role checks

### M2 — Table designer (draft only)
- [ ] ProjectTables / ProjectColumns + EF migrations
- [ ] Identifier + data-type/default validators (unit-tested)
- [ ] Draft CRUD endpoints
- [ ] Next.js table designer UI (add/edit/reorder/delete columns)

### M3 — Dynamic schema engine ⭐ (protect this one)
- [ ] `SchemaIntrospector` (INFORMATION_SCHEMA → SchemaModel)
- [ ] `SchemaDiffer` incl. rename detection + destructive flags
- [ ] `SqlRenderer` incl. combined ALTER statements
- [ ] `SchemaMigrations` + `SchemaApplier` (lock, journal, progress)
- [ ] `plan` / `apply` (planHash) / `migrations` / `drift` endpoints
- [ ] Next.js plan preview (SQL + warnings) + apply + history
- [ ] Testcontainers integration tests incl. failure/recovery

### M4 — Data API + data viewer
- [ ] Load applied snapshot; build parameterized SELECT/INSERT/UPDATE/DELETE
- [ ] JSON → typed value coercion per column type; validation errors as
      ProblemDetails
- [ ] Pagination + whitelisted sort
- [ ] Next.js data grid: browse, add, edit, delete rows

### M5 — Portfolio polish
- [ ] README: pitch, architecture diagram, "hard problems & decisions"
      section (reuse §0 and §5), roadmap of cut features
- [ ] Seed/demo script (user + project + sample tables)
- [ ] Demo GIF/video: design → preview SQL → apply → insert rows
- [ ] Live demo deploy if possible

**Priority if time gets tight:** M3 > M4 > M2 UI polish. M3 is what makes
this more than "yet another CRUD project" on your GitHub; M4 is what makes
the demo land.
