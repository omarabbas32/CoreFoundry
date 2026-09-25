# M2 — Table designer (draft only) ✅

**Goal:** users can design tables and columns in the UI. Every name, type and
default is validated as strictly as the schema engine will need, but **nothing
touches MySQL** yet. Everything is saved as draft metadata.

**Depends on:** M1.

---

## 1. Data

Tables: `ProjectTables`, `ProjectColumns` (see the [data model](../corefoundry-erd.html)).

- [x] Entities and EF configurations, migration `AddTableDesigner`
- [x] `UQ (ProjectId, Name)` and `UQ (TableId, Name)`
- [x] `DataType` enum stored as `TINYINT`:
      `Int=1, BigInt=2, Decimal=3, Bool=4, Varchar=5, Text=6, DateTime=7, Date=8, Json=9, Uuid=10`
- [x] `ProjectTables.RowVersion` (or `UpdatedAt` as the concurrency token) so
      two people editing the same table get a 409 instead of silently overwriting each other
      → `Version` int, bumped by every table or column change (D20)

---

## 2. Validation (the rules the engine will depend on)

These live in **Domain** as pure code, so M3 reuses them without changes.

### Identifiers: `IdentifierRules`
- [x] Regex `^[a-z][a-z0-9_]{0,63}$` (64 characters, see D18)
- [x] Not a MySQL 8.4 reserved word. Ship the list as an embedded resource
      generated from `INFORMATION_SCHEMA.KEYWORDS WHERE RESERVED = 1` (commit the
      script used to generate it).
- [x] Not reserved by CoreFoundry: `id`, and anything starting with `cf_`
- [x] Case: names are stored in lower case, so `Books` and `books` can't both exist

### Column definitions: `ColumnDefinitionRules`
| DataType | Length | Precision / Scale | Default allowed | Default format |
|---|---|---|---|---|
| Int | — | — | yes | integer in INT range |
| BigInt | — | — | yes | integer in BIGINT range |
| Decimal | — | P 1–65, S 0–30, S ≤ P | yes | decimal that fits (P,S) |
| Bool | — | — | yes | `true` / `false` |
| Varchar | 1–4000 | — | yes | string, length ≤ n |
| Text | — | — | **no** | — |
| DateTime | — | — | yes | ISO-8601 or `CURRENT_TIMESTAMP` |
| Date | — | — | yes | `yyyy-MM-dd` |
| Json | — | — | **no** | — |
| Uuid | — | — | yes | UUID or `UUID()` |

- [x] Fields that don't apply to the chosen type must be null (for example `Length` on an `Int`)
- [x] `DefaultValue` is parsed into a typed value object. The M3 renderer works
      from that parsed value and never from the raw string.
- [x] Limits: max 50 tables per project, 100 columns per table (stated in the README)
- [x] Row-size guard: the sum of Varchar lengths × 4 bytes (utf8mb4) must be under
      MySQL's 65,535-byte row limit, returned as a clear validation error
      → implemented as MySQL's exact row-size formula, plus UNIQUE limits (D19)

---

## 3. Draft semantics (what edit and delete mean before Apply)

| Action | Never applied (`AppliedName` null) | Already applied |
|---|---|---|
| Rename table/column | update `Name` | update `Name` (M3 turns this into a rename) |
| Change type/nullable/etc. | update fields | update fields (M3 turns this into a modify) |
| Delete | **hard delete** the row | set `PendingDrop = true` (reversible until Apply) |
| Undo delete | — | `PendingDrop = false` |

- [x] A name has to be unique among **all** rows in the scope, including
      `PendingDrop` rows. This avoids "drop `x` and add a new `x`" in the same apply.
- [x] Deleting a table with `PendingDrop` also marks its columns (UI shows them as struck through)
      → reported as `PendingDrop` while the table is, without changing the column rows (D21)

---

## 4. Endpoints

| Method | Route | Min role |
|---|---|---|
| GET | `/api/projects/{projectId}/tables` | Developer |
| GET | `/api/projects/{projectId}/tables/{tableId}` | Developer |
| POST | `/api/projects/{projectId}/tables` | Developer |
| PUT | `/api/projects/{projectId}/tables/{tableId}` | Developer |
| DELETE | `/api/projects/{projectId}/tables/{tableId}` | Developer |
| POST | `/api/projects/{projectId}/tables/{tableId}/restore` | Developer |
| POST | `/api/projects/{projectId}/tables/{tableId}/columns` | Developer |
| PUT | `/api/projects/{projectId}/tables/{tableId}/columns/{columnId}` | Developer |
| DELETE | `/api/projects/{projectId}/tables/{tableId}/columns/{columnId}` | Developer |
| POST | `/api/projects/{projectId}/tables/{tableId}/columns/{columnId}/restore` | Developer |
| PUT | `/api/projects/{projectId}/tables/{tableId}/columns/order` | Developer |

- [x] Every query filters by `projectId`, including the `tableId` → `projectId` check.
      A table from another project returns 404 even if its id exists.
- [x] Each table in responses includes a `state` so the UI can show a badge:
      `new` (never applied), `changed` (differs from applied), `pendingDrop`, `applied`.
      `changed` compares against the latest snapshot once M3 exists; until then
      it's `new` or `applied` only.
- [x] Validation errors → 400 `ValidationProblemDetails` keyed by field
      (`columns[2].length`)

---

## 5. Frontend

- [x] `/projects/[id]/tables`: list of tables with state badges and a "New table" action
- [x] `/projects/[id]/tables/[tableId]`: designer
  - [x] Read-only `id BIGINT PK` system row at the top
  - [x] Columns grid: name, type, length / precision / scale (shown only when
        they apply), nullable, unique, default
  - [x] Validation runs in the browser too (zod schemas that mirror the Domain rules),
        but the server's answer is final
  - [x] Drag to reorder (writes `OrdinalPosition`)
  - [x] Deleted columns show struck through with an "Undo" action
        (never-applied columns are hard-deleted; the designer offers an Undo that re-adds them, D22)
- [x] Banner: "Draft changes aren't applied yet" (the Review plan link arrives with M3)

---

## 6. Tests

**Unit (most of the value is here)**
- [x] Identifier rules: valid names, `1abc`, `a-b`, `` a`b ``, `a;drop`, 64-char
      and 65-char names, reserved words (`select`, `order`), `id`, `cf_x`, uppercase input
- [x] Every row of the type table: valid and invalid length/precision/default
- [x] Row-size guard at the boundary

**Integration**
- [x] Deleting a never-applied column removes it. Deleting an applied column
      sets `PendingDrop` (simulate by setting `AppliedName` directly in the database).
- [x] A table from another project → 404
- [x] Concurrent edit → 409

---

## 7. Definition of done
- [x] In Bookshop, a user can design `authors` and `books` with a mix of types,
      reorder columns, delete one and undo it
- [x] Every example of an invalid name from the unit tests is rejected by the API with a clear message
- [x] No SQL is sent to `cf_p_*` databases during this phase (check the logs)

## 8. Interview talking points
- Why identifier validation lives in Domain (the engine's safety depends on it)
- Hard delete vs `PendingDrop`, and why draft deletes must be reversible
- Why validation runs in the browser **and** on the server
