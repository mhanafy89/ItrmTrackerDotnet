# ITRM Tracker – ASP.NET Core MVC (.NET 8) edition

Modernized version of the Node.js **ITRM Business Request Tracker** (`../`), rebuilt as an ASP.NET Core MVC application
with Clean Architecture, EF Core and SQL Server. It runs against the **existing database unchanged** and preserves the
original business rules (proven by differential testing against the original JavaScript).

## Solution

| Project | Purpose |
|---|---|
| `Domain/` | Entities for every table, business constants (roles, statuses, SLA flags, rules) |
| `Application/` | Use-case services: business requests & incidents, SLA engine + service, legacy SLA, validation, import, reporting (dashboard/follow-up), lookups, users, query console, export |
| `Infrastructure/` | EF Core `ItrmDbContext`, migrations, repositories, ASP.NET Core Identity store (legacy scrypt-compatible), startup seeding, SLA backfill background service |
| `Web/` | MVC controllers, view models, FluentValidation validators, Razor views (Bootstrap 5), JSON API (`/api/...`, `/healthz`) |
| `Tests/` | xUnit tests (engine, service, validation, import, security) |
| `tools/SlaParityCheck/` | Differential test harness: runs the original JS modules and the C# port on the same randomized inputs |
| `deploy/`, `Dockerfile`, `docker-compose.yml` | Hosting |
| `Docs/` | Architecture, database mapping, migration summary & feature matrix, deployment, configuration, assumptions & risks |

## Quick start (development)

```powershell
cd ModernizedApp
dotnet restore                     # needs nuget.org or an internal feed (see Docs/DEPLOYMENT.md §1)
cd Web
dotnet user-secrets set "ConnectionStrings:ItrmDatabase" "Server=localhost;Database=itrm_tracker;User Id=sa;Password=...;TrustServerCertificate=true"
dotnet run                         # applies migrations, seeds an empty DB (dev admin: admin / admin123)
```

Then browse to the URL shown in the console and sign in with an existing account (legacy passwords work).

```powershell
dotnet test ..\Tests\ItrmTracker.Tests.csproj
dotnet run --project ..\tools\SlaParityCheck -- "<path to the original itrm-tracker-app-master>" node 400
```

## Documentation
* [Architecture](Docs/ARCHITECTURE.md) – discovery of the legacy app and the target architecture (with diagram)
* [Database mapping](Docs/DATABASE-MAPPING.md) – tables ↔ entities, relationships, migrations, recommended DB improvements
* [Migration summary](Docs/MIGRATION-SUMMARY.md) – verification results and the **feature mapping matrix**
* [Deployment guide](Docs/DEPLOYMENT.md) – IIS, Windows Service, Docker, database, parallel run, smoke test
* [Configuration guide](Docs/CONFIGURATION.md) – every setting and its legacy `.env` equivalent
* [Assumptions & risks](Docs/ASSUMPTIONS-RISKS.md) – flagged deviations, items for business review, future improvements
