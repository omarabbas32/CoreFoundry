# M5 — Portfolio polish

**Goal:** someone who has never seen the project understands it in 2 minutes
from the README, can run it with one command, and can try a live demo.

**Depends on:** M4 (or M3 if M4 was cut).

---

## 1. One-command run

- [x] `Dockerfile` for the API (multi-stage: `sdk` build → `aspnet` runtime, non-root user): `docker/api.Dockerfile`
- [x] `Dockerfile` for web (Next.js `output: "standalone"`): `docker/web.Dockerfile`
- [x] `docker-compose.yml`: `mysql`, `api`, `web`, and a reverse proxy (Caddy)
      serving both under **one origin**: `/` → web, `/api` → api.
      With one origin the refresh cookie stays `SameSite=Strict` and no CORS is needed.
      **Built (user's choice):** no Caddy yet: the web app's `/api` proxy already gives one origin, and only the web
      port (3100) is published. Caddy (HTTPS, security headers) comes with the deploy step.
- [x] Migrations run as a one-off `migrate` service (EF migrations bundle) before `api` starts
      **Built:** the API applies them on start when `Database:MigrateOnStartup` is set (only the compose file sets it);
      the MySQL accounts come from `docker/mysql/init.sh` on the database's first start.
- [ ] `docker compose up` from a fresh clone → working app at `http://localhost:3100` (files written and
      validated with `docker compose config`; not built or run yet)

## 2. Demo data

- [ ] `seed` command (`dotnet run --project src/CoreFoundry.Api -- seed`) that creates:
  - `demo@corefoundry.dev` (Owner) and `dev@corefoundry.dev` (Developer)
  - project **Bookshop** with `authors` and `books` applied and ~50 rows
  - one pending draft change (rename + add + drop) so the Review plan screen isn't empty
- [ ] Live demo resets the seed nightly (cron job or a scheduled container)

## 3. README (the most-read file in the repo)

Structure:
1. **One-line pitch** + 20-second GIF (design → review SQL → apply → add rows)
2. **Try it**: live demo link + demo credentials, and `docker compose up`
3. **Architecture**: diagram (export from `docs/corefoundry-flows.html`) + two sentences
4. **The hard problems** (reuse plan §0 and §5):
   - safe dynamic SQL for identifiers
   - no DDL transactions in MySQL → atomic statements + journal
   - draft vs. reality → plan/apply with diffing against `INFORMATION_SCHEMA`
   - rename detection via stable ids
5. **Tech stack** with a one-line "why" for each choice
6. **Testing**: what's covered, how to run, coverage badge
7. **Roadmap** (deliberately cut): foreign keys, composite indexes,
   per-tenant DB servers, code generator, realtime, CLI, Redis, background jobs
8. **Docs** links: data model, backend flows, phases

- [ ] Screenshots of: designer, review plan with a destructive warning, history with a
      failed migration, data grid

## 4. Production hardening (small but visible)

- [ ] Security headers via Caddy (HSTS, `X-Content-Type-Options`, CSP for the web app)
- [ ] `JWT_SIGNING_KEY` and DB passwords only from environment/secrets, never committed
- [ ] Health endpoint used by the Compose healthcheck and the host
- [ ] Structured logs with a correlation id returned in `ProblemDetails.traceId`
- [ ] Demo abuse limits: max projects per user, max rows per table (e.g. 1,000) on the demo

## 5. Deploy

Pick one (all run the same Compose file):
| Option | Pros | Cons |
|---|---|---|
| Small VPS (Hetzner / DigitalOcean) + Compose + Caddy | cheapest, full control, automatic HTTPS | you maintain the box |
| Fly.io / Railway | simple, managed TLS | MySQL is extra work or extra cost |
| Azure App Service + Azure Database for MySQL | matches a .NET job market | most setup, most cost |

- [ ] GitHub Actions deploy job on tag `v*` (build images → push to GHCR → deploy)

## 6. Definition of done
- [ ] Fresh clone + `docker compose up` works on a machine that has only Docker
- [ ] Live demo URL in the README works with the demo credentials
- [ ] GIF + screenshots in the README
- [ ] Repository pinned on your GitHub profile, with a description and topics
      (`dotnet`, `aspnet-core`, `mysql`, `nextjs`, `clean-architecture`)

## 7. Interview prep
- [ ] Be ready to demo in 3 minutes from the live URL
- [ ] Be ready to walk through `SchemaDiffer` and `SchemaApplier` line by line
- [ ] Prepare answers: "What would you change for 10,000 tenants?" (DB-per-server
      sharding, background apply jobs, an online DDL tool such as gh-ost) and
      "How would you add foreign keys?" (dependency ordering, drop/re-add constraints)
