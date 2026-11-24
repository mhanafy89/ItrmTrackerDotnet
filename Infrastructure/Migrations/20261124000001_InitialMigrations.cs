using ItrmTracker.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ItrmTracker.Infrastructure.Migrations;

/// <summary>
/// Baseline: every table of the original tracker (lookups, business requests, incidents, history, comments,
/// tech-owner/system junctions, SLA assumptions, users, legacy sessions, follows, import batches).
/// Source: db/schema.sql (idempotent – existing objects are left untouched).
/// </summary>
[DbContext(typeof(ItrmDbContext))]
[Migration("20261124000001_BaselineSchema")]
public partial class BaselineSchema : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        SqlScript.Run(migrationBuilder, "0001_BaselineSchema.sql");

        // The legacy app tracks its own migrations in dbo.schema_migrations; create it so both apps can coexist.
        migrationBuilder.Sql(@"
IF OBJECT_ID(N'dbo.schema_migrations', N'U') IS NULL
CREATE TABLE dbo.schema_migrations (
  name       NVARCHAR(200) NOT NULL CONSTRAINT PK_schema_migrations PRIMARY KEY,
  applied_at DATETIME2(0)  NOT NULL CONSTRAINT DF_schema_migrations_at DEFAULT SYSUTCDATETIME()
);");
    }

    /// <summary>
    /// Intentionally a no-op: dropping the baseline tables would destroy production data. Removing the schema must be
    /// a deliberate DBA action (see Docs/DEPLOYMENT.md).
    /// </summary>
    protected override void Down(MigrationBuilder migrationBuilder)
    {
    }
}

/// <summary>
/// Stage-driven, effort-driven SLA engine tables (request_stages, sla_effort_tracking, sla_pause_history,
/// request_stage_status_history, request_stage_history) and the engine settings defaults.
/// Source: db/migrations/001_sla_effort_engine.sql (idempotent). Down = the original rollback script.
/// </summary>
[DbContext(typeof(ItrmDbContext))]
[Migration("20261124000002_SlaEffortEngine")]
public partial class SlaEffortEngine : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        SqlScript.Run(migrationBuilder, "0002_SlaEffortEngine.sql");
        // Record it in the legacy migration log too, so the Node.js app (if still running) does not re-apply it.
        migrationBuilder.Sql(@"
IF NOT EXISTS (SELECT 1 FROM dbo.schema_migrations WHERE name = N'001_sla_effort_engine')
  INSERT INTO dbo.schema_migrations (name) VALUES (N'001_sla_effort_engine');");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        SqlScript.Run(migrationBuilder, "0002_SlaEffortEngine.down.sql");
        migrationBuilder.Sql("DELETE FROM dbo.schema_migrations WHERE name = N'001_sla_effort_engine';");
    }
}
