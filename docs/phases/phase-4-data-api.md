# M4 — Data API + data viewer

**Goal:** once a table is applied, users can browse, add, edit and delete rows,
both from the UI and through a generic REST API, using the same identifier
safety as the schema engine.

**Depends on:** M3 (needs `SnapshotJson` and applied tables).

Diagram: [Data API request flow](../corefoundry-flows.html#data).

---

## 1. Source of truth: the applied snapshot

- [ ] `SnapshotProvider.GetAsync(projectId)` returns the `SchemaModel` from the
      latest `SchemaMigrations` row with `Status = Applied`
- [ ] Cached in `IMemoryCache` under `snapshot:{projectId}:{schemaVersion}`.
      A successful apply bumps `SchemaVersion`, which invalidates the cache automatically.
- [ ] **The draft is never used here.** A column that exists only in the draft
      doesn't exist for the Data API.

---

## 2. Endpoints

| Method | Route | Min role |
|---|---|---|
| GET | `/api/projects/{projectId}/data/{table}` | Developer |
| GET | `/api/projects/{projectId}/data/{table}/{id}` | Developer |
| POST | `/api/projects/{projectId}/data/{table}` | Developer |
| PUT | `/api/projects/{projectId}/data/{table}/{id}` | Developer |
| DELETE | `/api/projects/{projectId}/data/{table}/{id}` | Developer |

### List query parameters
| Param | Rule |
|---|---|
| `page` | ≥ 1, default 1 |
| `pageSize` | 1–100, default 25 |
| `sort` | a column name from the snapshot, optional `-` prefix for DESC. `id` always added as a tie-breaker |

Response:
```json
{ "items": [ { "id": 26, "title": "Dune", "price_usd": "19.99" } ],
  "page": 2, "pageSize": 25, "total": 312 }
```
Decimals are serialized as **strings** so no precision is lost in JavaScript.
BIGINT `id` is a number (safe below 2^53, and documented as such).

---

## 3. Query building

- [ ] Resolve `{table}` against the snapshot → `TableModel` or 404
- [ ] Resolve `sort` → `ColumnModel` or 400
- [ ] Build SQL from resolved models only, quoting names with the M3 `Quote()`:
  ```sql
  SELECT `id`, `title`, `price_usd`, `isbn`, `published_on`
  FROM `cf_p_7`.`books`
  ORDER BY `price_usd` DESC, `id`
  LIMIT @take OFFSET @skip;

  SELECT COUNT(*) FROM `cf_p_7`.`books`;
  ```
- [ ] Insert and update use `@p_<ordinal>` parameter names, never names derived
      from user input
- [ ] Insert returns `LAST_INSERT_ID()` and then re-reads the row
- [ ] Executed with Dapper on a `cf_engine` connection

---

## 4. Value coercion (JSON → column type)

| Type | Accepts | Rejects |
|---|---|---|
| Int / BigInt | JSON integer within range | floats, strings |
| Decimal(p,s) | number or numeric string, ≤ p digits, ≤ s decimals | extra decimals (no silent rounding) |
| Bool | `true` / `false` | `0/1`, `"true"` |
| Varchar(n) / Text | string, length ≤ n (Text ≤ 65,535 bytes) | non-strings |
| Date | `yyyy-MM-dd` | other formats |
| DateTime | ISO-8601. Values with an offset are converted to UTC | — |
| Uuid | canonical UUID string | — |
| Json | any JSON value (stored as its serialized text) | — |
| any, `null` | only if the column is nullable | — |

- [ ] Unknown fields → 400. `id` in the body → 400.
- [ ] Missing NOT NULL field without a default on POST → 400
- [ ] PUT is a full replace of the user columns. PATCH is out of scope for v1.
- [ ] All errors are collected and returned together as `ValidationProblemDetails`
      (`errors: { "price_usd": ["Must have at most 2 decimals."] }`)

### Database error mapping
| MySQL error | HTTP | Message |
|---|---|---|
| 1062 duplicate entry | 409 | "`isbn` must be unique; `978…` already exists." |
| 1048 column cannot be null | 400 | per-field error |
| 1406 data too long | 400 | per-field error (should be caught earlier) |
| 1146 table doesn't exist | 409 | "Schema drift detected: run a new plan." |
| anything else | 500 | generic problem + correlation id, full error in the logs only |

---

## 5. Frontend

- [ ] `/projects/[id]/data/[table]`: table picker, paginated grid, sortable
      column headers
- [ ] Cells formatted by type (dates, decimals right-aligned, JSON collapsed)
- [ ] "Add row" and "Edit row" in a side panel. The form is **generated from the
      snapshot**: input type per column, required marks, and server errors shown per field.
- [ ] Delete with in-page confirmation (no `window.confirm`)
- [ ] Empty state: "No rows yet. Add the first one." Not-applied state:
      "This table hasn't been applied yet. Review the plan."

---

## 6. Tests

**Unit**
- [ ] Coercion: every row of the table above, including edge values (INT max/min,
      decimal scale overflow, leap day, invalid UUID)
- [ ] Query builder: snapshot tests. Unknown sort column → error.

**Integration**
- [ ] CRUD round trip on an applied `books` table
- [ ] Pagination and sort stability (equal prices come back ordered by `id`)
- [ ] Unique violation → 409 with a field message
- [ ] A column that exists only in the draft (not yet applied) → 400 unknown field
- [ ] After apply renames `price` → `price_usd`, the Data API uses the new name at once
      (cache invalidated)
- [ ] Injection attempts in `{table}`, `sort`, and JSON keys → 404/400, never SQL

---

## 7. Definition of done
- [ ] In Bookshop, add authors and books from the UI, sort by price, page through, edit, delete
- [ ] The same operations work with `curl` using a Bearer token (documented in the README)
- [ ] All tests pass in CI

## 8. Interview talking points
- Values are parameters and identifiers are resolved against the snapshot: two different defenses for two different problems
- Why the Data API reads the applied snapshot rather than the draft or the live `INFORMATION_SCHEMA`
- Cache invalidation by version key instead of explicit eviction
- Decimals as strings in JSON
