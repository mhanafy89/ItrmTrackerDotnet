# Migration summary and feature mapping

## 1. What was done

| Area | Result |
|---|---|
| Discovery | Every source file of the Node.js app analysed (server, 9 route modules, 10 lib modules, schema + migration, seed/migrate tools, 3 front-end files, tests, deployment scripts). Findings: [ARCHITECTURE.md §A](ARCHITECTURE.md). |
| Solution | .NET 8 Clean Architecture solution: Domain, Application, Infrastructure, Web (MVC), Tests, tools. |
| Data | EF Core 8 model of all 38 tables, 2 migrations reusing the original idempotent scripts, startup migration + seed, no schema change. |
| Business logic | SLA engine, SLA service, legacy SLA, validation, import, query guard, reporting (formerly in the browser) moved to Application services. |
| UI | 30 Razor views, Bootstrap 5, accessible markup (labels, landmarks, `aria-*`, keyboard shortcuts, skip link), CSP-compliant JS. |
| Security | Identity over `dbo.users`, legacy scrypt compatibility, role policies, antiforgery, CSP/security headers, login rate limit, query-console hardening. |
| Tests | 62 xUnit tests + a differential parity harness against the original JavaScript (see §3). |

## 2. Verification performed

The build environment had the .NET 8 SDK but **NuGet package downloads were blocked by the network proxy (HTTP 403)**.
Verification was therefore done as follows:

| Check | How | Result |
|---|---|---|
| Domain + Application compile | `dotnet build` (no third-party packages needed) | ✅ 0 warnings, 0 errors |
| Whole solution incl. all Razor views | single-compilation build of every project against faithful compile-only stubs of the EF Core 8 / SqlClient / FluentValidation 11 / WindowsServices APIs used | ✅ 0 errors, 0 warnings (6 real issues were found and fixed this way) |
| Business-logic parity | `tools/SlaParityCheck`: generates randomized inputs, runs the **original JS modules** (`lib/sla-engine.js`, `lib/sla.js`, `lib/sla-service.js` + `test/memory-repository.js`, `routes/validation.js`, `routes/import.js`, `routes/query.js`, `routes/sla.js`, `lib/csv-reader.js`, `lib/auth.js`) in Node and the C# port, and deep-compares outputs | ✅ Final code: **35 050 cases, 0 mismatches** (2 seeds × 1 000 per kind), including **1 000 multi-step SLA-service scenarios** (19 287 steps: 1 523 effective stage transitions, 2 819 legacy-form syncs, 7 529 rule rejections, backfills, dashboards, mid-flight pipeline changes) compared response-by-response and on the final persisted state. ~60 000 further cases were run during development. |
| Unit tests | the xUnit suite executed offline with a minimal xUnit-compatible runner | ✅ 62 / 62 passed |
| Password compatibility | RFC 7914 scrypt vector + hashes produced by the original `lib/auth.js` | ✅ verified |
| JavaScript | `node --check wwwroot/js/site.js` | ✅ |

**Not yet executed** (requires package restore + a SQL Server): `dotnet test`, running the web application, and EF
migrations against a database. See DEPLOYMENT.md §6 for the smoke-test checklist to run once restore is possible.

## 3. Feature mapping matrix

Status legend: ✅ migrated (behaviour preserved) · ✅⁺ migrated with a documented improvement · ⚠ migrated with a flagged deviation (see ASSUMPTIONS-RISKS.md) · ➖ intentionally not migrated

### Authentication & administration

| # | Original feature | Original file/module | New controller / service / view | Status |
|---|---|---|---|---|
| 1 | Login (username/password, active users only) | routes/auth.js `login`, lib/auth.js | `AccountController.Login` · Identity `AppSignInManager`, `AppUserStore` · Views/Account/Login | ✅⁺ (rate limited) |
| 2 | Logout | routes/auth.js `logout` | `AccountController.Logout` | ✅ |
| 3 | Current user (`/api/auth/me`) | routes/auth.js `me` | `TrackerApiController.Me` + layout header | ✅ |
| 4 | Change own password (any role) | routes/auth.js `changePassword` | `AccountController.ChangePassword` · `UserAdminService.ChangeOwnPasswordAsync` · Views/Account/ChangePassword | ✅ |
| 5 | List / create / update / delete users (read_write only, incl. GET) | routes/auth.js users routes | `UsersController` · `UserAdminService` · Views/Users/* | ✅⁺ (UI added; was API-only) |
| 6 | Cannot deactivate / delete yourself | routes/auth.js | `UserAdminService` | ✅ |
| 7 | scrypt password hashing | lib/auth.js | `LegacyCompatiblePasswordHasher`, `Scrypt` | ✅⁺ (legacy verify + upgrade to PBKDF2; legacy mode available) |
| 8 | 7-day session, HttpOnly SameSite=Lax cookie, Secure optional | lib/auth.js | cookie options in Program.cs, `Security:*` settings | ✅ |
| 9 | Session invalid when user deactivated/deleted | lib/auth.js `getSessionUser` | `CookieValidation.ValidateAsync` | ✅ |
| 10 | Role gate (read_only vs read_write, exemptions) | server.js | `[Authorize(Policy="ReadWrite")]` per action; exemptions kept (follow, add comment, query, import preview, change password) | ✅ |
| 11 | Default admin seeding when no users | db/seed.js | `DatabaseInitializer.SeedAdminAsync` | ⚠ (no default password outside Development) |

### Business requests & incidents

| # | Original feature | Original file/module | New controller / service / view | Status |
|---|---|---|---|---|
| 12 | Register list with lookup names, tech owners, systems, last comment | routes/businessRequests.js / incidents.js `LIST_SELECT` | `TrackerRepository.ListAsync` · `TrackerService.ListAsync` · Views/Tracker/Index | ✅ |
| 13 | Open / Closed / All view | app.js `applyFilters` | `TrackerFilter.Apply` | ✅ |
| 14 | Search (ref, title, description, CR) | app.js | `TrackerFilter.Apply` | ✅ |
| 15 | Multi-select filters (stage, status, priority, SLA, BU, category, requester, tech owner, system, RM) with search / Only / Exclude | app.js | `_MultiSelect` partial + site.js | ✅ |
| 16 | Flag filters (Newgen, Estimated, BA, BRD, Regulatory) and Stale (14+ days) | app.js | `TrackerFilter`, Index view | ✅ |
| 17 | Saved filters | app.js (localStorage) | site.js (`itrm_saved_filters_v2`, stores the query string) | ⚠ (old saved filters not migrated) |
| 18 | Column sorting (empty last; bool/num/text) | app.js `applySorting` | `TrackerFilter.Sort` (server-side, links) | ✅ |
| 19 | Column chooser (persisted), density toggle | app.js (localStorage) | Index view + site.js (same storage keys) | ✅ |
| 20 | Create / edit form, multi tech owner (first = primary), systems | app.js form, routes `create/update` | `TrackerControllerBase.Create/Edit` · `TrackerService` · Views/Tracker/Form | ✅ |
| 21 | Field dependencies (Estimated/BA Required/Regulatory) | app.js `FIELD_DEPENDENCIES` | Form view + site.js | ✅ |
| 22 | Two-sided pipeline date validation (client + server), date range | app.js, routes/validation.js | site.js + `RequestValidation` + `TrackerFormViewModelValidator` | ✅⁺ (impossible dates such as 2024-02-30 now rejected with the normal message instead of a SQL error) |
| 23 | Unique BR No / Incident ID / CR with original messages | routes `checkUniqueness` | `TrackerService.CheckUniquenessAsync` + `DuplicateKeyException` | ✅ |
| 24 | Quick-add lookup value ("+") from the form | app.js `handleAddLookup` | `LookupsController.QuickAdd` + site.js | ✅ |
| 25 | Delete with confirmation (history snapshot first) | routes `remove` | `TrackerControllerBase.Delete` · `TrackerService.DeleteAsync` | ✅⁺ (also written to the audit log) |
| 26 | History snapshot on create/update/bulk/delete | routes `recordHistory` | `TrackerSnapshot` (same JSON shape) | ✅ |
| 27 | Details drawer (all sections, copy BR No/CR, Yes/No badges) | app.js `openViewPanel` | `TrackerControllerBase.Details` · Views/Tracker/Details | ✅ |
| 28 | Audit trail with field-level diffs | app.js `renderHistoryList` | `HistoryDiffer` · Details view | ✅ |
| 29 | Comments: list, add (any role, Ctrl+Enter, ≤2000), delete (write) | routes comments, app.js | `TrackerControllerBase.Comment/DeleteComment` · Details view | ✅ |
| 30 | Follow / unfollow (★) per user | routes follow, app.js | `TrackerControllerBase.Follow/Unfollow` | ✅ |
| 31 | Bulk lookup update of selected rows | routes `bulkUpdate`, app.js | `TrackerControllerBase.BulkEdit` · `TrackerService.BulkUpdateAsync` · bulk modal | ✅ |
| 32 | Excel export (displayed / all columns, filters applied) | app.js `handleExport`, `/api/export` | `TrackerControllerBase.Export` · `XlsxWriter` | ✅⁺ (real .xlsx instead of SpreadsheetML .xls) |
| 33 | Copy table (displayed / all columns) | app.js | `TrackerControllerBase.CopyText` + site.js | ✅ |
| 34 | Email via Outlook (.eml draft of selected rows) | app.js `buildEmlDraft` | `TrackerControllerBase.EmailDraft` · `EmlBuilder` | ✅ |
| 35 | Legacy SLA columns & details (flag, state, remaining days/effort, target, gap, breakdown) | lib/sla.js `withSla` | `LegacySlaCalculator` · Index/Details views | ✅ |
| 36 | Days since update / stale | app.js `enrichAging` | `TrackerService.EnrichAsync` | ✅ |
| 37 | Read-only JSON API (lists, record, history, comments, export, lookups) | routes GET endpoints | `Api/TrackerApiController` | ✅ |
| 38 | JSON write API for requests/incidents | routes POST/PUT/DELETE | MVC form actions | ⚠ (JSON write endpoints not exposed – see risks) |

### SLA

| # | Original feature | Original file/module | New controller / service / view | Status |
|---|---|---|---|---|
| 39 | Stage-driven SLA engine (state machine, segments, holds, health, roll-up) | lib/sla-engine.js | `Application/Sla/SlaEngine.cs` | ✅ (parity-verified) |
| 40 | Stage status changes with row locking and audit | lib/sla-service.js, lib/sla-repository.js | `SlaService`, `SlaRepository` (`UPDLOCK, HOLDLOCK`) | ✅ (parity-verified) |
| 41 | Request SLA panel + actions (Start, Hold, Resume, Complete, Cancel, Reopen, Reinstate, Estimate, edit effort) | public/stage-sla.js | Views/Tracker/_StageSla · `StageSlaController.Change` | ✅ |
| 42 | SLA history (status changes, stage moves, hold periods) | stage-sla.js, `/sla/history` | `_StageSla` partial, `SlaApiController.History` | ✅ |
| 43 | Stage SLA dashboard (tiles, by stage, worst-first requests, health filter) | stage-sla.js, `/api/sla/dashboard` | `StageSlaController.Index` · Views/StageSla/Index | ✅ |
| 44 | Documented SLA REST API (`/api/requests/:id/sla`, `/stage-status`, `/sla/history`, `/api/sla/dashboard`, aliases) | routes/requestSla.js | `Api/SlaApiController` | ✅ (POST requires antiforgery header) |
| 45 | Reconciliation after request-form saves | `syncLegacyRequest` | `SlaService.SyncLegacyRequestAsync` (`Sla:EngineSyncEnabled`) | ✅ |
| 46 | Backfill of untracked requests at startup | server.js, `backfillMissing` | `SlaBackfillHostedService` (`Sla:BackfillOnStartup`) | ✅ |
| 47 | SLA settings editor (stages, priorities, categories, statuses, weekend, engine settings) + reset | routes/sla.js, app.js | `SlaSettingsController` · `SlaConfigService` · Views/SlaSettings/Index | ✅ |
| 48 | 15-second config cache with invalidation | lib/sla.js | `SlaConfigService` (`IMemoryCache`) | ✅ |

### Reporting, follow-up, lookups, import, query

| # | Original feature | Original file/module | New controller / service / view | Status |
|---|---|---|---|---|
| 49 | Dashboard (11 KPIs, 10 panels, 6-month trend) | app.js `renderDashboard` | `HomeController.Index` · `ReportingService.GetDashboardAsync` · Views/Home/Index | ✅ |
| 50 | Follow-up (overdue / due ≤5 days, include incidents) + SLA risks | app.js `computeFollowupRows`, `computeSlaRiskRows` | `FollowUpController.Index` · `ReportingService.GetFollowUpsAsync` | ✅ |
| 51 | My Follow-ups (+ unfollow) | app.js | `FollowUpController.Mine` | ✅ |
| 52 | Manage lookups (10 types: add / rename / delete, in-use protection) | routes/lookups.js, app.js | `LookupsController` · `LookupService` · Views/Lookups/Index | ✅ |
| 53 | Import wizard: upload (.xlsx/.csv/.txt, 30 MB, 20 000 rows) | routes/import.js `parse`, lib/xlsx-reader.js, lib/csv-reader.js | `ImportController.Upload` · `XlsxReader`, `CsvParser` | ✅ |
| 54 | Auto-detect + map columns (aliases), preview first 100 rows | routes/import.js `suggestMapping` | `ImportController.Map/Preview` | ✅ |
| 55 | Compare (new/update/duplicate/invalid), differences, per-row decisions | routes/import.js `analyze` | `ImportController.Analyze/Review` · `ImportService.AnalyzeAsync` | ✅ |
| 56 | Merge with lookup auto-creation, re-validation, batch history | routes/import.js `commit` | `ImportController.Commit/Summary` · `ImportService.CommitAsync` | ✅ |
| 57 | Import history list + detail | routes/import.js `history` | `ImportController.History/HistoryDetail` | ✅ |
| 58 | Query console (SELECT-only guard, schema browser, Ctrl+Enter, export) | routes/query.js, app.js | `QueryController` · `QueryConsoleService` · `SqlQueryExecutor` | ⚠ (users/sessions tables blocked; 10 000 row cap; CSV export) |
| 59 | Health endpoint + connection-lost banner | server.js `/healthz`, app.js heartbeat | `Api/HealthController`, layout + site.js | ✅ |

### Platform & operations

| # | Original feature | Original file/module | New component | Status |
|---|---|---|---|---|
| 60 | Schema + migrations applied at startup (idempotent) | db/database.js `init` | `DatabaseInitializer` + EF migrations | ✅ |
| 61 | Optional database creation | `SQL_CREATE_DATABASE` | EF `MigrateAsync` creates a missing database (needs rights) | ✅ |
| 62 | Lookup seeding | db/seed.js | `DatabaseInitializer.SeedLookupsAsync` (empty tables only) | ⚠ (was manual & additive) |
| 63 | `migrate.js up / status / backfill / down` | db/migrate.js | `dotnet ef database update` / `migrations list` / startup backfill / `database update BaselineSchema` | ✅ |
| 64 | SQLite → SQL Server one-time migration | db/migrate-from-sqlite.js | — | ➖ (historical one-off; data already in SQL Server) |
| 65 | IIS reverse proxy + scheduled-task hosting | DEPLOY-IIS.md, web.config, deploy/install-service.ps1 | IIS ASP.NET Core Module (in-process), `deploy/install-windows-service.ps1` | ✅⁺ |
| 66 | Docker / docker-compose | Dockerfile, docker-compose.yml | Dockerfile, docker-compose.yml | ✅ |
| 67 | `.env` configuration | db/database.js | appsettings.json + environment variables + user secrets | ✅⁺ |
| 68 | Engine unit tests and service integration tests | test/*.js | Tests/*.cs + tools/SlaParityCheck | ✅⁺ |

## 4. Behavioural deviations (all intentional, all flagged)
See [ASSUMPTIONS-RISKS.md §3](ASSUMPTIONS-RISKS.md#3-flagged-deviations-and-uncertain-business-logic) for details and
the rationale of every ⚠ item above.
