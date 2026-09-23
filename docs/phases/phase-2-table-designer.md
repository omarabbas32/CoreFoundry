# M2 — Table designer (draft only)

**Goal:** users can design tables and columns in the UI. Every name, type and
default is validated as strictly as the schema engine will need, but **nothing
touches MySQL** yet. Everything is saved as draft metadata.

**Depends on:** M1.

---

## 1. Data

Tables: `ProjectTables`, `ProjectColumns` (see the [data model](../corefoundry-erd.html)).

- [ ] Entities and EF configurations, migration `AddTableDesigner`
- [ ] `UQ (ProjectId, Name)` and `UQ (TableId, Name)`
- [ ] `DataType` enum stored as `TINYINT`:
      `Int=1, BigInt=2, Decimal=3, Bool=4, Varchar=5, Text=6, DateTime=7, Date=8, Json=9, Uuid=10`
- [ ] `ProjectTables.RowVersion` (or `UpdatedAt` as the concurrency token) so
      two people editing the same table get a 409 instead of silently overwriting each other

---

## 2. Validation (the rules the engine will depend on)

These live in **Domain** as pure code, so M3 reuses them without changes.

### Identifiers: `IdentifierRules`
- [ ] Regex `^[a-z][a-z0-9_]{0,62}$`
- [ ] Not a MySQL 8.4 reserved word. Ship the list as an embedded resource
      generated from `INFORMATION_SCHEMA.KEYWORDS WHERE RESERVED = 1` (commit the
      script used to generate it).
- [ ] Not reserved by CoreFoundry: `id`, and anything starting with `cf_`
- [ ] Case: names are stored in lower case, so `Books` and `books` can't both exist

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

- [ ] Fields that don't apply to the chosen type must be null (for example `Length` on an `Int`)
- [ ] `DefaultValue` is parsed into a typed value object. The M3 renderer works
      from that parsed value and never from the raw string.
- [ ] Limits: max 50 tables per project, 100 columns per table (stated in the README)
- [ ] Row-size guard: the sum of Varchar lengths × 4 bytes (utf8mb4) must be under
      MySQL's 65,535-byte row limit, returned as a clear validation error

---

## 3. Draft semantics (what edit and delete mean before Apply)

| Action | Never applied (`AppliedName` null) | Already applied |
|---|---|---|
| Rename table/column | update `Name` | update `Name` (M3 turns this into a rename) |
| Change type/nullable/etc. | update fields | update fields (M3 turns this into a modify) |
| Delete | **hard delete** the row | set `PendingDrop = true` (reversible until Apply) |
| Undo delete | — | `PendingDrop = false` |

- [ ] A name has to be unique among **all** rows in the scope, including
      `PendingDrop` rows. This avoids "drop `x` and add a new `x`" in the same apply.
- [ ] Deleting a table with `PendingDrop` also marks its columns (UI shows them as struck through)

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

- [ ] Every query filters by `projectId`, including the `tableId` → `projectId` check.
      A table from another project returns 404 even if its id exists.
- [ ] Each table in responses includes a `state` so the UI can show a badge:
      `new` (never applied), `changed` (differs from applied), `pendingDrop`, `applied`.
      `changed` compares against the latest snapshot once M3 exists; until then
      it's `new` or `applied` only.
- [ ] Validation errors → 400 `ValidationProblemDetails` keyed by field
      (`columns[2].length`)

---

## 5. Frontend

- [ ] `/projects/[id]/tables`: list of tables with state badges and a "New table" action
- [ ] `/projects/[id]/tables/[tableId]`: designer
  - [ ] Read-only `id BIGINT PK` system row at the top
  - [ ] Columns grid: name, type, length / precision / scale (shown only when
        they apply), nullable, unique, default
  - [ ] Validation runs in the browser too (zod schemas that mirror the Domain rules),
        but the server's answer is final
  - [ ] Drag to reorder (writes `OrdinalPosition`)
  - [ ] Deleted columns show struck through with an "Undo" action
- [ ] Banner: "Draft changes aren't applied yet" (links to Review plan in M3)

---

## 6. Tests

**Unit (most of the value is here)**
- [ ] Identifier rules: valid names, `1abc`, `a-b`, `` a`b ``, `a;drop`, 64-char
      and 65-char names, reserved words (`select`, `order`), `id`, `cf_x`, uppercase input
- [ ] Every row of the type table: valid and invalid length/precision/default
- [ ] Row-size guard at the boundary

**Integration**
- [ ] Deleting a never-applied column removes it. Deleting an applied column
      sets `PendingDrop` (simulate by setting `AppliedName` directly in the database).
- [ ] A table from another project → 404
- [ ] Concurrent edit → 409

---

## 7. Definition of done
- [ ] In Bookshop, a user can design `authors` and `books` with a mix of types,
      reorder columns, delete one and undo it
- [ ] Every example of an invalid name from the unit tests is rejected by the API with a clear message
- [ ] No SQL is sent to `cf_p_*` databases during this phase (check the logs)

## 8. Interview talking points
- Why identifier validation lives in Domain (the engine's safety depends on it)
- Hard delete vs `PendingDrop`, and why draft deletes must be reversible
- Why validation runs in the browser **and** on the server
