# M9 — Realtime in the exported backend (plan)

**Goal:** the backend a user downloads today is request/response only. Supabase's signature feature is that a
client *subscribes* to table changes. In M9 the export also generates a **realtime hub**: the generated API pushes
`insert` / `update` / `delete` events for its own tables, and a client subscribes per table over SignalR. The levels
the user already set for the REST API (M8) decide **who may subscribe**.

**Status:** plan only, nothing built yet.
**Depends on:** M8, **built and merged first** (a subscription is a *read*, so its rule is the table's read level).
This plan assumes M8 is in place: `ExportModel` already carries each entity's `Read`/`Write`, the generated auth
has the Admin role and the `role` claim, and the generated API has M8's fallback policy (anything without an
attribute requires a signed-in user). M9 starts only after M8's Definition of done is met.
**Decided by the user (2026-09-25):** realtime belongs in the **exported** backend — the user's own app — not in
CoreFoundry's own UI. CoreFoundry is the generator; the product is the backend it hands over.
**Not changing:** CoreFoundry's own Data API and web UI stay request/response (M8: the Data API is the admin tool,
not the app's public API). Nothing here adds a schema object, so plan/apply, drift and the snapshot are untouched.

---

## 1. The subscription model

| Concept | v1 |
|---|---|
| **Transport** | SignalR over WebSockets (the generated API is reached directly by the user's frontend — see §3) |
| **Hub** | `RealtimeHub` at `/hubs/realtime` (outside `/api/`, where every table has a route — §2) |
| **Client calls** | `Subscribe(table)` and `Unsubscribe(table)`; groups are `table:<name>` |
| **Server event** | `change` → `{ table, operation, id }` with operation `insert` / `update` / `delete` |
| **Who may subscribe** | the table's **read** level from M8: Public = anyone (no token), Signed-in = valid token, Admin = Admin role |
| **Which tables** | only the tables the export contains; any other name is refused (the allow-list is generated, never taken from the client) |
| **Payload** | a **notification, not the row**: the REST API stays the single source of truth and the client refetches (why, and the alternative, in §6 q.1) |

A subscription is a **read**, so it follows the read level, not the write level. Because a Public table must be
subscribable without a token, the hub can't carry a blanket `[Authorize]`, and M8's fallback policy would close it
too: `MapHub` is mapped with **`.AllowAnonymous()`** so anyone can connect, and each `Subscribe` checks the table's
level against `Context.User`, mirroring the per-action attributes M8 writes on the controllers (and the same
"anything not explicitly allowed is closed" rule). A refused `Subscribe` throws a `HubException` with a readable
message ("Sign in to subscribe to orders."), so the client's `invoke` rejects with it. Subscribing twice to the
same table is harmless (group membership is a set).

**What is covered, honestly:**
- Events are emitted for writes that go through the generated API. Supabase reads the Postgres WAL, so it also sees
  writes from other clients, scripts or a second service. Closing that gap is a bigger job (§6 q.4) and is out of
  scope for v1 — the export README says so instead of pretending.
- **Cascades send no event.** Deleting an `authors` row whose `books` reference it with *Cascade* or *Set null*
  changes those `books` rows inside MySQL; only the `authors` delete is published. The README says so.
- **Access is checked at subscribe time.** A user demoted by an Admin (M8's role endpoint) keeps receiving events
  until the connection closes. The hub sets `CloseOnAuthenticationExpiration`, so a connection with a token ends
  when that token expires. Events carry only ids, which limits what a stale subscription sees.

## 2. Where this lives in CoreFoundry

- **`ExportModel`:** no change. M8 already gives each entity its `Read`/`Write`; the hub's level map is generated
  from `Read`. The hub is always generated (§6 q.5), so there is no `Realtime` flag, no migration, no DDL.
- **`CodeNames.Reserved`** gains the new generated types and namespace segment — `RealtimeHub`, `ChangeEvent`,
  `ChangeOperation`, `IChangePublisher`, `SignalRChangePublisher`, `Realtime` — so a table such as `change_events`
  can't produce a clashing class.
- **No route clash:** controllers are routed `api/{table}`, so a hub under `/api/` could collide with a table named
  `realtime`. The hub lives at **`/hubs/realtime`**, outside `/api/`, so no table name can reach it.
- **No new CoreFoundry runtime component.** No hub, no notifier and no socket in the CoreFoundry API.
- **UI (small):** the Export card mentions what will be subscribable ("Realtime: 6 tables — 2 public"), so the user
  can see the consequence of M8's levels before downloading.

## 3. What the export generates

| File | Change |
|---|---|
| `Bookshop.Application/Realtime/IChangePublisher.cs` | **new** — port + `ChangeEvent`, `ChangeOperation` records (no SignalR types, like `IRepository`) |
| `Bookshop.Application/Common/CrudService.cs` | **changed** — a new abstract `TableName`; after `SaveChangesAsync` in Create/Replace/Delete, publish once (the single write path for every table). Nothing is published when the save throws |
| `Bookshop.Application/Tables/Book/BookService.cs` (each entity) | **changed** — overrides `TableName` with the applied table name (`"books"`) |
| `Bookshop.Infrastructure/Realtime/RealtimeHub.cs` | **new** — the hub + the generated table→read-level map (from M8's `Read`) and the `Subscribe`/`Unsubscribe` checks |
| `Bookshop.Infrastructure/Realtime/SignalRChangePublisher.cs` | **new** — pushes to `Clients.Group("table:<name>")` through `IHubContext<>`; registered in Infrastructure DI. It **catches and logs** any send failure: the row is already saved, so the request must not fail. Each send is **bounded to 5 s**, so a subscriber that stops reading can't hold up the table's writes |
| `Bookshop.Infrastructure/DependencyInjection.cs` | **changed** — `AddSignalR()` (with `CloseOnAuthenticationExpiration`) and the publisher registration |
| `Bookshop.Api/Program.cs` | **changed** — `MapHub<RealtimeHub>("/hubs/realtime").AllowAnonymous()`, CORS, and the `access_token` query-string reader in `JwtBearerEvents.OnMessageReceived`, **only for paths under `/hubs/`** |
| `Bookshop.Api/appsettings.json` | **changed** — a `Cors:AllowedOrigins` section, empty by default (the generated app has no CORS at all today; a browser client needs it) |
| `Bookshop/README.md` | **changed** — a Realtime section: subscribe snippet, refetch-on-reconnect, CORS, the external-writer gap |

**Why the hub is in Infrastructure and not Api:** the publisher lives next to `EfRepository`, and a publisher in
Infrastructure cannot reference a type in Api. SignalR is available without a new package in both projects (the
generated `Infrastructure.csproj` already has `FrameworkReference Microsoft.AspNetCore.App`, and the generated Api
uses `Microsoft.NET.Sdk.Web`), so this costs nothing. `MapHub` in `Program.cs` still wires it.

**Transport:** the generated API is called directly by the user's frontend, so WebSockets work as they are. (This is
why the feature belongs in the export: CoreFoundry's own UI would have to tunnel WebSockets through Next's
standalone proxy, which does not upgrade them — vercel/next.js#44209, closed as not planned.) If the user puts a
reverse proxy in front of the generated API, it must forward upgrades (Caddy/nginx do); the README says so. One
instance only: several instances need a SignalR backplane (Redis) — out of scope, documented.

**CORS, exactly:** the policy reads `Cors:AllowedOrigins`. Empty (the default) → no CORS policy is added, which is
today's behavior. Set → `WithOrigins(...)` + `AllowAnyHeader()` + `AllowAnyMethod()` + **`AllowCredentials()`** (the
SignalR JS client sends credentials by default, and `AllowAnyOrigin` can't be combined with credentials).
`UseCors()` goes **before** `UseAuthentication()`. The policy covers the REST API too — a browser frontend needs it
there as well. The README notes that the WebSocket token travels in the query string, so it can appear in proxy
access logs.

**The snippet the export README carries:**

```js
import { HubConnectionBuilder } from "@microsoft/signalr";

const connection = new HubConnectionBuilder()
  .withUrl("http://localhost:8080/hubs/realtime", { accessTokenFactory: () => token })
  .withAutomaticReconnect()
  .build();

// A reconnect gets a new connection, and the server forgets its subscriptions: remember them here.
const tables = new Set();
async function subscribe(table) {
  tables.add(table);
  await connection.invoke("Subscribe", table);
}

connection.on("change", (e) => {
  // { table: "books", operation: "insert", id: 12 } — a hint: refetch, nothing is replayed
  refresh(e.table);
});
connection.onreconnected(async () => {
  for (const table of tables) await connection.invoke("Subscribe", table);
  refreshEverything(); // events sent while disconnected are lost
});

await connection.start();
await subscribe("books");
```

---

## 4. Tests

- **Unit** (`tests/CoreFoundry.UnitTests/Export`): the generated files — the hub, the publisher (catches and logs a
  failed send), `CrudService` publishing once per write and not on a failed save, each service's `TableName`, the
  `Program.cs` wiring (`AllowAnonymous` on the hub, CORS before authentication, `access_token` only under `/hubs/`),
  the generated level map from M8's `Read`, and that a table name outside the export is refused; plus the new names
  in `CodeNames.Reserved` (a table `change_events` still exports and builds).
- **Integration** (extends the M6/M8 end-to-end test, `tests/CoreFoundry.IntegrationTests/Export/ExportEndpointsTests.cs`),
  against the running exported Bookshop with M8's levels (`books` Read Public / Write Admin, `authors` Admin/Admin):
  - a subscribed client gets three `change` events (right table, operation, id) for `POST`/`PUT`/`DELETE` over HTTP
  - a token-less client may subscribe to `books` and gets a `HubException` for `authors`; a signed-in non-admin
    gets one for `authors` too
  - **reconnect:** the connection is dropped and restored, the client re-subscribes, and the next write still arrives
  - a failed save (e.g. a unique-value conflict) publishes nothing
  - the generated project still builds with **0 warnings** and `dotnet ef migrations has-pending-model-changes`
    still reports none.
  Event assertions wait on a collector with an explicit timeout, so a missing event fails fast instead of hanging.
- **New test package:** `Microsoft.AspNetCore.SignalR.Client` (integration tests only; CoreFoundry itself keeps no
  SignalR dependency) — §6 q.6.

## 5. Steps (plan-before-execute; commit after each verified step)

0. **Before starting:** M8 is merged into `main` and its Definition of done is checked off. M9's branch starts from
   that `main`.
1. **Reserved names:** the new types and the `Realtime` namespace segment in `CodeNames.Reserved`, with a test.
2. **Hub, write path and authorization — one step, so no commit has an open hub:** `IChangePublisher`, `TableName`
   and publishing in `CrudService`, the hub with its level map and `Subscribe` checks (anonymous / Signed-in / Admin,
   unknown table refused), the Infrastructure publisher and DI, `Program.cs` (hub with `AllowAnonymous`,
   `access_token` under `/hubs/`, CORS) and `appsettings.json`; unit tests on the generated files.
3. **End-to-end:** the export integration test gains the realtime assertions (§4).
4. **The export's README:** the snippet above, re-subscribe and refetch on reconnect, CORS setup, the token in the
   query string, the external-writer and cascade gaps, backplane note.
5. **Web (CoreFoundry):** the Export card says what will be subscribable; the API page explains that the read level
   is also the subscription rule.
6. **Docs:** `docs/PROGRESS.md`, this checklist, the decisions log, and `intial-plan.md` §1 — which currently lists
   "Realtime" among the things explicitly **cut** — reworded to point at M9 (realtime in the *exported* backend,
   still not in CoreFoundry's own UI).

## 6. Open questions (decide before or during M9)

1. **Payload: notification or row?** Recommended: notification only. The row's JSON contract (decimals as *strings*,
   date/time formats, the generated converters) lives in the controllers' JSON options; a hub sending rows would have
   to duplicate those options through `AddJsonProtocol` or quietly diverge from the REST API. Sending the row is a
   later upgrade with that duplication done deliberately.
2. **Client-side filters.** Supabase accepts `filter: "id=eq.5"`. Arbitrary predicates are a SQL-injection and
   authorization surface; v1 takes none, and a later version may allow a small safe subset on indexed/unique columns.
3. **Row-level visibility (the RLS equivalent).** v1 is table-level only — the same limit M8 has. Per-row rules need
   M8's open question 1: an **Owner** level plus a link between the generated `cf_users` and a user-owned table.
4. **External writers (CDC).** Events cover the generated API's own writes only. Supabase reads the WAL, so any writer
   is captured. Closing it means polling watermarks (`id`, an `UpdatedAt` column, or a change-log table — which would
   also need a generated migration) or a binlog reader (needs `REPLICATION CLIENT`/`REPLICATION SLAVE` grants on the
   user's database and `binlog_row_image=FULL`). That is a phase of its own, not a step of this one.
5. **Always generate the hub, or an opt-in checkbox?** Recommended: always (one export shape, one code path to test);
   the cost is a little unused code in a download that doesn't want realtime.
6. **Which client do we document?** Recommended: the `@microsoft/signalr` snippet above, in the export README only —
   we do not generate a TypeScript client in this phase.

## 7. Definition of done

- [ ] The exported backend has a hub, and a subscribed client receives `insert` / `update` / `delete` notifications
      for the tables it may read
- [ ] M8's levels are enforced at subscribe time (Public without a token, Signed-in, Admin), and unknown table names
      are refused
- [ ] A client that reconnects and re-subscribes keeps receiving events (end-to-end test)
- [ ] A failed publish never fails a saved write, and a failed save publishes nothing
- [ ] The generated solution still builds with **0 warnings** and its migration still matches its model
- [ ] The export README documents subscribing, re-subscribing and refetching on reconnect, CORS, and the
      external-writer and cascade gaps
- [ ] `docs/PROGRESS.md`, the decisions log and `intial-plan.md` §1 are updated
- [ ] All CoreFoundry tests pass (including the extended export test)
