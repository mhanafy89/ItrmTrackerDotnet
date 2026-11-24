# Database mapping

The modernized application uses the **existing** SQL Server schema unchanged (`db/schema.sql` +
`db/migrations/001_sla_effort_engine.sql`). The EF Core model (`Infrastructure/Data/ItrmDbContext.cs`) maps every
table, key, index (including filtered unique indexes), check constraint and cascade rule. Column names are mapped by a
snake_case convention (`BrNo` → `br_no`); the three exceptions are configured explicitly.

* Schema: `dbo` · Dates: `DATE` ↔ `DateOnly`, `DATETIME2(0)` (UTC) ↔ `DateTime`, `BIT` ↔ `bool`, `DECIMAL(p,s)` ↔ `decimal` (`HasPrecision`)
* All `*_at` defaults `SYSUTCDATETIME()` are declared with `HasDefaultValueSql`; the application also sets them explicitly (UTC, whole seconds).
* `ON DELETE CASCADE` is declared exactly where the schema has it; every lookup FK is `NO ACTION` (deleting a used lookup value fails with “still used by existing records”, as before).

## 1. Table → entity mapping

| Table | Entity (Domain/Entities) | Key | Notes |
|---|---|---|---|
| business_units | `BusinessUnit` | `id` identity | `UQ_business_units_name` |
| business_categories | `BusinessCategory` | `id` | `UQ_business_categories_name` |
| priorities | `Priority` | `id` | + `sort_order` (default 0), `UQ_priorities_name` |
| stages | `Stage` | `id` | + `sort_order`, `UQ_stages_name` |
| statuses | `Status` | `id` | + `sort_order`, `UQ_statuses_name` |
| tech_owners | `TechOwner` | `id` | unique name |
| regulators | `Regulator` | `id` | unique name |
| requesters | `Requester` | `id` | unique name |
| rms | `Rm` | `id` | unique name |
| systems | `SystemLookup` | `id` | renamed in C# to avoid the `System` namespace |
| business_requests | `BusinessRequest` | `id` | see §2 |
| business_request_history | `BusinessRequestHistory` | `id` | FK cascade; `IX_brh_request (business_request_id, changed_at DESC)` |
| request_comments | `RequestComment` | `id` | FK cascade; `IX_comments_request` |
| business_request_tech_owners | `BusinessRequestTechOwner` | (`business_request_id`,`tech_owner_id`) | junction; cascade from request |
| business_request_systems | `BusinessRequestSystem` | (`business_request_id`,`system_id`) | junction; cascade from request |
| business_request_follows | `BusinessRequestFollow` | (`user_id`,`business_request_id`) | cascade from user and request |
| incidents | `Incident` | `id` | see §2 |
| incident_history | `IncidentHistory` | `id` | FK cascade |
| incident_comments | `IncidentComment` | `id` | FK cascade |
| incident_tech_owners | `IncidentTechOwner` | composite | junction |
| incident_systems | `IncidentSystem` | composite | junction |
| incident_follows | `IncidentFollow` | composite | cascade from user and incident |
| sla_stages | `SlaStageConfig` | `name` | `br_days`, `incident_days` INT; `effort_days` DECIMAL(6,2) |
| sla_priorities | `SlaPriorityConfig` | `name` | `factor` DECIMAL(5,2), `response_hours` DECIMAL(6,1), `update_days` INT |
| sla_categories | `SlaCategoryConfig` | `name` | `factor` DECIMAL(5,2) |
| sla_statuses | `SlaStatusConfig` | `name` | `CK_sla_statuses_state` (Active/Paused/Done) |
| sla_settings | `SlaSetting` | `setting_key` | key/value: `weekend_days`, `at_risk_threshold_pct`, `breach_threshold_pct`, `priority_factor_mode`, `enforce_stage_order`, `auto_start_next_stage` |
| request_stages | `RequestStage` | `id` | `UQ_rs_request_stage (business_request_id, stage_name)`, `CK_rs_status`, `CK_rs_effort`; decimals (7,2) |
| sla_effort_tracking | `SlaEffortTracking` | `id` | filtered unique `UX_set_open (request_stage_id) WHERE ended_at IS NULL` |
| sla_pause_history | `SlaPauseHistory` | `id` | filtered unique `UX_sph_open WHERE resumed_at IS NULL` |
| request_stage_status_history | `RequestStageStatusHistory` | `id` | append-only audit |
| request_stage_history | `RequestStageHistory` | `id` | stage movements |
| users | `AppUser` | `id` | `UQ_users_username`, `CK_users_role` (read_only/read_write); used by ASP.NET Core Identity |
| sessions | `UserSession` | `token` | legacy Node sessions – **not used** by the new app, kept for coexistence |
| import_batches | `ImportBatch` | `id` | `IX_import_batches_created (created_at DESC, id DESC)` |
| import_batch_rows | `ImportBatchRow` | `id` | FK cascade to batch |
| schema_migrations | `LegacySchemaMigration` | `name` | legacy migration log (kept in sync by the EF migrations) |
| __EFMigrationsHistory | (EF Core) | `MigrationId` | **new** – created by EF Core |

## 2. Main tables in detail

### business_requests → `BusinessRequest`

| Column | Type | C# property | Rule / note |
|---|---|---|---|
| id | INT IDENTITY | Id | |
| br_no | NVARCHAR(255) NULL | BrNo | filtered unique `UX_business_requests_br_no` (NULLs allowed many times) |
| cr | NVARCHAR(255) NULL | Cr | filtered unique `UX_business_requests_cr` |
| submission_date | DATE | SubmissionDate | 2020-01-01…2035-12-31 |
| business_unit_id, business_category_id, requester_id, priority_id, stage_id, status_id, tech_owner_id, regulator_id, rm_id | INT NULL FK | *Id | each indexed (`IX_br_*`) |
| requester | NVARCHAR(255) | **RequesterText** | legacy free text – shown when `requester_id` is NULL |
| title | NVARCHAR(1000) NOT NULL | Title | required |
| description | NVARCHAR(MAX) | Description | |
| newgen_related, estimated, ba_required, brd_assigned, regulatory | BIT NOT NULL default 0 | bool | |
| regulatory_deadline, analysis_planned_start/end_date, dev_planned_start/end_date, qa_planned_start/end_date, uat_planned_start_date, uat_planned_date, cab_date, deployment_planned_end_date, closed_date | DATE | DateOnly? | pipeline order rule (two-sided) |
| comments | NVARCHAR(MAX) | Comments | legacy, superseded by request_comments |
| created_at, updated_at | DATETIME2(0) UTC | CreatedAt, UpdatedAt | `IX_br_updated_at DESC` |
| created_by, updated_by | NVARCHAR(255) | | display name of the user |

`tech_owner_id` holds the *primary* (first selected) owner; the full set is in `business_request_tech_owners`.

### incidents → `Incident`
Same pattern; `incident_id` is the **business reference** and is mapped to `IncidentRef` (the identity key is `id`).
Filtered unique indexes `UX_incidents_incident_id`, `UX_incidents_cr`.

## 3. Relationships

```
business_units/…/rms 1─* business_requests *─* tech_owners (business_request_tech_owners)
                                         *─* systems     (business_request_systems)
business_requests 1─* business_request_history | request_comments | request_stages | request_stage_history | business_request_follows
request_stages 1─* sla_effort_tracking | sla_pause_history | request_stage_status_history
users 1─* business_request_follows | incident_follows | sessions
import_batches 1─* import_batch_rows
(incidents mirror business_requests without SLA-engine tables)
sla_* tables are keyed by lookup NAME (not FK) so assumptions survive lookup renames.
```

## 4. JSON columns (format preserved)
`*_history.snapshot_json` stores the request row with **snake_case keys**, dates as `YYYY-MM-DD`, timestamps as
`YYYY-MM-DD HH:MM:SS` (UTC) and booleans as 0/1 – exactly the shape written by the Node.js app, so audit trails written
by either application diff correctly (`TrackerSnapshot`, `HistoryDiffer`). `import_batch_rows.diff_json` / `raw_json` and
`import_batches.summary_json` keep their original shapes.

## 5. Migrations

| EF migration | Source script | Up | Down |
|---|---|---|---|
| `20261124000001_BaselineSchema` | db/schema.sql (+ `schema_migrations`) | idempotent create-if-missing | no-op (dropping production data must be a DBA decision) |
| `20261124000002_SlaEffortEngine` | db/migrations/001_sla_effort_engine.sql | idempotent; also records `001_sla_effort_engine` in `schema_migrations` | original rollback script |

Because the scripts are idempotent, the migrations can be applied to a brand-new database **and** to the existing
production database (where they only add the `__EFMigrationsHistory` rows). See DEPLOYMENT.md §3.

> **Model snapshot:** the SDK/NuGet feed was not reachable while this solution was generated, so the EF Core model
> snapshot (`ItrmDbContextModelSnapshot.cs`) could not be produced by `dotnet ef`. Before adding the *next* migration run
> `dotnet ef migrations add SnapshotBaseline -p Infrastructure -s Web`, delete the generated `Up`/`Down` bodies (the
> schema already exists) and keep the generated snapshot. Applying the two shipped migrations does not need it.

## 6. Recommended database improvements (not applied – functionality unchanged)

| # | Recommendation | Benefit |
|---|---|---|
| 1 | Add `ON DELETE` history retention: write deletion audits to a table without FK cascade (today the "delete" history row is removed together with the record). | durable delete audit |
| 2 | Replace `business_requests.tech_owner_id` / `comments` / `requester` legacy columns after confirming no external report uses them. | remove redundancy |
| 3 | Key `sla_*` tables and `request_stages.stage_name` by lookup id (+ keep name for display) or add a rename cascade. | referential integrity |
| 4 | `rowversion` column on `business_requests` / `incidents` for optimistic concurrency (last write currently wins). | lost-update protection |
| 5 | Covering index for the register query: `(updated_at DESC, id DESC) INCLUDE (…)` and on `request_comments (business_request_id, created_at DESC, id DESC) INCLUDE (body)`. | faster list/last-comment lookup |
| 6 | Drop the unused `dbo.sessions` table once the Node.js app is retired. | hygiene |
| 7 | Partition / archive `*_history` and `import_batch_rows` by year. | storage growth |
| 8 | `CHECK` constraints for the 2020-01-01…2035-12-31 date window and pipeline order (currently application-only). | defence in depth |
| 9 | Make `created_at`/`updated_at` `DATETIMEOFFSET` or document UTC explicitly in column descriptions. | clarity |
