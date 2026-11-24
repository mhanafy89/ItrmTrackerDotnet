# Configuration guide

Configuration uses the standard ASP.NET Core providers, in increasing priority: `appsettings.json` →
`appsettings.{Environment}.json` → user secrets (Development) → environment variables (`Section__Key`) → command line.
**Never commit secrets**; use environment variables, IIS app-pool variables, Docker secrets, Azure Key Vault or
`dotnet user-secrets` (the Web project has a `UserSecretsId`).

## 1. Settings

| Key | Default | Description | Legacy equivalent |
|---|---|---|---|
| `ConnectionStrings:ItrmDatabase` | *(required)* | SQL Server connection string | `SQL_SERVER`, `SQL_PORT`, `SQL_DATABASE`, `SQL_USER`, `SQL_PASSWORD`, `SQL_ENCRYPT`, `SQL_TRUST_SERVER_CERTIFICATE`, `SQL_TRUSTED_CONNECTION`, `SQL_POOL_MAX` |
| `ConnectionStrings:QueryConsole` | empty | Optional read-only login for the query console (recommended: `db_datareader` only) | – |
| `Database:ApplyMigrationsOnStartup` | `true` | Apply pending EF migrations at startup | always on (schema.sql + migrations) / `SKIP_MIGRATIONS` |
| `Database:SeedOnStartup` | `true` | Seed empty lookup tables and the first admin | `node db/seed.js` |
| `Database:SeedAdminUsername` | `admin` | First administrator username | `SEED_ADMIN_USERNAME` |
| `Database:SeedAdminPassword` | empty | First administrator password; no admin is created when empty (Development falls back to `admin123`) | `SEED_ADMIN_PASSWORD` (default `admin123`) |
| `Security:PasswordHashFormat` | `Identity` | `Identity` (PBKDF2) or `LegacyScrypt` (Node-compatible) for new hashes | – |
| `Security:UpgradeLegacyHashesOnLogin` | `true` | Re-hash scrypt passwords to Identity format on login (only with `Identity`) | – |
| `Security:SessionDays` | `7` | Authentication cookie lifetime (fixed, not sliding) | 7-day session TTL |
| `Security:CookieSecurePolicy` | `SameAsRequest` | `Always` behind HTTPS, `SameAsRequest`, or `None` | `COOKIE_SECURE` |
| `Sla:EngineSyncEnabled` | `true` | Reconcile the stage engine after request-form saves | `SLA_ENGINE_SYNC` |
| `Sla:BackfillOnStartup` | `true` | Create missing SLA tracking in the background at startup | `SLA_BACKFILL_ON_START` |
| `Sla:BusinessTimeZoneId` | empty (= server local) | Time zone that defines a calendar day for SLA, follow-up and "today" (e.g. `Arab Standard Time` / `Asia/Riyadh`) | server local time (implicit) |
| `QueryConsole:BlockedTables` | `["users","sessions"]` | Tables the console refuses to query | – (everything allowed) |
| `QueryConsole:MaxRows` | `10000` | Row cap (0 = unlimited) | unlimited |
| `QueryConsole:CommandTimeoutSeconds` | `30` | Command timeout | `requestTimeout` 30 s |
| `Logging:LogLevel:*` | see file | Log levels; keep `ItrmTracker.Audit` at `Information` | console output |
| `AllowedHosts` | `*` | Host filtering – set to the public host name in production | – |
| `ASPNETCORE_URLS` / `--urls` | `http://localhost:5000` | Listening addresses (service/Docker) | `PORT` |
| `ASPNETCORE_ENVIRONMENT` | `Production` | `Development` enables the developer exception page and the dev admin password fallback | – |

## 2. Connection string examples

```text
# SQL authentication (legacy default)
Server=sql01,1433;Database=itrm_tracker;User Id=itrm_app;Password=***;Encrypt=true;TrustServerCertificate=false;Application Name=ITRM Tracker

# Named instance
Server=sql01\ITRM;Database=itrm_tracker;User Id=itrm_app;Password=***;Encrypt=true

# Windows authentication (legacy SQL_TRUSTED_CONNECTION=true) – the app pool / service account must have a SQL login
Server=sql01;Database=itrm_tracker;Integrated Security=true;Encrypt=true

# Self-signed certificate (dev only; legacy SQL_TRUST_SERVER_CERTIFICATE=true)
...;Encrypt=true;TrustServerCertificate=true

# Connection pool size (legacy SQL_POOL_MAX)
...;Max Pool Size=10
```

## 3. Setting secrets

```powershell
# Development
cd Web
dotnet user-secrets set "ConnectionStrings:ItrmDatabase" "Server=localhost;Database=itrm_tracker;User Id=sa;Password=...;TrustServerCertificate=true"

# Windows service / server (machine-level environment variables)
[Environment]::SetEnvironmentVariable("ConnectionStrings__ItrmDatabase", "<conn>", "Machine")
[Environment]::SetEnvironmentVariable("Database__SeedAdminPassword", "<strong password>", "Machine")
```

Remove `Database__SeedAdminPassword` after the first start (it is only used while `dbo.users` is empty).

## 4. Front-end preferences (browser storage)
Column choices (`itrm_visible_cols`, `itrm_incident_cols`) and density (`itrm_density`) use the same localStorage keys
as the original UI, so users keep their preferences when the app is served from the same origin. Saved filters are
stored under `itrm_saved_filters_v2` (new format: the filter query string).
