# Next session plan: finish M3, then M4 (Data API)

_Written 2026-09-25 at the end of the M3 build session. Read this first, then `docs/PROGRESS.md`
and `docs/phases/phase-4-data-api.md`._

---

## 0. Where things stand

| Branch | State | Pushed? |
|---|---|---|
| `main` | M0 (PR #1) | yes |
| `m1-models` | M1: auth, projects, members, dashboard | yes |
| `m2-table-designer` | M2 table designer + M2.5 relations/diagram (on top of `m1-models`) | **no** |
| `m3-schema-engine` | M3 schema engine, steps 1–7 done (on top of `m2-table-designer`), HEAD `ef11511` | **no** |

- Working tree is clean. The user pushes and opens PRs themselves.
- Tests: **402 unit + 90 integration pass**, web `lint` and `build` clean.
- Coverage gate in CI: `SchemaDiffer` 97.2%, `MySqlSqlRenderer` 96.6% (target ≥ 90%, `build/check-coverage.py`).
- Nothing is running (API and `next dev` were stopped).

### M3 commits on `m3-schema-engine`
| Commit | Change |
|---|---|
| `6e60a48` | Schema model + `SchemaDiffer` (45 table-driven tests), decisions D26–D27 |
| `10652be` | `MySqlSqlRenderer` + coverage gate in CI |
| `39d335e` | `MySqlSchemaIntrospector`, `DraftSchema` mapping, `cf_engine` gets `REFERENCES` |
| `fd89a14` | `SchemaPlanService`, `SchemaApplier`, `Changed` state |
| `8fd0724` | `SchemaController` + problem types, D28 (non-unique migration version) |
| `284218b` | Web: Review plan, History, drift banner |
| `ef11511` | Fix: apply outcome stays visible after the plan refreshes |

### Rules for working with this user (from memory; still apply)
- **Ask before** environment/tooling choices (new packages, Docker, ports, stopping their processes, DB grants).
- Commit **locally** on the feature branch, **never push**, **no `Co-Authored-By` trailer**.
- Use the plan-before-execute workflow: show the plan, do one verified step at a time, announce any change of plan.
- **Stop at the end of a phase** and wait.
- Never store passwords (the MySQL root password was shared once in chat; it's not saved anywhere and must not be).

### Environment facts
- Local MySQL 9.2 on 3306 (Windows service `MySQL92`), no Docker. Accounts: `cf_meta` (only `corefoundry`,
  `corefoundry_test`), `cf_engine` (only `cf_p\_%`, now **with REFERENCES**). Secrets in user-secrets `corefoundry-api-dev`.
- API: `dotnet run --project src/CoreFoundry.Api --launch-profile http` → `http://localhost:5172`.
- Web: `cd web && npm run dev` → `http://localhost:3100` (proxies `/api/*` to 5172).
- The user sometimes has their own API / `next dev` running: **ask before stopping them**. If the API's Debug
  output is locked, build/`dotnet ef` with `--configuration Release`, but **never** run
  `database update --no-build` right after `migrations add` (the stale DLL doesn't contain the new migration).
- `mysql.exe`: `C:/Program Files/MySQL/MySQL Server 9.2/bin/mysql.exe`.
- Long inline bash heredocs containing Python sometimes fail to parse in this harness: write the script to the
  scratchpad and run it.
- Web code: Next 16; read `web/node_modules/next/dist/docs/` for anything new. Client pages use `useParams`.
- Browser checks: the Chrome extension usually isn't connected; the approved fallback is `puppeteer-core`
  installed in the session scratchpad, driving `C:/Program Files/Google/Chrome/Application/chrome.exe` headless.
  Tips: don't use `networkidle0` with `next dev`; `bringToFront()` before using a second tab; clear inputs with
  real key presses (Locator `fill("")` doesn't notify react-hook-form).

---

## Part A — Finish M3 (step 9: docs) · small

The user skipped the rest of the browser check. 15/16 checks had passed (apply + `SHOW CREATE TABLE`, rename
keeps data, forced failure journaled and shown in History); the 16th failed on a script bug
(`page.locator(...).first` doesn't exist in puppeteer), not the app. **Only re-run it if the user asks.**

### A1. `docs/PROGRESS.md`
- [ ] Header: branch `m3-schema-engine`; M3 row ✅ Done, M4 row ⏭ Next
- [ ] Test line: 402 unit + 90 integration (count the MySQL-backed ones: `SkipIfUnavailable` tests), coverage numbers
- [ ] New **M3 — Schema engine** section:
  - Components: `SchemaModel`, `SchemaDiffer`, `MySqlSqlRenderer`, `MySqlSchemaIntrospector`, `DraftSchema`,
    `SchemaPlanService`, `SchemaApplier`, `MySqlSchemaEngine` (GET_LOCK session), `SchemaSnapshot`, `PlanHash`
  - Endpoints (below) and problem types
  - UI: Review plan, History, drift banner, `Changed` badge
  - How it's verified: differ/renderer unit tests, applier unit tests with fakes, engine round-trip tests on real
    MySQL (next diff always empty), API integration tests, partial browser run
- [ ] Changes from the plan:
  - D26 local MySQL instead of Testcontainers; D27 coverage extension; D28 non-unique `(ProjectId, Version)`
  - `DraftSchemaReader` became a pure `DraftSchema.From(tables)` in Application
  - Unique keys found by column (any single-column unique index), renamed with `RENAME INDEX` when names drift
  - Unique/foreign keys on columns created in the same plan are `Safe`
  - `cf_engine` needed `REFERENCES` (setup script updated, grant applied locally)
- [ ] **Known gaps** (add all of these):
  - Column order changes in the designer aren't applied to MySQL (new columns are appended; `CREATE` uses the order)
  - Removing a reference leaves the FK's supporting index (`fk_…`) in MySQL
  - Swapping two table names in one plan (a→b, b→a) would fail (renames run one by one)
  - If someone edits the draft while an apply runs, the final metadata save can hit a version conflict: MySQL is
    changed but the journal row stays `Pending`. Re-planning recovers (name fallback), but the row isn't cleaned up
  - Plan warnings count NULLs / rows per risky operation (one query each), fine for small schemas
  - Drift compares with the snapshot of the last *successful* apply
- [ ] Commits table for `m3-schema-engine`

### A2. `README.md`
- [ ] Add the schema endpoints to "API so far":
  | GET | `/api/projects/{id}/schema/plan` | Developer | `{ planHash, schemaVersion, operations, statements, warnings, unmanaged… }` |
  | POST | `/api/projects/{id}/schema/apply` | Admin | `{ planHash, acknowledgeDestructive }` → 200 / 409 plan-stale / 409 apply-in-progress / 422 / 500 apply-failed |
  | GET | `/api/projects/{id}/schema/migrations?page=&pageSize=` | Developer | newest first |
  | GET | `/api/projects/{id}/schema/migrations/{migrationId}` | Developer | statements, status, error |
  | GET | `/api/projects/{id}/schema/drift` | Developer | changes made outside CoreFoundry |
- [ ] Short "How applying works" paragraph (lock, hash, journal, re-plan after failure)
- [ ] Mention `db/setup-local.sql` now grants `REFERENCES` (existing installs: one `GRANT REFERENCES …` as root)

### A3. `docs/phases/phase-3-schema-engine.md`
- [ ] Tick every item that's done, add notes where the build differs (see "Changes from the plan"), mark ✅
- [ ] `docs/phases/README.md`: M3 ✅

### A4. Verify and commit
- [ ] `dotnet test` (all), `cd web && npm run lint && npm run build`
- [ ] Commit: "Document M3: progress, README, phase checklist" — then **stop and tell the user M3 is done**

---

## Part B — M4: Data API + data viewer

Start only after the user says so. Spec: `docs/phases/phase-4-data-api.md`. Below is how it maps onto the code as
it is now, plus the decisions to ask about first.

### B0. Ask the user first
1. **Branch:** new `m4-data-api` off `m3-schema-engine` (recommended) or keep going on `m3-schema-engine`.
2. **Reference columns in the row form** (e.g. `books.author_id`): plain number input validated by the FK
   (simple), or a picker listing the referenced table's rows (nicer; needs a small lookup endpoint). Recommend the picker.
3. **Dapper** is already in `Directory.Packages.props` but not referenced by Infrastructure yet: confirm using it
   (the spec says Dapper) vs. plain `MySqlCommand` (no new reference). Recommend Dapper as planned.

### B1. Important design notes (read before coding)
- **Snapshot type mismatch.** The spec says `SnapshotProvider` returns a `SchemaModel`, but what M3 stores in
  `SchemaMigrations.SnapshotJson` is a `SchemaSnapshot`: plain strings (`Type = "Varchar(200)"`, `"Decimal(10,2)"`,
  raw MySQL types for unknown ones; `Default` = canonical text; `References` = physical table name; `OnDelete`).
  → Add `ColumnType.TryParse(string)` in Domain (inverse of `ColumnType.ToString()`), and have the provider build a
  typed per-table model from the snapshot. Columns whose type doesn't parse (drift / unmanaged types) are
  **read-only** in the Data API (returned as strings, rejected on write).
- **Unmanaged columns and the system `id`:** the snapshot doesn't contain `id` (it's implicit). Reads always select
  `id` first. Unmanaged columns aren't in the snapshot, so they're invisible to the Data API (document it).
- **Cache key** `snapshot:{projectId}:{schemaVersion}`: every successful apply bumps `Projects.SchemaVersion`, so a
  new version means a new key and old entries simply expire. The service loads the project once per request
  (the authorization policy only reads the member's role) and uses its `SchemaVersion`.
- **Identifier safety:** `{table}`, `sort` and JSON keys are only ever **looked up** in the snapshot; the SQL uses
  the snapshot's names through `MySqlSqlRenderer.Quote(Identifier.Of(...))`. Values are always parameters
  `@p_<ordinal>`.
- **Decimals as strings** in JSON responses; `BIGINT id` as a number. DateTime returned as ISO-8601 without offset
  (the column has no time zone; values with an offset on input are converted to UTC, per spec).
- **FK errors** (not in the spec's table, needed since M2.5): MySQL **1452** (parent row missing) → 400 on that field
  ("No authors row with id 42."); **1451** (row is referenced, RESTRICT) → 409 ("Other rows reference this one:
  books.author_id."). Add both to the error mapping.
- **Existing error mapping to reuse:** `ApiExceptionHandler` (400/404/409/422/500 + problem types). Add a
  `DataConflictException` or reuse `ConflictException`.

### B2. Steps (plan-before-execute; commit after each verified step)

1. **Branch + snapshot provider**
   - `ColumnType.TryParse` (+ unit tests for every type and unknown strings)
   - Application `DataSchema` (typed table/column view of a snapshot) + `ISnapshotProvider` with
     `IMemoryCache` keyed by version; unit tests (cache hit, new version = new model, no applied migration → no tables)
2. **Value coercion (Domain or Application, pure)**
   - `JsonElement` → typed value per column type, all errors collected per field; every row of spec §4 plus edge
     values (INT min/max, BIGINT, decimal scale overflow, leap day, invalid UUID, `null` on NOT NULL, unknown field,
     `id` in body, missing required field on POST). Table-driven tests.
3. **Query builder (Infrastructure, pure)**
   - SELECT with ORDER BY (+ `id` tie-breaker), LIMIT/OFFSET, COUNT, INSERT, UPDATE (full replace), DELETE, all from
     resolved models; `@p_<ordinal>` parameters. Snapshot tests of the SQL; unknown sort → error.
4. **Data repository (Infrastructure, Dapper on `cf_engine`)**
   - Execute the built queries, read rows into `Dictionary<string, object?>`, map MySQL types back (decimal → string,
     `tinyint(1)` → bool, `json` → raw JSON, dates → ISO strings); `LAST_INSERT_ID()` then re-read.
   - MySQL error mapping: 1062, 1048, 1406, 1146, **1451, 1452**, else 500.
5. **`DataService` (Application) + `DataController` (API)**
   - The 5 endpoints of spec §2 (Developer), `page`/`pageSize`/`sort` validation, 404 for unknown/unapplied tables.
   - Integration tests: CRUD round trip on applied `books`; pagination + sort stability; unique → 409; FK parent
     missing → 400, referenced row delete → 409; draft-only column → 400 unknown field; rename `price` → `price_usd`
     then the API uses the new name at once; injection attempts in `{table}`, `sort`, JSON keys → 404/400.
6. **Web: data viewer**
   - `/projects/[id]/data/[table]` with a table picker (applied tables only), paginated grid, sortable headers,
     cells formatted by type, side panel form generated from the snapshot (required marks, server errors per field),
     delete with in-page confirmation, empty and "not applied yet → Review plan" states.
   - Links: project page Schema card and table designer ("Browse data" for applied tables).
7. **Docs**: README curl examples with a Bearer token (register/login → token → list/insert/update/delete),
   PROGRESS.md M4 section, phase-4 checklist, decisions log entries for any changes.
8. **Browser check of the Definition of done — only if the user wants it** (they skipped it last time).

### B3. Definition of done (from the spec)
- In Bookshop: add authors and books from the UI, sort by price, page through, edit, delete
- The same with `curl` + Bearer token, documented in the README
- All tests pass

---

## Start-here prompt for the new session

> Read `docs/NEXT-SESSION-PLAN.md`, `docs/PROGRESS.md` and my memory notes. Do Part A (finish M3 docs) on
> `m3-schema-engine`, commit, and stop. Then wait for me before starting Part B (M4).
