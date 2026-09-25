using System.Text;
using CoreFoundry.Application.Export;
using CoreFoundry.Domain.Schema;

namespace CoreFoundry.Infrastructure.Export;

/// <summary>Docker files and the README of an exported backend.</summary>
internal static class DeployFiles
{
    public static IEnumerable<GeneratedFile> For(ExportModel model)
    {
        var n = model.Solution;
        var slug = model.Slug;
        string[] projects = ["Domain", "Application", "Infrastructure", "Api"];

        yield return new("Dockerfile", $$"""
            # Build: restore first (cached while only code changes), then publish.
            FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
            WORKDIR /src
            COPY global.json Directory.Build.props Directory.Packages.props ./
            {{string.Join("\n", projects.Select(project => $"COPY src/{n}.{project}/{n}.{project}.csproj src/{n}.{project}/"))}}
            RUN dotnet restore src/{{n}}.Api/{{n}}.Api.csproj
            COPY src/ src/
            RUN dotnet publish src/{{n}}.Api/{{n}}.Api.csproj -c Release -o /app --no-restore

            # Run: the ASP.NET Core runtime only, as a non-root user.
            FROM mcr.microsoft.com/dotnet/aspnet:10.0
            WORKDIR /app
            COPY --from=build /app .
            ENV ASPNETCORE_HTTP_PORTS=8080
            EXPOSE 8080
            USER $APP_UID
            ENTRYPOINT ["dotnet", "{{n}}.Api.dll"]

            """);

        yield return new("docker-compose.yml", $$"""
            # The API and its MySQL database. Values come from .env (copy .env.example).
            services:
              db:
                image: mysql:8.4
                environment:
                  MYSQL_DATABASE: {{slug}}
                  MYSQL_USER: {{slug}}
                  MYSQL_PASSWORD: ${MYSQL_PASSWORD:?Set MYSQL_PASSWORD in .env}
                  MYSQL_ROOT_PASSWORD: ${MYSQL_ROOT_PASSWORD:?Set MYSQL_ROOT_PASSWORD in .env}
                volumes:
                  - db-data:/var/lib/mysql
                healthcheck:
                  test: ["CMD-SHELL", "mysqladmin ping -h localhost -uroot -p$$MYSQL_ROOT_PASSWORD --silent"]
                  interval: 5s
                  timeout: 5s
                  retries: 30

              api:
                build: .
                ports:
                  - "${API_PORT:-8080}:8080"
                environment:
                  ConnectionStrings__Default: Server=db;Port=3306;Database={{slug}};User={{slug}};Password=${MYSQL_PASSWORD}
                  Jwt__SigningKey: ${JWT_SIGNING_KEY:?Set JWT_SIGNING_KEY in .env (at least 32 characters)}
                  Database__MigrateOnStartup: "true"
                  Swagger__Enabled: "${SWAGGER_ENABLED:-true}"
                depends_on:
                  db:
                    condition: service_healthy
                restart: unless-stopped

            volumes:
              db-data:

            """);

        yield return new(".env.example", """
            # Copy to .env and change every value. .env is ignored by git.
            MYSQL_PASSWORD=change-me
            MYSQL_ROOT_PASSWORD=change-me-too
            # At least 32 random characters, e.g. `openssl rand -base64 48`
            JWT_SIGNING_KEY=change-me-to-a-long-random-secret-of-32-chars-or-more
            API_PORT=8080
            SWAGGER_ENABLED=true

            """);

        yield return new("README.md", Readme(model));
    }

    private static string Readme(ExportModel model)
    {
        var n = model.Solution;
        var tables = new StringBuilder();
        foreach (var entity in model.Entities)
        {
            tables.Append($"\n### `{entity.Table}`\n\n");
            tables.Append($"`GET /api/{entity.Table}` · `GET /api/{entity.Table}/{{id}}` · `POST /api/{entity.Table}` · `PUT /api/{entity.Table}/{{id}}` · `DELETE /api/{entity.Table}/{{id}}`\n\n");
            tables.Append("| Field | Type | Required | Notes |\n|---|---|---|---|\n| `id` | BigInt | set by the database | |\n");
            foreach (var property in entity.Properties)
            {
                var notes = new List<string>();
                if (property.Reference is { } reference)
                {
                    notes.Add($"id of a `{reference.TargetTable}` row, on delete {reference.OnDelete}");
                }

                if (property.IsUnique)
                {
                    notes.Add("unique");
                }

                if (property.Default is { } value)
                {
                    notes.Add($"default `{value.Canonical}`");
                }

                if (property.IsNullable)
                {
                    notes.Add("may be null");
                }

                if (property.Type.DataType == DataType.Decimal)
                {
                    notes.Add("returned as a string");
                }

                tables.Append($"| `{property.Column}` | {property.Type} | {(CSharp.IsRequiredOnInsert(property) ? "yes" : "no")} | {string.Join(", ", notes)} |\n");
            }
        }

        var access = new StringBuilder("| Table | Read | Write |\n|---|---|---|\n");
        foreach (var entity in model.Entities)
        {
            access.Append($"| `{entity.Table}` | {AccessName(entity.Read)} | {AccessName(entity.Write)} |\n");
        }

        var wideDecimals = model.Entities
            .SelectMany(entity => entity.Properties.Where(property => property.Type.Precision > 28).Select(property => $"`{entity.Table}.{property.Column}`"))
            .ToList();
        var decimalNote = wideDecimals.Count == 0
            ? ""
            : $"\n- **Wide decimals:** {string.Join(", ", wideDecimals)} can hold more digits than .NET's `decimal` (28–29). " +
              "Values beyond that can't be read into C#.\n";
        var example = model.Entities.Count > 0 ? model.Entities[0].Table : "{table}";

        return $$"""
            # {{model.ProjectName}} API

            A .NET 10 backend generated by **CoreFoundry** from schema version {{model.SchemaVersion}} of the
            project "{{model.ProjectName}}". It's yours now: change anything.

            - **Clean Architecture:** `{{n}}.Domain` (entities) → `{{n}}.Application` (services, DTOs, validation) →
              `{{n}}.Infrastructure` (EF Core, MySQL, JWT) → `{{n}}.Api` (controllers, Swagger UI).
            - **Database:** MySQL 8.4+ through EF Core. The schema comes from the migration in
              `src/{{n}}.Infrastructure/Persistence/Migrations`.
            - **Auth:** register or log in to get an access token, then send `Authorization: Bearer <token>`.

            ## Run with Docker

            ```bash
            cp .env.example .env    # then change the passwords and the signing key
            docker compose up --build
            ```

            The API listens on http://localhost:8080 and creates its tables on start. Swagger UI: http://localhost:8080/swagger

            ## Run locally

            Needs the .NET 10 SDK and a MySQL 8.4+ server.

            1. Put your connection string in `src/{{n}}.Api/appsettings.Development.json`, or keep it out of the file:
               ```bash
               dotnet user-secrets init --project src/{{n}}.Api
               dotnet user-secrets set "ConnectionStrings:Default" "Server=localhost;Port=3306;Database={{model.Slug}};User=root;Password=..." --project src/{{n}}.Api
               ```
            2. Start it. In Development the tables are created on start (`Database:MigrateOnStartup`):
               ```bash
               dotnet run --project src/{{n}}.Api
               ```
            3. Open http://localhost:5080/swagger.

            `appsettings.Development.json` contains a signing key generated for this download. Use it for local
            development only; in production set `Jwt__SigningKey` (at least 32 characters) in the environment.

            ## Use the API

            ```bash
            curl -s -X POST http://localhost:8080/api/auth/register -H "Content-Type: application/json" \
              -d '{"email":"me@example.com","password":"a long password"}'
            # → {"accessToken":"eyJ…","expiresAt":"…"}
            TOKEN=eyJ…
            curl -s "http://localhost:8080/api/{{example}}?page=1&pageSize=25&sort=-id" -H "Authorization: Bearer $TOKEN"
            ```

            - Lists: `?page=` (from 1), `?pageSize=` (1–100, default 25), `?sort=column` or `?sort=-column`; ties by id.
              The answer is `{ "items": [...], "page": 1, "pageSize": 25, "total": 42 }`.
            - `POST` adds a row (201). Fields left out get NULL or the column's default.
            - `PUT` replaces the whole row. Fields left out get the column's default, or NULL.
            - JSON field names are the column names. Decimals are returned as strings (no digits are lost in
              JavaScript), date-times have no offset (one sent with an offset is stored in UTC), Json columns take any JSON.
            - Errors are ProblemDetails. Invalid fields: 400 with `errors` per field. Duplicate unique value or a row
              that other rows still reference: 409. A reference to a missing row: 400 on that field.
            {{decimalNote}}
            ## Access

            **Public**: anyone, no token. **Signed-in**: any registered user. **Admin**: the `Admin` role only.
            `List`/`Get` need Read; `Create`/`Replace`/`Delete` need Write.

            {{access}}
            **Becoming Admin:** The first account registered becomes `Admin`; every later one is a `User`. An Admin
            lists the accounts with `GET /api/auth/users` and promotes or demotes one with
            `PUT /api/auth/users/{id}/role` and `{ "role": "Admin" }` or `{ "role": "User" }` (the last Admin can't be
            demoted: 409). The role travels in the token, so a changed role counts from the account's next login.

            **No ownership yet:** whoever may write a table can set any column value. On a Signed-in table, a customer
            creating an order could set another customer's id. Keep such tables Admin, or add your own checks.

            {{Realtime(model)}}
            ## Tables
            {{tables}}
            ## Changing the schema

            This project now owns its schema. Change an entity and its configuration, then add a migration:

            ```bash
            dotnet tool restore
            dotnet ef migrations add AddSomething --project src/{{n}}.Infrastructure --startup-project src/{{n}}.Api
            dotnet ef database update --project src/{{n}}.Infrastructure --startup-project src/{{n}}.Api
            ```

            Accounts live in the `cf_users` table.

            """;
    }

    /// <summary>The README's Realtime section (phase-9-realtime-export.md §3): subscribing, reconnecting, CORS and the limits.</summary>
    private static string Realtime(ExportModel model)
    {
        var subscribers = new StringBuilder("| Table | Who may subscribe |\n|---|---|\n");
        foreach (var entity in model.Entities)
        {
            subscribers.Append($"| `{entity.Table}` | {AccessName(entity.Read)} |\n");
        }

        return $$"""
            ## Realtime

            The API pushes a notification when a row is added, replaced or deleted through it. Connect with SignalR to
            `/hubs/realtime` and subscribe per table: each change arrives as a `change` event with `{ table, operation, id }`
            (`operation` is `insert`, `update` or `delete`). It's a hint, not the row: refetch what you show from the REST API.

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

            A table's Read level (see Access) decides who may subscribe to it. Public tables need no token; for the others
            pass `accessTokenFactory` with a token from `/api/auth/login`. Subscribing to a table you may not read, or to a
            name that isn't a table here, makes `invoke("Subscribe", …)` reject with the reason.

            {{subscribers}}
            - **Reconnecting:** a reconnect is a new connection, and the server forgets its subscriptions. Subscribe again in
              `onreconnected` (as above) and refetch: events sent while disconnected are lost, nothing is replayed.
            - **Access is checked when subscribing.** A user demoted by an Admin keeps receiving events until the connection
              closes; a connection made with a token closes when that token expires. Events carry only ids.
            - **CORS:** a browser frontend on another origin must be listed in `Cors:AllowedOrigins` (`appsettings.json`, or
              `Cors__AllowedOrigins__0=https://app.example.com` in the environment). Empty, the default: no CORS at all. The
              list covers the REST API too, and allows credentials, so list exact origins.
            - **The token travels in the URL:** browsers can't set headers on a WebSocket, so the client sends the token as
              `?access_token=` (read under `/hubs/` only). It can appear in proxy access logs; keep it out of them.
            - **Reverse proxies** in front of the API must forward WebSocket upgrades (Caddy does; nginx needs the `Upgrade`
              and `Connection` headers).
            - **One instance only:** events reach the clients connected to the instance that saved the row. Several
              instances need a SignalR backplane (e.g. Redis), which isn't set up.
            - **Only this API's writes send events.** Rows changed in the database directly, by other clients, scripts or
              another service, send none.
            - **Cascades send no event.** Deleting a row whose referencing rows the database deletes or sets to NULL (on
              delete Cascade or SetNull) publishes only that row's delete.

            """;
    }

    private static string AccessName(AccessLevel level) => level switch
    {
        AccessLevel.Public => "Public",
        AccessLevel.SignedIn => "Signed-in",
        AccessLevel.Admin => "Admin",
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, null),
    };
}
