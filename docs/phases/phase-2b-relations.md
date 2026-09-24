# M2.5 — Relations and schema diagram (draft only)

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

- [ ] `ProjectColumns.ReferencesTableId` (nullable FK → `ProjectTables.Id`) and
      `ProjectColumns.OnDelete` (`TINYINT`, `Restrict=1, Cascade=2, SetNull=3`), migration `AddColumnReferences`
- [ ] Both set or both null

## 3. Rules (Domain)

- [ ] A reference column is `BigInt` (it holds an `id`), with no length/precision/scale and no default
- [ ] `SetNull` requires a nullable column
- [ ] The target table is in the same project and not pending drop; self-references are allowed
- [ ] A table can't be deleted (hard delete or pending drop) while live columns of **other**
      tables reference it; the error names them (`books.author_id`)
- [ ] A column can't be restored, and a table can't be restored, while a reference it holds
      points at a table pending drop
- [ ] Renaming the target is fine: references are by id

## 4. API

- [ ] Column requests: `referencesTableId`, `onDelete`; responses add `referencesTableName`
- [ ] A target from another project (or unknown) → 400 on `referencesTableId`
- [ ] `GET /api/projects/{projectId}/schema` (Developer): every table with its columns,
      for the diagram (and the M3 plan)

## 5. Frontend

- [ ] Column dialog: "References" picker (none or a table); picking one locks the type to
      BigInt, hides the default and shows "On delete"
- [ ] Grid: `BigInt → authors` with the on-delete rule
- [ ] `/projects/[id]/tables/diagram`: React Flow canvas, one node per table (columns listed,
      pending drops struck through), an edge from each reference column to its target's `id`,
      auto layout, pan/zoom/drag, click a table to open the designer

## 6. Tests

- [ ] Unit: every rule in §3
- [ ] Integration: create `books.author_id → authors`; wrong type / SetNull on NOT NULL /
      other project's table → 400; deleting `authors` while referenced → 400; after deleting the
      reference column it works; `/schema` returns both tables; the project database stays empty
- [ ] Browser: design authors ← books, see the edge in the diagram

## 7. Definition of done

- [ ] In Bookshop, `books.author_id` references `authors` with `Cascade`, and the diagram shows the relation
- [ ] Deleting `authors` is refused with a message naming `books.author_id`
- [ ] No SQL is sent to `cf_p_*` databases

## 8. What M3 must add (tracked in the M3 phase doc)

Constraint names (`fk_<table>_<column>`, hashed if longer than 64), creating tables before
adding constraints, dropping constraints before dropping or modifying their columns and before
dropping tables, introspecting `REFERENTIAL_CONSTRAINTS`, and diffing on-delete changes.
