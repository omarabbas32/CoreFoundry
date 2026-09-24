# M1 — Auth, projects, members

**Goal:** a user can register, sign in, stay signed in through refresh-token
rotation, create a project (which provisions its `cf_p_<id>` database),
and manage who else has access, with role checks enforced on every route.

**Depends on:** M0.

> **Progress:** data model (all 7 metadata tables, see plan D13–D15), auth endpoints, projects API
> (with `cf_p_<id>` provisioning and startup recovery) and project authorization are done. Members API
> and the frontend are next. Test projects use ids ≥ 1,000,000 so tests never touch dev project databases.
> Auth integration tests run against a local `corefoundry_test` database (`db/setup-test.sql`) and
> skip when it isn't configured. Two refreshes racing with the same token: the loser gets 401
> (optimistic concurrency on `RefreshTokens.RevokedAt`), which is **not** treated as theft.

---

## 1. Data (EF Core, `corefoundry` database)

Tables: `Users`, `RefreshTokens`, `Projects`, `ProjectMembers`.
Full columns and delete rules are in the [data model](../corefoundry-erd.html).

- [x] Entities in `Domain`, EF configurations (`IEntityTypeConfiguration<T>`)
      in `Infrastructure`. No data annotations on domain entities.
- [x] Enums stored as `TINYINT`: `ProjectRole { Owner=1, Admin=2, Developer=3 }`,
      `ProjectStatus { Provisioning=0, Active=1, Failed=2, Deleting=3 }`
- [x] Unique indexes: `Users.Email`, `RefreshTokens.TokenHash`, `Projects.Slug`.
      Plain index: `ProjectMembers.UserId`.
- [x] `CreatedAt` / `UpdatedAt` set by a `SaveChanges` interceptor using an
      injected `TimeProvider` (testable time)
- [ ] First migration `InitialAuthAndProjects`, applied on startup in
      Development only. Other environments use `dotnet ef migrations bundle`.

---

## 2. Authentication

### Endpoints
| Method | Route | Body | Result |
|---|---|---|---|
| POST | `/api/auth/register` | `{ email, password }` | 201 + same response as login |
| POST | `/api/auth/login` | `{ email, password }` | 200 `{ accessToken, expiresAt, user }` + refresh cookie |
| POST | `/api/auth/refresh` | cookie | 200 new access token + rotated cookie |
| POST | `/api/auth/logout` | cookie | 204, token revoked, cookie cleared |
| GET  | `/api/auth/me` | Bearer | 200 `{ id, email }` |

### Rules
- [x] Emails are trimmed and lower-cased before they are saved. Passwords must be
      at least 10 characters (no composition rules, per NIST 800-63B).
- [x] Passwords are hashed with `PasswordHasher<User>` (PBKDF2).
- [x] Access token: JWT, HS256, 15 min, claims `sub`, `email`, `jti`.
      **No project roles in the token.** Roles are read from the database on each
      request, so revoking a role takes effect immediately.
- [x] Refresh token: 32 random bytes, base64url. Only `SHA-256(token)` is stored.
      It expires after 7 days.
- [x] Cookie: `cf_refresh`, `HttpOnly`, `Secure`, `SameSite=Strict`,
      `Path=/api/auth`
- [x] **Rotation** (one transaction): look up the token by hash. If it's valid, revoke it,
      insert a new one and set `ReplacedByTokenId`.
- [x] **Replay detection:** if the token is already revoked, follow
      `ReplacedByTokenId` to the end of the chain, revoke every token in it, and return 401.
- [x] Login and register rate-limited with the built-in
      `AddRateLimiter` (for example a fixed window of 10 requests/min per IP)
- [x] Login failure returns the same 401 whether the email or the password was
      wrong, so accounts can't be discovered by trying emails

---

## 3. Projects & provisioning

### Endpoints
| Method | Route | Min role |
|---|---|---|
| GET | `/api/projects` | (any signed-in user, returns only their memberships) |
| POST | `/api/projects` | (any signed-in user) |
| GET | `/api/projects/{projectId}` | Developer |
| PATCH | `/api/projects/{projectId}` | Admin (name only) |
| DELETE | `/api/projects/{projectId}` | Owner |
| POST | `/api/projects/{projectId}/retry-provisioning` | Owner |

### Create flow
1. EF transaction: insert `Project { Status = Provisioning }` and the
   `ProjectMember { Role = Owner }` row. The database name `cf_p_{Id}` is computed
   from the Id (`Project.DatabaseName`), so nothing else needs saving.
2. Outside the transaction, connect as `cf_engine` and run
   ``CREATE DATABASE IF NOT EXISTS `cf_p_{Id}` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci``.
3. Success → `Status = Active`. Failure → `Status = Failed`, log the error, and
   the UI shows a **Retry** button.

### Delete flow
1. `Status = Deleting` (the project disappears from the list immediately).
2. ``DROP DATABASE IF EXISTS `cf_p_{Id}` ``.
3. Delete the metadata rows (cascades take care of members, tables, migrations).

### Startup check
- [x] Hosted service on boot: finish provisioning for projects stuck in `Provisioning`
      and finish deleting projects stuck in `Deleting`. Both steps are safe to run
      again because of `IF [NOT] EXISTS`.

### Slug
- [x] Generated from the name (`My Shop` → `my-shop`). On collision, append `-2`, `-3` and so on.

---

## 4. Members

| Method | Route | Min role | Notes |
|---|---|---|---|
| GET | `/api/projects/{projectId}/members` | Developer | |
| POST | `/api/projects/{projectId}/members` | Admin | `{ email, role }`, the user must already exist |
| PUT | `/api/projects/{projectId}/members/{userId}` | Admin | change role |
| DELETE | `/api/projects/{projectId}/members/{userId}` | Admin | members can also remove themselves |
| POST | `/api/projects/{projectId}/transfer-ownership` | Owner | `{ userId }`, one transaction |

Rules:
- [ ] Exactly one `Owner` per project, always equal to `Projects.OwnerId`
- [ ] Admins can't grant, change or remove the Owner role
- [ ] The Owner can't be removed. Ownership has to be transferred first.

---

## 5. Authorization

- [x] `ProjectRoleRequirement(ProjectRole minimum)` +
      `ProjectRoleHandler : AuthorizationHandler<ProjectRoleRequirement>`
- [x] The handler reads `projectId` from route values and loads the caller's
      membership (one indexed query, cached for the length of the request).
- [x] Policies `Project.Developer`, `Project.Admin`, `Project.Owner`, applied as
      `[Authorize(Policy = "Project.Admin")]` on endpoints
- [x] **Not a member → 404** (so project ids can't be probed). **Member without
      the required role → 403.**
- [x] Role order: Owner (highest) > Admin > Developer. Keep the numeric values
      separate from the ordering to avoid accidental comparisons.

```csharp
// sketch
protected override async Task HandleRequirementAsync(
    AuthorizationHandlerContext ctx, ProjectRoleRequirement req)
{
    if (!TryGetProjectId(out var projectId)) return;          // no route value → fail
    var role = await _members.GetRoleAsync(projectId, ctx.User.GetUserId());
    if (role is null) { _http.Items["cf:notMember"] = true; return; } // → 404 in result handler
    if (role.Value.AtLeast(req.Minimum)) ctx.Succeed(req);
}
```
Turn "not a member" into 404 with a custom `IAuthorizationMiddlewareResultHandler`.

---

## 6. Frontend (Next.js)

- [ ] `/login`, `/register`: react-hook-form + zod, server errors mapped to fields
- [ ] Auth state: keep the access token in memory (React context). On app load,
      call `/api/auth/refresh` to restore the session from the cookie.
- [ ] `apiFetch` wrapper: adds the Bearer token. On a 401 it refreshes once and
      retries. If two requests get a 401 at the same time, they share one refresh call.
- [ ] `/projects`: list with a status badge (Provisioning, Active, Failed + Retry),
      and a create dialog
- [ ] `/projects/[id]/members`: table showing roles, add by email, change role, remove.
      Controls the user isn't allowed to use are hidden or disabled.
- [ ] CORS: API allows `http://localhost:3000` with credentials (dev only).
      Production uses a shared origin (M5).

---

## 7. Tests

**Unit**
- [ ] Slug generation, role comparison, refresh-token hashing

**Integration (Testcontainers + WebApplicationFactory)**
- [x] Register → login → `/me` works. Wrong password and unknown email return the same 401.
- [x] Refresh rotates the cookie. The old cookie is then rejected. Replaying a revoked
      token revokes the chain, and the newest token stops working too.
- [x] Creating a project creates `cf_p_<id>` (checked via `INFORMATION_SCHEMA.SCHEMATA`)
- [x] Deleting a project drops the database
- [ ] Non-member → 404. Developer on an Admin route → 403. Admin can't remove the Owner.
- [ ] Transfer ownership swaps the roles and `OwnerId` atomically

---

## 8. Definition of done
- [ ] A new user can register, create "Bookshop", invite a second user as Developer,
      and that user sees the project but can't manage members
- [ ] Closing and reopening the browser keeps the session (refresh cookie)
- [ ] `cf_p_<id>` exists after creating a project and is gone after deleting it
- [ ] All tests above pass in CI

## 9. Interview talking points
- Why roles aren't in the JWT (revoking a role takes effect immediately)
- Refresh rotation with replay detection, and why only hashes are stored
- 404 vs 403 for non-members
- Why provisioning uses a status field instead of a transaction (a database can't
  be created inside a transaction)
