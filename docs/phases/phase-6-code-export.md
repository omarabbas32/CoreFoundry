# M6 — Code export: a deployable backend per project

> **Built** on branch `m6-code-export` (2026-09-25). The hands-on UI download and a Docker run are still open (§6).
> Notes marked **Built:** say where the build differs from this plan; decisions D33–D35 are in the plan's log.

**Goal:** from a project's schema, generate a standalone **.NET 10 Clean Architecture** backend that the user
downloads as a **.zip**, runs with `docker compose up`, and owns from then on. It has typed entities, an **EF Core**
model with a generated **initial migration**, CRUD endpoints per table, **JWT** auth and **Swagger UI**.

**Depends on:** M3 (applied snapshot), M4 (type coercion rules, identifier safety).
**Decided by the user (2026-09-25):** .NET 10 Clean Architecture · EF Core + migration · Dockerfile + compose,
JWT auth, OpenAPI/Swagger UI · no test project · delivery as .zip download.

---

## 1. What gets exported

- **Source of truth: the last applied snapshot** (the same managed tables and columns the Data API serves, D29).
  The draft is not exported: the export matches what is running. Unapplied draft changes → the export button says
  "Apply the plan first to include them".
- Schema only, **no row data** (a seed export can come later).

### Generated solution (project "Bookshop")
```
Bookshop/
├─ Bookshop.slnx                **Built:** the .NET 10 XML solution format
├─ Directory.Build.props · Directory.Packages.props · global.json · .gitignore · .dockerignore
├─ src/
│  ├─ Bookshop.Domain/           Entities/Author.cs, Book.cs (plain classes, no EF attributes)
│  ├─ Bookshop.Application/      per table: DTOs, request validation, service, repository port; paging/sort
│  ├─ Bookshop.Infrastructure/   AppDbContext, one IEntityTypeConfiguration per table, repositories,
│  │                             Migrations/<ts>_InitialCreate.cs + AppDbContextModelSnapshot.cs, JWT + password hashing
│  └─ Bookshop.Api/              Controllers per table + AuthController, Program.cs, appsettings*.json, Swagger UI
├─ Dockerfile                    multi-stage (sdk → aspnet), non-root
├─ docker-compose.yml            mysql:8.4 + api; migrations applied on start
├─ .env.example                  MySQL password, JWT signing key
└─ README.md                     run locally, run with Docker, endpoints, auth, evolving the schema with EF migrations
```

### Endpoints (same contract as the M4 Data API, so docs and clients carry over)
| Method | Route | Notes |
|---|---|---|
| POST | `/api/auth/register`, `/api/auth/login` | email + password → JWT (lifetime from config) |
| GET | `/api/{table}?page=&pageSize=&sort=` | `{ items, page, pageSize, total }`, `id` tie-breaker |
| GET / PUT / DELETE | `/api/{table}/{id}` | PUT is a full replace |
| POST | `/api/{table}` | 201 + Location |

All table endpoints require a token. Errors are ProblemDetails; validation errors per field; unique → 409;
missing referenced row → 400; row still referenced → 409.

---

## 2. Mapping rules

| Schema | C# (Domain) | EF configuration |
|---|---|---|
| table `books` | class `Book` (simple singularizer: `ies→y`, `ses/xes/zes→`, `s→`; collisions fall back to the PascalCase plural) | `ToTable("books")` |
| column `price_usd` | property `PriceUsd` (a clash with the class name gets a `Value` suffix) | `HasColumnName("price_usd")` |
| Int / BigInt | `int` / `long` | |
| Decimal(p,s) | `decimal` | `HasPrecision(p, s)`; p > 28 is flagged in the README (.NET `decimal` holds 28–29 digits) |
| Bool | `bool` | `tinyint(1)` |
| Varchar(n) / Text | `string` | `HasMaxLength(n)` / `HasColumnType("text")` |
| Date / DateTime | `DateOnly` / `DateTime` | `date` / `datetime(6)` |
| Uuid | `Guid` | `char(36)` |
| Json | `string` (raw JSON, validated on write) | `json` |
| nullable | `?` on value types, `string?` | `IsRequired(false)` |
| default | — | `HasDefaultValueSql(...)` rendered from `ColumnDefault` (same rules as `MySqlSqlRenderer`) |
| unique | — | `HasIndex(...).IsUnique().HasDatabaseName("uq_<table>_<column>")` |
| reference `author_id → authors` | `long? AuthorId` + navigation `Author? Author` | `HasOne().WithMany().HasForeignKey().OnDelete(Restrict/Cascade/SetNull)`, `fk_…` name |

**Built:** a column with a default is nullable in C# (null = MySQL fills in the default; otherwise EF would treat
an explicit `0`/`false` as "not set"); `DateOnly` goes through a `DateTime` converter (the Oracle connector reads
DATE as DateTime); decimals are returned with the column's scale (`19.90`); the index behind each foreign key is
named like the constraint (`fk_…`), as MySQL names it; type names that would clash (`Task`, `User`'s set name
`Users`, namespace segments, the solution name) are renamed (`Tasks`, `Users2`, `…Entity`).

Names come only from the snapshot (already `[a-z][a-z0-9_]`), so the generated identifiers are safe C#.
PascalCase can't produce a C# keyword (they're all lower-case).

---

## 3. How it's built in CoreFoundry

| Component | Layer | Responsibility |
|---|---|---|
| `ExportModel` | Application | From `DataSchema` + draft metadata (on-delete rules, defaults): entities, properties, relations, names |
| `CodeNames` | Application (pure) | PascalCase, singularize, collision handling; unit-tested |
| `DotNetBackendGenerator` | Infrastructure (pure) | `ExportModel` → `IReadOnlyList<GeneratedFile(path, content)>`, templates as C# raw string literals (no template package) |
| `EfMigrationWriter` | Infrastructure (pure) | the `InitialCreate` migration + model snapshot text, from the same model |
| `ExportService` + `GET /api/projects/{id}/export` | Application / Api | Developer+; builds the zip in memory (`System.IO.Compression`), `application/zip`, `bookshop-backend.zip` |
| Web | — | "Export code" button (project page + API page) with what's included and the unapplied-changes note |

**Generating the migration without running `dotnet ef` on the server:** the migration and model snapshot are written
as code. The integration test proves they're right (below), so exports stay fast and need no SDK at runtime.

---

## 4. Steps (plan-before-execute; commit after each verified step)

1. **Naming + export model**: `CodeNames` (table-driven tests), `ExportModel` from the snapshot and draft
   (on-delete, defaults), with unit tests for Bookshop, self-references, every type.
2. **Generator: Domain + Application + Api** templates, solution/props files; snapshot tests of key files.
3. **Generator: Infrastructure**: DbContext, entity configurations, repositories, JWT/password hashing,
   `InitialCreate` migration + model snapshot.
4. **Docker, compose, README** of the generated project.
5. **Export endpoint + zip**, integration test: design + apply Bookshop → export → unzip to a temp folder →
   `dotnet build` passes → `dotnet ef migrations has-pending-model-changes` reports none (the hand-written
   migration matches the model) → `database update` on a scratch MySQL database creates the same tables
   (`SHOW CREATE TABLE` compared with CoreFoundry's) → start the API, register, login, CRUD a book.
   (Slow: tagged so it can be run separately.)
6. **Web**: "Export code" button, states (nothing applied, unapplied changes), download.
7. **Docs**: README, PROGRESS, this checklist, decisions.
8. **Docker check (only if you want it):** `docker compose up` on the exported Bookshop. Docker isn't installed
   locally yet (D10), so this needs your OK to install it, or can wait.

## 5. Out of scope for this phase (roadmap)
- Test project in the export · GitHub push · other stacks (Node) · exporting row data · refresh tokens in the
  generated auth · regenerating into an existing, user-modified codebase (the export is a starting point).

## 6. Definition of done
- [ ] Bookshop (authors, books with a reference, every type somewhere) exports as a zip from the UI
      (the endpoint and the zip are tested; the button hasn't been clicked in a browser yet)
- [x] The unzipped solution builds with no warnings, its migration creates the same tables as CoreFoundry's apply,
      and register → login → CRUD works against it
- [ ] The README inside the zip is enough to run it locally and with Docker (the built API is run by the test, not by following the README; Docker not run: not installed)
- [x] All CoreFoundry tests pass (709)
