# M3 — Schema engine ⭐

**Goal:** turn the draft into real MySQL tables safely. **Plan** compares the draft
with the real schema and shows the exact SQL. **Apply** runs it with a lock and
a journal. History and drift are visible. This phase is the reason the project exists.

**Depends on:** M2. **Protect this phase** if time runs short.

Diagrams: [plan pipeline and apply state machine](../corefoundry-flows.html#plan).

---

## 1. Components

| Component | Layer | Pure? | Responsibility |
|---|---|---|---|
| `SchemaModel` | Domain | ✔ | Immutable records describing a database: tables → columns |
| `DraftSchemaReader` | Infrastructure | ✘ | EF query → desired `SchemaModel` |
| `SchemaIntrospector` | Infrastructure | ✘ | `INFORMATION_SCHEMA` → actual `SchemaModel` |
| `SchemaDiffer` | Domain | ✔ | `(desired, actual)` → `IReadOnlyList<SchemaOperation>` |
| `SqlRenderer` | Infrastructure | ✔ | operations → DDL strings (MySQL dialect) |
| `PlanService` | Application | ✘ | orchestrates the above, computes `planHash` |
| `SchemaApplier` | Application | ✘ | lock, journal, execute, commit metadata |

`SqlRenderer` lives in Infrastructure because it is MySQL-specific, but it
does no I/O and is unit-tested like Domain code.

---

## 2. SchemaModel

```csharp
public sealed record SchemaModel(IReadOnlyList<TableModel> Tables);

public sealed record TableModel(
    long? MetadataId,          // null for tables found only in the real DB
    string Name,               // desired name (draft) or physical name (actual)
    string? AppliedName,       // draft only: current physical name
    bool PendingDrop,
    IReadOnlyList<ColumnModel> Columns);

public sealed record ColumnModel(
    long? MetadataId, string Name, string? AppliedName, bool PendingDrop,
    ColumnType Type,           // DataType + Length/Precision/Scale
    bool IsNullable, bool IsUnique, DefaultValue? Default);
```

### Introspection queries (as `cf_engine`)
```sql
SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES
 WHERE TABLE_SCHEMA = @db AND TABLE_TYPE = 'BASE TABLE';

SELECT TABLE_NAME, COLUMN_NAME, ORDINAL_POSITION, COLUMN_TYPE, DATA_TYPE,
       IS_NULLABLE, COLUMN_DEFAULT, CHARACTER_MAXIMUM_LENGTH,
       NUMERIC_PRECISION, NUMERIC_SCALE, EXTRA
  FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = @db;

SELECT TABLE_NAME, INDEX_NAME, NON_UNIQUE, COLUMN_NAME, SEQ_IN_INDEX
  FROM INFORMATION_SCHEMA.STATISTICS WHERE TABLE_SCHEMA = @db;
```
- [ ] Map `COLUMN_TYPE` back to `ColumnType` (`tinyint(1)` → Bool,
      `char(36)` → Uuid, `datetime(6)` → DateTime …). Anything that doesn't map
      becomes `ColumnType.Unknown(raw)` and shows up as drift.
- [ ] A column counts as unique only if a single-column unique index named
      `uq_<table>_<column>` exists

---

## 3. Differ algorithm

Input: desired (draft) and actual (real). Tables are matched on
`draft.AppliedName == actual.Name` and columns the same way.

1. **Tables to drop:** draft tables with `PendingDrop` and an `AppliedName` that exists in actual → `DropTable` *(destructive)*
2. **Tables to create:** draft tables with `AppliedName == null`, or whose `AppliedName`
   is missing from actual (recovery case) → `CreateTable` with every column and unique key
3. **Tables to rename:** `Name != AppliedName` → `RenameTable`
4. **For each matched table**, compare columns:
   - `PendingDrop` → `DropColumn` *(destructive)*
   - no `AppliedName`, or `AppliedName` missing from actual → `AddColumn`
   - `Name != AppliedName` → `RenameColumn`
   - definition differs → `ModifyColumn`, destructive if narrowing (see below)
   - unique added/removed → `AddUniqueKey` / `DropUniqueKey`
5. **Actual tables with no draft match** → reported as **unmanaged**. They are
   **never** dropped automatically.
6. Output order: drops first, then renames, creates and alters. Operations
   for one table are grouped so the renderer can combine them.

### Destructive / risky rules
| Change | Flag |
|---|---|
| Drop table / column | `Destructive` |
| Varchar length decreases, Decimal precision/scale decreases | `Destructive` (possible truncation) |
| Type family change (e.g. Varchar → Int) | `Destructive` |
| NULL → NOT NULL | `Risky` (fails if NULLs exist; plan shows `SELECT COUNT(*) … IS NULL` result) |
| Add NOT NULL column without default to a non-empty table | `Risky` (MySQL fills in the type's implicit default) |

The UI requires an extra "I understand" checkbox when any `Destructive` flag is present.

---

## 4. SQL rendering

- [ ] Identifiers go through a single `Quote(Identifier id)` that only accepts
      an `Identifier` value object (already validated). There's no overload that
      takes a raw `string`.
- [ ] One `CREATE TABLE` per new table:
  ```sql
  CREATE TABLE `cf_p_7`.`books` (
    `id` BIGINT NOT NULL AUTO_INCREMENT,
    `title` VARCHAR(200) NOT NULL,
    `price_usd` DECIMAL(10,2) NOT NULL DEFAULT 0.00,
    PRIMARY KEY (`id`),
    UNIQUE KEY `uq_books_title` (`title`)
  ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
  ```
- [ ] One `ALTER TABLE` per existing table, with every clause separated by commas:
      `RENAME COLUMN`, `ADD COLUMN`, `MODIFY COLUMN`, `DROP COLUMN`,
      `ADD UNIQUE KEY`, `DROP INDEX`
- [ ] `RENAME TABLE` is a separate statement and runs before that table's `ALTER`
- [ ] Defaults are written from the typed `DefaultValue`: numbers invariant-culture,
      strings escaped as `'` → `''` and `\` → `\\`, and the keywords
      `CURRENT_TIMESTAMP(6)` / `(UUID())` for the function defaults
- [ ] `planHash = SHA-256(SchemaVersion + "\n" + string.Join("\n", statements))`, hex

---

## 5. Apply

### Endpoint contract
```
POST /api/projects/{projectId}/schema/apply        (Admin)
{ "planHash": "9f2c…", "acknowledgeDestructive": true }

200 { migrationId, version, status: "Applied", statements: 3 }
409 { type: ".../plan-stale" }         draft or schema changed since the plan was made
409 { type: ".../apply-in-progress" }  someone else holds the lock
422 { type: ".../destructive-not-acknowledged" }
500 { type: ".../apply-failed", migrationId, failedStatement, error }
```

### Steps
1. Open a **dedicated** `cf_engine` connection and keep it open for the whole
   apply. `GET_LOCK` belongs to the session: it must be taken and released on
   the same connection.
2. `SELECT GET_LOCK('cf_apply_{projectId}', 0)`. If it returns 0 → 409 apply-in-progress.
3. Build the plan again and compare its hash with the one the client sent. If they differ → release the lock, 409 plan-stale.
4. Insert `SchemaMigrations { Status = Pending, Version = SchemaVersion + 1, StatementsJson }` (via `cf_meta`).
5. For each statement `i`: execute it, then update `StatementsApplied = i`.
6. **All succeeded** → one EF transaction:
   - set `AppliedName = Name` on every applied table and column
   - delete the `PendingDrop` rows that were dropped
   - `Projects.SchemaVersion += 1`
   - `SnapshotJson` = the model introspected **after** apply (the truth)
   - `Status = Applied`, `CompletedAt = now`
7. **A statement threw** → `Status = Failed`, `Error`, keep `StatementsApplied`.
   Metadata is left alone. The next plan re-diffs against reality and proposes only what's left.
8. `finally`: `SELECT RELEASE_LOCK(...)`, dispose the connection.

> Note: if statement 1 of 3 succeeds and 2 fails, the real DB is half changed but
> **consistent per table**, because each statement is atomic. Recovery is "plan again",
> which is safe because the differ always compares against `INFORMATION_SCHEMA`.

---

## 6. Other endpoints

| Method | Route | Min role | Returns |
|---|---|---|---|
| GET | `/api/projects/{projectId}/schema/plan` | Developer | `{ planHash, schemaVersion, operations[], statements[], warnings[], unmanaged[] }` |
| GET | `/api/projects/{projectId}/schema/migrations` | Developer | paged history, newest first |
| GET | `/api/projects/{projectId}/schema/migrations/{id}` | Developer | statements, status, error |
| GET | `/api/projects/{projectId}/schema/drift` | Developer | differences between the last `SnapshotJson` and `INFORMATION_SCHEMA` |

---

## 7. Frontend

- [ ] **Review plan** page: grouped by table, each operation as a coloured row
      (add green, rename amber, drop red), SQL in a code block with a copy button,
      and warnings at the top
- [ ] Apply button, visible only to Admin and Owner. It needs the destructive
      checkbox when a destructive change is present. A progress state is shown during the request.
- [ ] Clear 409 messages: "The draft changed since you reviewed it. Review again."
      and "Someone else is applying changes to this project."
- [ ] **History** page: list of migrations with status and expandable SQL. A failed
      migration shows the statement that failed and the error.
- [ ] Drift banner on the project page when `/schema/drift` isn't empty

---

## 8. Tests

**Unit: `SchemaDiffer` (table-driven, aim for full branch coverage)**
- [ ] Empty → one table = one `CreateTable`
- [ ] Rename column keeps its data (a `RenameColumn` op, not drop + add)
- [ ] Rename table + rename column in the same plan
- [ ] PendingDrop column → `DropColumn` flagged destructive
- [ ] Varchar 200 → 50 flagged, 50 → 200 not flagged
- [ ] Unmanaged table in actual → reported, no drop
- [ ] Recovery: draft says applied but the column is missing in actual → `AddColumn`
- [ ] No changes → empty plan, and the same hash on repeated calls

**Unit: `SqlRenderer`**
- [ ] Snapshot tests of the rendered SQL for every operation type
- [ ] Default escaping: `O'Reilly`, `back\slash`
- [ ] Several ops on one table → exactly one `ALTER TABLE`

**Integration (real MySQL via Testcontainers)**
- [ ] Design → plan → apply → `INFORMATION_SCHEMA` matches the draft
- [ ] Round trip: after apply, the next plan is empty
- [ ] Rename a column that has data → the data survives
- [ ] Failure mid-apply: add a statement that fails on purpose (e.g. make a unique key
      conflict with duplicate rows) → `Failed`, `StatementsApplied` correct, re-plan
      proposes only the rest, second apply succeeds
- [ ] Two applies at once → one returns 409 apply-in-progress
- [ ] Stale hash → 409 plan-stale
- [ ] Developer calling apply → 403
- [ ] Security: even if a crafted identifier got past validation (bypass validation in the test),
      `Quote` rejects it (defense in depth)

---

### Foreign keys (from M2.5, D25)
- [ ] Constraint names `fk_<table>_<column>`, shortened with a hash when over 64 characters
- [ ] Order: create tables → add/modify columns → add constraints; drop constraints before
      dropping or modifying their columns and before dropping or renaming tables they block
- [ ] Introspect `INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS` + `KEY_COLUMN_USAGE`; diff on-delete changes as drop + add constraint
- [ ] Tests: create `authors` + `books` with a reference in one plan; drop `authors` with `books` pending drop in the same plan; change `Cascade` → `SetNull`

## 9. Definition of done
- [ ] Bookshop: design `authors` + `books`, review the plan, apply, then see the tables in
      MySQL Workbench / `SHOW CREATE TABLE`
- [ ] Rename `price` → `price_usd` with rows present, apply, and no data is lost
- [ ] Force a failure, see it in History, re-plan, apply successfully
- [ ] Unit coverage of `SchemaDiffer` + `SqlRenderer` ≥ 90% (report in CI)

## 10. Interview talking points
- Why EF migrations can't do this (the schema is defined at runtime by end users)
- MySQL commits DDL immediately → atomic single statements + a journal instead of a transaction
- Diffing against `INFORMATION_SCHEMA` rather than trusting stored state → self-healing recovery
- Stable ids + `AppliedName` → rename detection
- `planHash`: what you reviewed is what runs
- Session-scoped `GET_LOCK` and why the connection must stay pinned
- Identifier safety: validated value objects, a single quoting function, no raw-string overload
