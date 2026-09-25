# CoreFoundry API. Build context: the repository root (see docker-compose.yml).

# Build: restore first (cached while only code changes), then publish.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/CoreFoundry.Domain/CoreFoundry.Domain.csproj src/CoreFoundry.Domain/
COPY src/CoreFoundry.Application/CoreFoundry.Application.csproj src/CoreFoundry.Application/
COPY src/CoreFoundry.Infrastructure/CoreFoundry.Infrastructure.csproj src/CoreFoundry.Infrastructure/
COPY src/CoreFoundry.Api/CoreFoundry.Api.csproj src/CoreFoundry.Api/
RUN dotnet restore src/CoreFoundry.Api/CoreFoundry.Api.csproj
COPY src/ src/
RUN dotnet publish src/CoreFoundry.Api/CoreFoundry.Api.csproj -c Release -o /app --no-restore

# Run: the ASP.NET Core runtime only, as the image's non-root user.
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "CoreFoundry.Api.dll"]
