# M7 — Schema templates (ready schemas)

> **Built** on branch `m7-schema-templates` (2026-09-25); decisions D36–D37. The browser check (§5) is still open.

**Goal:** instead of designing every table from scratch, a user starts a project from a **ready schema**, first
**E-commerce**, and can optionally get **sample rows**, so the data viewer, the API page and the code export have
something real to show straight away.

**Depends on:** M2/M2.5 (draft tables and references), M3 (apply), M4 (row inserts).
**Decided by the user (2026-09-25):** pick a template in the *New project* dialog **and** in an empty project's
designer · first template: **E-commerce** · optional sample rows, inserted **right after the first apply**.
**Default kept:** a template only creates **draft** tables. Nothing touches MySQL until the user reviews the plan and applies it.

---

## 1. The E-commerce template

8 tables, every column type used somewhere, all three on-delete rules:

| Table | Columns (→ reference, on delete) |
|---|---|
| `customers` | `email` Varchar(254) unique · `full_name` Varchar(120) · `phone` Varchar(30)? · `birth_date` Date? · `created_at` DateTime = CURRENT_TIMESTAMP |
| `addresses` | `customer_id` → customers (Cascade) · `line1` Varchar(200) · `line2` Varchar(200)? · `city` Varchar(100) · `postal_code` Varchar(20)? · `country_code` Varchar(2) · `is_default` Bool = false |
| `categories` | `name` Varchar(100) unique · `slug` Varchar(120) unique · `parent_id` → categories (SetNull)? |
| `products` | `category_id` → categories (SetNull)? · `sku` Varchar(40) unique · `name` Varchar(200) · `description` Text? · `price` Decimal(10,2) · `stock` Int = 0 · `is_active` Bool = true · `attributes` Json? · `created_at` DateTime = CURRENT_TIMESTAMP |
| `orders` | `public_id` Uuid = UUID() unique · `customer_id` → customers (Restrict) · `shipping_address_id` → addresses (SetNull)? · `status` Varchar(20) = 'pending' · `total` Decimal(12,2) = 0.00 · `placed_at` DateTime = CURRENT_TIMESTAMP |
| `order_items` | `order_id` → orders (Cascade) · `product_id` → products (Restrict) · `quantity` Int = 1 · `unit_price` Decimal(10,2) |
| `payments` | `order_id` → orders (Cascade) · `amount` Decimal(12,2) · `method` Varchar(30) · `status` Varchar(20) = 'authorized' · `paid_at` DateTime? |
| `reviews` | `product_id` → products (Cascade) · `customer_id` → customers (Cascade) · `rating` Int · `comment` Text? · `created_at` DateTime = CURRENT_TIMESTAMP |

`?` = nullable; `= x` = default. Sample data: 6 customers, 7 addresses, 5 categories (2 nested), 12 products,
8 orders with items and payments, 10 reviews. All names and emails are made up (`@example.com`).

## 2. How it works

- **Definitions in code** (`Application/Templates`): a template is a list of tables and columns, plus sample rows whose
  references use the other rows' local keys (`"customer_id": "@ann"`). A unit test runs every template through the
  same Domain rules as the designer (names, column rules, row size, references), so a template can't be invalid.
- **Using a template** (`POST /api/projects/{id}/templates/{key}`, Developer+, only while the project has **no
  tables**, otherwise 409): creates the draft tables in reference order (self-references added last) and remembers
  the template on the project (`Projects.TemplateKey`, `Projects.SampleDataPending`, migration `ProjectTemplates`).
- **New project dialog:** "Start with: Empty · E-commerce (8 tables)" + "Add sample data after the first apply";
  the web creates the project, then applies the template.
- **Empty designer:** a "Start from a template" button opens the same choice (with a preview of the tables).
- **Sample rows after apply:** when an apply succeeds and the project has `SampleDataPending`, the rows are inserted
  through the Data API's own coercion and repository (same validation), parents before children, local keys
  replaced by the real ids. Tables the user renamed or dropped before applying are skipped, and the result says
  which. The flag is cleared either way; the apply itself never fails because of sample data. The apply result shows
  "Added N sample rows", and there's a "Load sample data" button while the tables are still empty.
- `GET /api/templates`: key, name, description, tables (names + column counts) for the pickers.

## 3. Steps (plan-before-execute; commit after each verified step)

1. **Template model + E-commerce definition** with the Domain-rules test (and a topological-order test).
2. **Apply a template** to an empty project: service, migration for the two project columns, endpoints
   (`GET /api/templates`, `POST …/templates/{key}`), integration tests (tables, references, 409 on a non-empty project,
   non-members 404).
3. **Sample data**: seeding after a successful apply (and the "Load sample data" endpoint), integration tests
   (row counts, references resolved, plan → apply → rows; renamed table skipped; the flag cleared).
4. **Web**: template choice in the New project dialog, "Start from a template" in the empty designer, the apply
   result message and the "Load sample data" button.
5. **Docs**: README, PROGRESS, this checklist, decisions.
6. **Browser check only if you want it.**

## 4. Out of scope (roadmap)
More templates (Blog, CRM, Task manager: easy to add once the model exists) · adding a template to a project that
already has tables · user-made templates ("save my schema as a template").

## 5. Definition of done
- [ ] New project → E-commerce + sample data → Review plan → Apply → the data viewer shows the sample rows
      (the API path is tested end to end; the UI hasn't been clicked through in a browser)
- [ ] The same in an empty existing project from the designer
- [x] The E-commerce template passes every Domain rule (test) and exports/builds with M6 (test: generated; the
      exported-build test uses its own Bookshop)
- [x] All tests pass (722)
