# M8 — Access rules for the exported backend (plan)

**Goal:** today every endpoint of an exported backend requires a signed-in user, which doesn't fit most apps: a
shop's products and reviews must be readable by anyone, while payments are for admins only. In M8 the user decides,
**in CoreFoundry before exporting**, who may read and who may write each table, and the export writes exactly those
rules into the generated code.

**Status:** plan only, nothing built yet.
**Decided by the user (2026-09-25):** rules are edited in the project and used by the export · one **read** rule and
one **write** rule per table · levels **Public / Signed-in / Admin** · in the exported backend the **first registered
user becomes Admin**.
**Not changing:** CoreFoundry's own Data API stays members-only; it is the project's admin tool, not the app's public API.
**Order:** M8 is built and merged before M9 starts. M9 (realtime in the export) uses `ExportModel`'s `Read`, the
Admin role and the fallback policy from this phase as they are.

---

## 1. The rules

| Level | Who | Generated attribute |
|---|---|---|
| **Public** | anyone, no token | `[AllowAnonymous]` |
| **Signed-in** | any user with a valid token | `[Authorize]` |
| **Admin** | users with the Admin role | `[Authorize(Roles = "Admin")]` |

- **Read** covers `GET /api/{table}` and `GET /api/{table}/{id}`.
- **Write** covers `POST`, `PUT` and `DELETE`.
- A new table starts at **Read: Signed-in, Write: Signed-in**, which is today's behavior, so nothing becomes public by accident.
- Write can't be more open than read (e.g. Read Admin + Write Public is refused): whoever can change rows must
  be able to see them.

### Defaults of the E-commerce template (M7)

| Table | Read | Write | Why |
|---|---|---|---|
| `products` | Public | Admin | the catalog is public, only staff edit it |
| `categories` | Public | Admin | same |
| `reviews` | Public | Signed-in | anyone reads, customers write |
| `customers` | Admin | Admin | personal data |
| `addresses` | Admin | Admin | personal data |
| `orders` | Admin | Signed-in | customers place orders; only staff list them (see §6) |
| `order_items` | Admin | Signed-in | same as orders |
| `payments` | Admin | Admin | money |

## 2. Where the rules live (CoreFoundry)

- **Metadata:** two columns on `ProjectTables`, `ReadAccess` and `WriteAccess` (TINYINT, enum `AccessLevel`
  Public = 1, SignedIn = 2, Admin = 3, default SignedIn), migration `TableAccessRules`.
- They're **not part of the schema**: no DDL, the plan/apply ignores them, and changing them never marks a table
  `Changed`. They still use the table's `Version` (409 when someone else changed the table meanwhile).
- **Domain:** `ProjectTable.SetAccess(read, write)` checks the "write not wider than read" rule.
- **Templates:** `TemplateTable` gets `Read`/`Write` (the defaults above); `TemplateService` sets them when it
  creates the drafts.

### API
| Method | Route | Notes |
|---|---|---|
| PUT | `/api/projects/{id}/tables/{tableId}/access` | Developer+; `{ version, read, write }` → the table with its new version |
| GET | `/api/projects/{id}/tables`, `…/tables/{tableId}` | now include `readAccess`, `writeAccess` |

### UI
- **Designer (table page):** an "Access in the exported API" row with two selects (Read, Write) and a hint on what each
  level means.
- **API page:** an **Access** section: one row per table with both selects (the grid in the preview above), so
  the whole API can be reviewed and set in one place, next to the Export card.
- **Export card:** a one-line summary ("3 public tables, 2 admin-only") and a warning when every table is still at
  the default.

## 3. What the export generates

- **`ExportModel`:** each entity gets `Read`/`Write`, taken from the draft table whose `AppliedName` is the applied
  table (the export already follows the applied schema; access comes from metadata, so an edit takes effect on the
  next export without an apply).
- **Controllers:** no class-level `[Authorize]` any more. Each action gets its level's attribute: `List`/`Get` from
  Read, `Create`/`Replace`/`Delete` from Write. A fallback authorization policy (require an authenticated user)
  keeps anything without an attribute closed. It covers every endpoint, not only controllers: `/health` already
  has `.AllowAnonymous()`, and M9's realtime hub will need the same (it checks each table's Read level itself).
- **Roles in the generated auth:**
  - `AppUser.Role` (`User` / `Admin`), column `cf_users.role`, in the generated `InitialCreate` migration and
    model snapshot (`EfMigrationWriter`).
  - Register: the **first** account becomes `Admin`. The check runs in a serializable transaction so two
    simultaneous first sign-ups can't both become Admin.
  - The token carries a `role` claim (`RoleClaimType = "role"` in the JWT setup).
  - Admin-only endpoints: `GET /api/auth/users` (list accounts) and `PUT /api/auth/users/{id}/role` (promote or
    demote; the last Admin can't be demoted).
- **Swagger UI:** the lock icon only on operations that need a token (an operation transformer reads the endpoint's
  authorization metadata instead of adding the Bearer requirement to the whole document).
- **README of the export:** the access table (table, read, write) and a note on how to become Admin.

## 4. Tests

- **Unit:** `SetAccess` rules (write not wider than read, unknown levels); template defaults pass them; the
  generator writes the right attribute per action and level; migration writer includes `role`.
- **Integration (CoreFoundry):** the access endpoint (update, stale version → 409, non-member → 404, invalid combination
  → 400); access changes don't appear in the plan; template tables get their defaults.
- **End to end (extends the M6 export test):** export Bookshop with `books` Read Public / Write Admin and `authors`
  Admin/Admin, then against the running generated API:
  - no token: `GET /api/books` → 200, `POST /api/books` → 401, `GET /api/authors` → 401
  - the first user (Admin): everything works
  - the second user (User): `GET /api/books` → 200, `POST /api/books` → 403, `GET /api/authors` → 403
  - the Admin promotes the second user → `POST /api/books` → 201
  - EF still reports no pending model changes.

## 5. Steps (plan-before-execute; commit after each verified step)

1. Domain + migration `TableAccessRules`, `SetAccess`, DTO fields, access endpoint, integration tests.
2. Template defaults (E-commerce).
3. Export: `ExportModel` access, per-action attributes, fallback policy, roles in the generated auth and migration,
   Swagger per-operation lock; unit tests.
4. End-to-end export test with public/admin tables.
5. Web: selects in the designer, the Access section on the API page, the Export card summary.
6. Docs: README, PROGRESS, phase checklist, decisions log.

## 6. Open questions (decide before or during M8)

1. **Per-customer data ("only my own orders").** With table-level rules, *Signed-in* read on `orders` would show
   every customer's orders to every signed-in user, which is why the template uses Admin read for them. Real
   per-row ownership would need a fourth level, **Owner** (rows whose owner column is the signed-in user), and a
   link between the generated `cf_users` accounts and a table such as `customers`. Suggested for a later phase.
2. **Signed-in writes can set any value.** E.g. a customer creating an order could set another customer's
   `customer_id`. Ownership (question 1) or server-set columns would fix it; until then the export README says so.
3. **Should CoreFoundry's own Data API ever serve public tables?** The user chose export-only for now.

## 7. Definition of done
- [ ] Access rules can be set per table in the designer and on the API page, and saved
- [ ] A new E-commerce project has the defaults above
- [ ] The exported backend enforces them (end-to-end test), the first user is Admin, and Swagger shows which endpoints are public
- [ ] All tests pass
