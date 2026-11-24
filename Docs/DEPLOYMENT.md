# Deployment guide

## 1. Prerequisites

| Component | Version |
|---|---|
| .NET SDK (build) | 8.0.x |
| Runtime on the server | ASP.NET Core Runtime 8.0 (for IIS: the **ASP.NET Core 8.0 Hosting Bundle**) |
| Database | SQL Server 2017+ / Azure SQL (the existing ITRM database or an empty one) |
| NuGet access | nuget.org **or an internal feed/mirror** (Artifactory, Azure Artifacts, Nexus) for: `Microsoft.EntityFrameworkCore.SqlServer`, `Microsoft.EntityFrameworkCore.Design`, `FluentValidation.AspNetCore`, `Microsoft.Extensions.Hosting.WindowsServices`, and for tests `xunit`, `Microsoft.NET.Test.Sdk` |

> If nuget.org is blocked by the corporate proxy, add the internal feed:
> `dotnet nuget add source https://<your-feed>/nuget/v3/index.json -n corp` (or a `nuget.config` next to `ItrmTracker.sln`).

## 2. Build, test, publish

```powershell
cd ModernizedApp
dotnet restore
dotnet build -c Release
dotnet test Tests/ItrmTracker.Tests.csproj -c Release
dotnet publish Web/ItrmTracker.Web.csproj -c Release -o .\publish
```

Optional – re-run the parity check against the original JavaScript (needs Node.js and the original app folder):

```powershell
dotnet run --project tools/SlaParityCheck -- "<path to itrm-tracker-app-master>" node 400
```

## 3. Database

### 3.1 Existing production database (the normal case)
The schema already exists. The two shipped migrations execute the original **idempotent** scripts, so applying them
only creates `dbo.__EFMigrationsHistory` and records the migrations:

```powershell
# either let the app do it at startup (Database:ApplyMigrationsOnStartup = true, the default) …
# … or apply explicitly with a DBA account:
dotnet ef database update -p Infrastructure -s Web --connection "<connection string>"
# or generate a script for review:
dotnet ef migrations script -p Infrastructure -s Web --idempotent -o itrm-migrations.sql
```

Take a full backup first. No table, column or data is modified.

### 3.2 New database
Create an empty database (or give the app login `CREATE DATABASE` rights) and start the app; migrations create the
schema, then `DatabaseInitializer` seeds the lookup tables (only when empty) and the first administrator (only when
`dbo.users` is empty, using `Database:SeedAdminPassword`).

### 3.3 Required SQL permissions
Application login: `db_datareader`, `db_datawriter`, `EXECUTE`; plus `db_ddladmin` only if the app applies migrations
at startup. Recommended: apply migrations with a DBA account and set `Database:ApplyMigrationsOnStartup=false`.
Query console: optionally a separate `db_datareader`-only login in `ConnectionStrings:QueryConsole`.

### 3.4 Rolling back the SLA engine tables
`dotnet ef database update BaselineSchema -p Infrastructure -s Web` runs the original rollback script
(`001_sla_effort_engine.down.sql`). Set `Sla:BackfillOnStartup=false` afterwards, otherwise nothing recreates them
until the next `database update`.

## 4. Hosting options

### 4.1 IIS (replaces the URL Rewrite + ARR reverse proxy setup)
1. Install the **ASP.NET Core 8.0 Hosting Bundle** on the server (restart IIS: `iisreset`).
2. Create an application pool: *.NET CLR version* = **No Managed Code**, identity = a domain/service account that can reach SQL Server (or use SQL authentication in the connection string).
3. Create a site (or application) pointing to the `publish` folder. `dotnet publish` generates `web.config` with the `AspNetCoreModuleV2` handler (in-process hosting); no URL Rewrite/ARR needed. Unlike the Node version, the app may also run as a sub-application.
4. Configure secrets as environment variables on the app pool (IIS Manager → Configuration Editor → `system.webServer/aspNetCore/environmentVariables`) or in `appsettings.Production.json`:
   `ConnectionStrings__ItrmDatabase`, `Database__SeedAdminPassword` (first start only).
5. Bind HTTPS and set `Security:CookieSecurePolicy=Always`.
6. Browse to `/healthz` → `{"ok":true}`.

### 4.2 Windows Service (replaces the scheduled task)
```powershell
dotnet publish Web/ItrmTracker.Web.csproj -c Release -o C:\Apps\itrm-tracker
.\deploy\install-windows-service.ps1 -AppPath C:\Apps\itrm-tracker -Urls http://0.0.0.0:5000
# remove: .\deploy\install-windows-service.ps1 -Remove
```
The service restarts automatically after a failure (1 minute delay). Put it behind IIS/ARR or a load balancer for TLS.

### 4.3 Docker
```powershell
# .env next to docker-compose.yml: SQL_PASSWORD=..., SEED_ADMIN_PASSWORD=...
docker compose up --build
```
The app listens on port 8080 inside the container (non-root user). Point `ConnectionStrings__ItrmDatabase` at the
real SQL Server for production and remove the `sqlserver` service.

## 5. Coexistence with the Node.js application (parallel run)
Both applications can use the same database at the same time:

* No schema change; the EF migrations also mark `001_sla_effort_engine` in `dbo.schema_migrations`.
* Set `Security:PasswordHashFormat=LegacyScrypt` during the parallel run so passwords changed/created in the new app
  can still be verified by the Node.js app. Switch to `Identity` (PBKDF2) after the Node.js app is retired; legacy
  hashes are then upgraded transparently at each user's next login.
* Run the SLA backfill in **one** application only (`SLA_BACKFILL_ON_START=false` in Node or `Sla:BackfillOnStartup=false` here) to avoid duplicate work (it is idempotent and lock-protected, so this is an optimisation).
* Sessions are not shared: users sign in separately to each application.

## 6. Post-deployment smoke test
1. `/healthz` returns 200.
2. Sign in with an existing legacy account (verifies scrypt compatibility).
3. Business Requests: list loads with SLA columns; filter + sort + export `.xlsx`; open a record (history, comments, Stage SLA panel).
4. Create a test request with pipeline dates out of order → the original validation messages are shown.
5. Stage SLA: start / hold / resume a stage on a test request; check the SLA history and the Stage SLA dashboard.
6. Sign in as a `read_only` user: write buttons hidden, direct POST to `/BusinessRequests/Create` returns 403; comments and follow still work.
7. Import a small CSV (preview → compare → merge) and check Import history.
8. Query console: `SELECT TOP 5 br_no FROM business_requests` works; `SELECT * FROM users` is rejected.
9. Check logs for `ItrmTracker.Audit` entries.

## 7. Operations
* **Logs:** standard `ILogger` providers (console / EventLog / Application Insights / Serilog can be plugged in). Audit events use category `ItrmTracker.Audit`, event id 9000.
* **Health:** `/healthz` (anonymous) checks the database.
* **Backups:** unchanged – the database remains the single source of truth.
* **Scaling out:** add an ASP.NET Core Data Protection key ring shared by all instances (file share / Redis / SQL) so authentication cookies and antiforgery tokens work across servers; import wizard state is in-memory, so use sticky sessions or move it to a distributed cache.
