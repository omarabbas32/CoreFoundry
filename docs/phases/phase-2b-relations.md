# M2.5 — Relations and schema diagram (draft only) ✅

**Goal:** a column can reference another table (a many-to-one foreign key), and the
project's draft schema can be seen as a diagram with relation lines. Like M2, this only
edits draft metadata: **nothing touches MySQL**. M3 creates the real constraints.

**Depends on:** M2. **Replaces** plan decision D6 ("foreign keys: roadmap only") with D25.

---

## 1. Scope

- Many-to-one references from a column to another table's `id` (or its own table's).
  One-to-one = a reference column that is also UNIQUE. Many-to-many = a join table.
- On delete: `Restrict`, `Cascade`, `SetNull`. On update is always `RESTRICT`: ids never change.
- **Not in scope** (roadmap): composite keys, references to columns other than `id`,
  saved diagram positions.

## 2. Data

- [x] `ProjectColumns.ReferencesTableId` (nullable FK → `ProjectTables.Id`) and
      `ProjectColumns.OnDelete` (`TINYINT`, `Restrict=1, Cascade=2, SetNull=3`), migration `AddColumnReferences`
- [x] Both set or both null
- [x] `ReferencesTableId` is a real FK with ON DELETE CASCADE: the Domain refuses to delete a referenced
      table, so the cascade only runs when a whole project is deleted (tested)

## 3. Rules (Domain)

- [x] A reference column is `BigInt` (it holds an `id`), with no length/precision/scale and no default
- [x] `SetNull` requires a nullable column
- [x] The target table is in the same project and not pending drop; self-references are allowed
- [x] A table can't be deleted (hard delete or pending drop) while live columns of **other**
      tables reference it; the error names them (`books.author_id`)
- [x] A column can't be restored, and a table can't be restored, while a reference it holds
      points at a table pending drop
- [x] Renaming the target is fine: references are by id

## 4. API

- [x] Column requests: `referencesTableId`, `onDelete`; responses add `referencesTableName`
- [x] A target from another project (or unknown) → 400 on `referencesTableId`
- [x] `GET /api/projects/{projectId}/schema` (Developer): every table with its columns,
      for the diagram (and the M3 plan)

## 5. Frontend

- [x] Column dialog: "References" picker (none or a table); picking one locks the type to
      BigInt, hides the default and shows "On delete"
- [x] Grid: `BigInt → authors` with the on-delete rule
- [x] `/projects/[id]/tables/diagram`: React Flow canvas, one node per table (columns listed,
      pending drops struck through), an edge from each reference column to its target's `id`,
      auto layout, pan/zoom/drag, click a table to open the designer
      (edges run from a column's left edge to the referenced `id` row's right edge, since referenced tables are laid out to the left)

## 6. Tests

- [x] Unit: every rule in §3
- [x] Integration: create `books.author_id → authors`; wrong type / SetNull on NOT NULL /
      other project's table → 400; deleting `authors` while referenced → 400; after deleting the
      reference column it works; `/schema` returns both tables; the project database stays empty
- [x] Browser: design authors ← books, see the edge in the diagram

## 7. Definition of done

- [x] In Bookshop, `books.author_id` references `authors` with `Cascade`, and the diagram shows the relation
- [x] Deleting `authors` is refused with a message naming `books.author_id`
- [x] No SQL is sent to `cf_p_*` databases

## 8. What M3 must add (tracked in the M3 phase doc)

Constraint names (`fk_<table>_<column>`, hashed if longer than 64), creating tables before
adding constraints, dropping constraints before dropping or modifying their columns and before
dropping tables, introspecting `REFERENTIAL_CONSTRAINTS`, and diffing on-delete changes.
