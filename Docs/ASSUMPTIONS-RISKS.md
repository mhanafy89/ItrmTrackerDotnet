# Assumptions, risks and recommended improvements

## 1. Assumptions

1. The production database is the SQL Server schema defined by `db/schema.sql` + `001_sla_effort_engine.sql`; no
   out-of-band schema changes exist. (The EF model and migrations mirror these scripts.)
2. The original SQLite → SQL Server move (`db/migrate-from-sqlite.js`) has already been completed; the tool is not needed.
3. The server's local time zone is the business time zone (as in the Node app). If the server runs in UTC, set
   `Sla:BusinessTimeZoneId`, otherwise "today", follow-up days and SLA calendar days shift.
4. Users access the app through a browser; the documented SLA REST API is the only programmatic integration in use.
5. `read_write` users are the administrators (there is no separate admin role, as before).
6. A corporate NuGet feed/mirror will be available to restore packages (nuget.org downloads were blocked in the build environment).
7. Usernames are compared case-insensitively (the legacy lookup relied on the database collation, which is normally CI).

## 2. Risks and mitigations

| # | Risk | Impact | Mitigation |
|---|---|---|---|
| R1 | Packages could not be restored in the generation environment, so the app has not been **run** end-to-end nor `dotnet test` executed with real xUnit/EF Core | Medium | Whole solution compiled against faithful API stubs (0 errors); business logic proven equal to the original by differential testing; 62 tests executed offline. Run DEPLOYMENT.md §2 and §6 once restore works. |
| R2 | EF Core model snapshot not generated | Low | Not needed to apply the shipped migrations; procedure in DATABASE-MAPPING.md §5 before the next migration. |
| R3 | Password hashes upgraded to PBKDF2 can no longer be verified by the Node app | Medium during parallel run | `Security:PasswordHashFormat=LegacyScrypt` until the Node app is retired. |
| R4 | Parallel run: both apps run SLA reconciliation/backfill | Low | Operations are idempotent and lock-protected; disable backfill in one app. |
| R5 | External scripts that called the old JSON **write** endpoints (`POST/PUT/DELETE /api/business-requests…`, `/api/lookups…`, `/api/import/commit`, `/api/auth/users…`) | Medium if such scripts exist | Only read endpoints + the SLA endpoints are exposed as JSON; writes go through MVC forms with antiforgery. Inventory consumers before cut-over; add token-authenticated API endpoints if needed. |
| R6 | Register screens load all records (as the original did) | Medium at high volumes | Fine for tracker-sized data (thousands of rows); see improvement I1 (server-side paging). |
| R7 | Import wizard state is held in server memory | Low | Sticky sessions or a distributed cache for multi-instance deployments. |
| R8 | Time-zone differences between server and users | Low | `Sla:BusinessTimeZoneId`; timestamps are rendered in the viewer's local time by the browser. |
| R9 | Cookie / antiforgery keys on multiple servers | Medium when scaled out | Configure a shared Data Protection key ring. |

## 3. Flagged deviations and uncertain business logic

Every item below was a conscious decision; items marked **REVIEW** need business confirmation.

| # | Area | Original behaviour | New behaviour | Why / review |
|---|---|---|---|---|
| D1 | Date validation | `YYYY-MM-DD` strings with impossible days (e.g. `2024-02-30`) passed validation and then failed in SQL Server with a 500 error | Rejected with the normal "must be a valid date (YYYY-MM-DD)" message | Bug fix; same rule otherwise. |
| D2 | Import date parsing fallback | Unrecognised formats parsed by JavaScript `new Date(s)` and converted to UTC – could shift the date by one day on servers east of UTC; D/M/Y values like `31/02/2024` rolled over | .NET invariant parsing without time-zone shift; impossible D/M/Y dates reported as not recognizable | Bug fix. All documented formats (ISO, Excel serial, D/M/Y, M/D/Y when unambiguous, Y/M/D) behave identically (parity-tested). |
| D3 | Query console | Any SELECT on any table (including `users.password_hash`), unlimited rows, export format of the original button | `users`/`sessions` blocked (configurable), 10 000-row cap (configurable), every statement in a rolled-back transaction, optional read-only connection, CSV export | **REVIEW** – security hardening. Set `QueryConsole:BlockedTables=[]` and `MaxRows=0` to restore the original behaviour. |
| D4 | First administrator | Created with `admin`/`admin123` when no users exist | Created only if `Database:SeedAdminPassword` is configured (Development falls back to `admin123`) | Secure configuration. |
| D5 | Lookup seeding | `db/seed.js` (manual) inserted every missing seed value | Startup seeding fills **empty** tables only | Avoids re-adding values users deleted. |
| D6 | Excel export | SpreadsheetML 2003 `.xls` (Excel shows a format warning) | Real `.xlsx`, same columns/values, bold frozen header | Improvement. |
| D7 | Saved filters | Stored filter state objects in `itrm_saved_filters` | Stored as query strings in `itrm_saved_filters_v2` | Users need to re-save their filters once. |
| D8 | Delete audit | A "delete" history row is written and then removed by the cascade together with the record | Same (preserved) + a durable entry in the structured audit log | **REVIEW** – see DATABASE-MAPPING improvement 1. |
| D9 | Import and SLA engine | Imported/updated rows are **not** reconciled with the stage engine immediately (tracking is created at the next startup backfill or first view) | Preserved | **REVIEW** – consider calling the reconciliation after import. |
| D10 | "Closed" statuses | The UI treats `completed, compeleted, cancelled, canceled` as closed; the legacy SLA calculation also treats `closed` as done | Both lists preserved exactly (`TrackerRules.ClosedStatuses`, `SlaDefaults`) | **REVIEW** – a status literally named "Closed" is open in registers but "done" for the legacy SLA. |
| D11 | JSON write API | REST endpoints for every write | MVC form posts (antiforgery protected); SLA `stage-status` JSON endpoint kept (needs `X-CSRF-TOKEN`) | See R5. |
| D12 | created_by / updated_by fallback | Logged-in name, else `changed_by` body field, `X-User` header or the OS user name | Logged-in display name (authentication is mandatory) | The fallbacks were only reachable without login. |
| D13 | Comment length | 2 000 characters enforced in the browser only | Also enforced on the server | Consistency. |
| D14 | Dashboard follow-up KPIs | Used the Follow-up tab's "Include incidents" checkbox state (default on) | Always include incidents (the default) | **REVIEW** if the checkbox influence was intended. |
| D15 | E-mail draft rows | Only selected rows that were *visible* under the current filters | All selected rows (selection is cleared when the page is re-filtered) | Equivalent in practice. |
| D16 | Sessions | Server-side `dbo.sessions` rows | Encrypted authentication cookie re-validated against `dbo.users` on every request | Same effect (deactivation is immediate); `dbo.sessions` is no longer written. |
| D17 | Sorting / filtering location | In the browser after downloading every row | On the server (same rules); sort/filter state is in the URL (bookmarkable) | Improvement. |
| D18 | 7-day weekend | Legacy SLA loop never terminated / crashed | Treated as "no weekend" (the settings validation already forbids it) | Defensive. |

## 4. Recommended future improvements

| # | Improvement |
|---|---|
| I1 | Server-side paging and SQL-side filtering for the registers (keep the SLA computation for the visible page or materialise it). |
| I2 | Optimistic concurrency (`rowversion`) on requests and incidents to prevent lost updates when two users edit the same record. |
| I3 | Token-based API (OAuth2 / Azure AD app registrations) for integrations, plus OpenAPI documentation. |
| I4 | Single sign-on with Azure AD / Entra ID (OpenID Connect) mapped to the read-only / read-write roles. |
| I5 | Account lockout and MFA (requires adding lockout/security-stamp columns to `dbo.users`). |
| I6 | Durable audit store (table or SIEM sink for the `ItrmTracker.Audit` log category). |
| I7 | Distributed cache + shared Data Protection keys for multi-instance hosting. |
| I8 | Health checks for readiness/liveness (`Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore`) and OpenTelemetry tracing. |
| I9 | Background reconciliation of imported records (see D9) and a scheduled nightly SLA backfill/consistency check. |
| I10 | Notifications (e-mail/Teams) for overdue follow-ups and SLA breaches – the data is already computed. |
| I11 | Integration tests with Testcontainers (SQL Server) and Playwright UI tests. |
| I12 | Localization (Arabic/English) and RTL support with Bootstrap 5 RTL. |
| I13 | Apply the database recommendations in DATABASE-MAPPING.md §6. |
