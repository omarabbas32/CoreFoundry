# M0 — Setup

**Goal:** a clean repository where `docker compose up` starts MySQL with the
right accounts, the API boots and reports healthy, the Next.js app loads, and
CI is green. No features yet.

**Depends on:** nothing.

---

## 1. Repository layout

```
CoreFoundry/
├── src/
│   ├── CoreFoundry.Api/              # ASP.NET Core Web API (composition root)
│   ├── CoreFoundry.Application/      # use cases, DTOs, validators, ports
│   ├── CoreFoundry.Domain/           # entities, SchemaModel, differ — no I/O
│   └── CoreFoundry.Infrastructure/   # EF Core, MySqlConnector/Dapper, JWT
├── tests/
│   ├── CoreFoundry.UnitTests/
│   └── CoreFoundry.IntegrationTests/ # Testcontainers MySQL + WebApplicationFactory
├── web/                              # Next.js (App Router, TypeScript)
├── docker/
│   └── mysql/init/01-accounts.sh     # creates databases + cf_meta / cf_engine
├── docs/
├── .github/workflows/ci.yml
├── Directory.Build.props
├── Directory.Packages.props
├── .editorconfig
├── .env.example
├── docker-compose.yml
└── CoreFoundry.sln
```

### Project references (enforce the dependency rule)
| Project | References |
|---|---|
| Domain | — |
| Application | Domain |
| Infrastructure | Application, Domain |
| Api | Application, Infrastructure |
| UnitTests | Domain, Application |
| IntegrationTests | Api (via `WebApplicationFactory<Program>`) |

---

## 2. Tasks

### Repo
- [ ] `git init`, `.gitignore` (dotnet + node + `.env`), first commit with the plan and docs
- [ ] Create GitHub repo, push `main`, protect `main` (require CI)
- [ ] `.editorconfig` with C# conventions (file-scoped namespaces, `var` rules)

### .NET solution
- [ ] `dotnet new sln -n CoreFoundry`
- [ ] 4 `src` projects + 2 `tests` projects, add references per the table above
- [ ] `Directory.Build.props`:
  ```xml
  <Project>
    <PropertyGroup>
      <TargetFramework>net10.0</TargetFramework>
      <Nullable>enable</Nullable>
      <ImplicitUsings>enable</ImplicitUsings>
      <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
      <AnalysisLevel>latest-recommended</AnalysisLevel>
    </PropertyGroup>
  </Project>
  ```
- [ ] `Directory.Packages.props` (central package management) with:
  - EF Core provider: **check EF Core 10 support first**. Use
    `Pomelo.EntityFrameworkCore.MySql` if it has a 10.x release, otherwise
    Oracle's `MySql.EntityFrameworkCore`. Record the choice in the plan's decisions log.
  - `MySqlConnector`, `Dapper`
  - `Microsoft.AspNetCore.Authentication.JwtBearer`
  - `FluentValidation`
  - Tests: `xunit.v3`, `Shouldly`, `Testcontainers.MySql`,
    `Microsoft.AspNetCore.Mvc.Testing`
- [ ] API: `GET /health` using `AddHealthChecks()` with two checks, one per
      MySQL account (see §3)
- [ ] `ProblemDetails` enabled globally (`AddProblemDetails()` + exception handler)
- [ ] Serilog or built-in JSON console logging with request logging

### MySQL in Docker
- [ ] `docker-compose.yml` with `mysql:8.4`, a named volume, and a healthcheck
- [ ] Init script `docker/mysql/init/01-accounts.sh` (see §3)
- [ ] `.env.example` with `MYSQL_ROOT_PASSWORD`, `CF_META_PASSWORD`,
      `CF_ENGINE_PASSWORD`, `JWT_SIGNING_KEY`
- [ ] Compose services: `mysql`, `api`, `web` (api/web can be added in M5; for
      now running them locally is fine)

### Next.js
- [ ] `npx create-next-app@latest web --ts --app --eslint --tailwind --src-dir`
- [ ] Add `zod`, `react-hook-form`, `@tanstack/react-query`
- [ ] `NEXT_PUBLIC_API_URL` in `.env.local.example`
- [ ] Placeholder home page that calls `/health` and shows the result

### CI (`.github/workflows/ci.yml`)
- [ ] Job `api`: `dotnet restore` → `dotnet build -c Release` → `dotnet test`
      (Docker is available on `ubuntu-latest`, so Testcontainers works)
- [ ] Job `web`: `npm ci` → `npm run lint` → `npm run build`

---

## 3. Key specs

### Account setup script
Docker's MySQL image runs `*.sh` files in `/docker-entrypoint-initdb.d`, so
passwords come from environment variables instead of being hard-coded in SQL.

```bash
#!/bin/bash
set -euo pipefail
mysql -uroot -p"$MYSQL_ROOT_PASSWORD" <<SQL
CREATE DATABASE IF NOT EXISTS corefoundry
  CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;

CREATE USER IF NOT EXISTS 'cf_meta'@'%'   IDENTIFIED BY '${CF_META_PASSWORD}';
CREATE USER IF NOT EXISTS 'cf_engine'@'%' IDENTIFIED BY '${CF_ENGINE_PASSWORD}';

GRANT ALL PRIVILEGES ON corefoundry.* TO 'cf_meta'@'%';
GRANT CREATE, ALTER, DROP, INDEX, SELECT, INSERT, UPDATE, DELETE
  ON \`cf\\_p\\_%\`.* TO 'cf_engine'@'%';
FLUSH PRIVILEGES;
SQL
```
`_` is a wildcard in grant patterns, so it is escaped (`cf\_p\_%`).

### Connection strings (appsettings)
```json
"ConnectionStrings": {
  "Metadata": "Server=localhost;Database=corefoundry;User=cf_meta;Password=...",
  "Engine":   "Server=localhost;User=cf_engine;Password=...;AllowUserVariables=true"
}
```
The `Engine` string has **no default database**. Queries always name the
database explicitly: `` `cf_p_7`.`books` ``.

---

## 4. Definition of done
- [ ] `docker compose up -d mysql` creates both accounts.
      `cf_engine` **cannot** run `SELECT * FROM corefoundry.Users` (verify by hand once).
- [ ] `dotnet run --project src/CoreFoundry.Api` → `GET /health` returns 200
      with both checks healthy
- [ ] `npm run dev` in `web/` shows the health result
- [ ] CI is green on `main`
- [ ] One smoke integration test starts MySQL via Testcontainers and hits `/health`

## 5. Interview talking points
- Why central package management and warnings-as-errors from day one
- Least-privilege DB accounts before a single feature exists
