using System.Text;
using ItrmTracker.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ItrmTracker.Infrastructure.Data;

/// <summary>
/// EF Core model of the existing ITRM tracker SQL Server database. Every table, column, key, index (including the
/// filtered unique indexes), check constraint and cascade rule mirrors db/schema.sql and
/// db/migrations/001_sla_effort_engine.sql, so the modernized app runs against the existing database unchanged.
/// </summary>
public class ItrmDbContext : DbContext
{
    public ItrmDbContext(DbContextOptions<ItrmDbContext> options) : base(options) { }

    public DbSet<BusinessUnit> BusinessUnits => Set<BusinessUnit>();
    public DbSet<BusinessCategory> BusinessCategories => Set<BusinessCategory>();
    public DbSet<Priority> Priorities => Set<Priority>();
    public DbSet<Stage> Stages => Set<Stage>();
    public DbSet<Status> Statuses => Set<Status>();
    public DbSet<TechOwner> TechOwners => Set<TechOwner>();
    public DbSet<Regulator> Regulators => Set<Regulator>();
    public DbSet<Requester> Requesters => Set<Requester>();
    public DbSet<Rm> Rms => Set<Rm>();
    public DbSet<SystemLookup> Systems => Set<SystemLookup>();

    public DbSet<BusinessRequest> BusinessRequests => Set<BusinessRequest>();
    public DbSet<BusinessRequestHistory> BusinessRequestHistory => Set<BusinessRequestHistory>();
    public DbSet<RequestComment> RequestComments => Set<RequestComment>();
    public DbSet<BusinessRequestTechOwner> BusinessRequestTechOwners => Set<BusinessRequestTechOwner>();
    public DbSet<BusinessRequestSystem> BusinessRequestSystems => Set<BusinessRequestSystem>();
    public DbSet<BusinessRequestFollow> BusinessRequestFollows => Set<BusinessRequestFollow>();

    public DbSet<Incident> Incidents => Set<Incident>();
    public DbSet<IncidentHistory> IncidentHistory => Set<IncidentHistory>();
    public DbSet<IncidentComment> IncidentComments => Set<IncidentComment>();
    public DbSet<IncidentTechOwner> IncidentTechOwners => Set<IncidentTechOwner>();
    public DbSet<IncidentSystem> IncidentSystems => Set<IncidentSystem>();
    public DbSet<IncidentFollow> IncidentFollows => Set<IncidentFollow>();

    public DbSet<SlaStageConfig> SlaStages => Set<SlaStageConfig>();
    public DbSet<SlaPriorityConfig> SlaPriorities => Set<SlaPriorityConfig>();
    public DbSet<SlaCategoryConfig> SlaCategories => Set<SlaCategoryConfig>();
    public DbSet<SlaStatusConfig> SlaStatuses => Set<SlaStatusConfig>();
    public DbSet<SlaSetting> SlaSettings => Set<SlaSetting>();

    public DbSet<RequestStage> RequestStages => Set<RequestStage>();
    public DbSet<SlaEffortTracking> SlaEffortTracking => Set<SlaEffortTracking>();
    public DbSet<SlaPauseHistory> SlaPauseHistory => Set<SlaPauseHistory>();
    public DbSet<RequestStageStatusHistory> RequestStageStatusHistory => Set<RequestStageStatusHistory>();
    public DbSet<RequestStageHistory> RequestStageHistory => Set<RequestStageHistory>();

    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<UserSession> Sessions => Set<UserSession>();
    public DbSet<ImportBatch> ImportBatches => Set<ImportBatch>();
    public DbSet<ImportBatchRow> ImportBatchRows => Set<ImportBatchRow>();
    public DbSet<LegacySchemaMigration> LegacySchemaMigrations => Set<LegacySchemaMigration>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema("dbo");

        Lookup<BusinessUnit>(b, "business_units");
        Lookup<BusinessCategory>(b, "business_categories");
        SortedLookup<Priority>(b, "priorities");
        SortedLookup<Stage>(b, "stages");
        SortedLookup<Status>(b, "statuses");
        Lookup<TechOwner>(b, "tech_owners");
        Lookup<Regulator>(b, "regulators");
        Lookup<Requester>(b, "requesters");
        Lookup<Rm>(b, "rms");
        Lookup<SystemLookup>(b, "systems");

        ConfigureBusinessRequests(b);
        ConfigureIncidents(b);
        ConfigureSla(b);
        ConfigureAccounts(b);

        // Columns follow the snake_case naming of the existing database (explicit names above win).
        foreach (var entity in b.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                if (property.GetColumnName() == property.Name) property.SetColumnName(ToSnakeCase(property.Name));
            }
        }
    }

    internal static string ToSnakeCase(string name)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c) && i > 0) sb.Append('_');
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    private static void Lookup<T>(ModelBuilder b, string table) where T : LookupEntity
    {
        b.Entity<T>(e =>
        {
            e.ToTable(table);
            e.HasKey(x => x.Id).HasName($"PK_{table}");
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.HasIndex(x => x.Name).IsUnique().HasDatabaseName($"UQ_{table}_name");
        });
    }

    private static void SortedLookup<T>(ModelBuilder b, string table) where T : SortedLookupEntity
    {
        Lookup<T>(b, table);
        b.Entity<T>().Property(x => x.SortOrder).HasDefaultValue(0);
    }

    private static void Audit<T>(EntityTypeBuilder<T> e) where T : class
    {
        e.Property<DateTime>("CreatedAt").HasColumnType("datetime2(0)").HasDefaultValueSql("SYSUTCDATETIME()");
        e.Property<DateTime>("UpdatedAt").HasColumnType("datetime2(0)").HasDefaultValueSql("SYSUTCDATETIME()");
        e.Property<string?>("CreatedBy").HasMaxLength(255);
        e.Property<string?>("UpdatedBy").HasMaxLength(255);
    }

    private static void ConfigureBusinessRequests(ModelBuilder b)
    {
        b.Entity<BusinessRequest>(e =>
        {
            e.ToTable("business_requests");
            e.HasKey(x => x.Id).HasName("PK_business_requests");
            e.Property(x => x.BrNo).HasMaxLength(255);
            e.Property(x => x.Cr).HasMaxLength(255);
            e.Property(x => x.RequesterText).HasColumnName("requester").HasMaxLength(255);
            e.Property(x => x.Title).HasMaxLength(1000).IsRequired();
            e.Property(x => x.Description).HasColumnType("nvarchar(max)");
            e.Property(x => x.Comments).HasColumnType("nvarchar(max)");
            Audit(e);

            // Filtered unique indexes: SQL Server would otherwise allow only one NULL.
            e.HasIndex(x => x.BrNo).IsUnique().HasFilter("[br_no] IS NOT NULL").HasDatabaseName("UX_business_requests_br_no");
            e.HasIndex(x => x.Cr).IsUnique().HasFilter("[cr] IS NOT NULL").HasDatabaseName("UX_business_requests_cr");
            e.HasIndex(x => x.BusinessUnitId).HasDatabaseName("IX_br_business_unit");
            e.HasIndex(x => x.BusinessCategoryId).HasDatabaseName("IX_br_business_category");
            e.HasIndex(x => x.RequesterId).HasDatabaseName("IX_br_requester");
            e.HasIndex(x => x.PriorityId).HasDatabaseName("IX_br_priority");
            e.HasIndex(x => x.StageId).HasDatabaseName("IX_br_stage");
            e.HasIndex(x => x.StatusId).HasDatabaseName("IX_br_status");
            e.HasIndex(x => x.TechOwnerId).HasDatabaseName("IX_br_tech_owner");
            e.HasIndex(x => x.RegulatorId).HasDatabaseName("IX_br_regulator");
            e.HasIndex(x => x.RmId).HasDatabaseName("IX_br_rm");
            e.HasIndex(x => x.UpdatedAt).IsDescending().HasDatabaseName("IX_br_updated_at");

            e.HasOne(x => x.BusinessUnit).WithMany().HasForeignKey(x => x.BusinessUnitId).HasConstraintName("FK_br_business_unit").OnDelete(DeleteBehavior.NoAction);
            e.HasOne(x => x.BusinessCategory).WithMany().HasForeignKey(x => x.BusinessCategoryId).HasConstraintName("FK_br_business_category").OnDelete(DeleteBehavior.NoAction);
            e.HasOne(x => x.Requester).WithMany().HasForeignKey(x => x.RequesterId).HasConstraintName("FK_br_requester").OnDelete(DeleteBehavior.NoAction);
            e.HasOne(x => x.Priority).WithMany().HasForeignKey(x => x.PriorityId).HasConstraintName("FK_br_priority").OnDelete(DeleteBehavior.NoAction);
            e.HasOne(x => x.Stage).WithMany().HasForeignKey(x => x.StageId).HasConstraintName("FK_br_stage").OnDelete(DeleteBehavior.NoAction);
            e.HasOne(x => x.Status).WithMany().HasForeignKey(x => x.StatusId).HasConstraintName("FK_br_status").OnDelete(DeleteBehavior.NoAction);
            e.HasOne(x => x.TechOwner).WithMany().HasForeignKey(x => x.TechOwnerId).HasConstraintName("FK_br_tech_owner").OnDelete(DeleteBehavior.NoAction);
            e.HasOne(x => x.Regulator).WithMany().HasForeignKey(x => x.RegulatorId).HasConstraintName("FK_br_regulator").OnDelete(DeleteBehavior.NoAction);
            e.HasOne(x => x.Rm).WithMany().HasForeignKey(x => x.RmId).HasConstraintName("FK_br_rm").OnDelete(DeleteBehavior.NoAction);
        });

        b.Entity<BusinessRequestHistory>(e =>
        {
            e.ToTable("business_request_history");
            e.HasKey(x => x.Id).HasName("PK_business_request_history");
            e.Property(x => x.ChangedAt).HasColumnType("datetime2(0)").HasDefaultValueSql("SYSUTCDATETIME()");
            e.Property(x => x.ChangedBy).HasMaxLength(255);
            e.Property(x => x.ChangeType).HasMaxLength(50).IsRequired();
            e.Property(x => x.SnapshotJson).HasColumnType("nvarchar(max)").IsRequired();
            e.HasIndex(x => new { x.BusinessRequestId, x.ChangedAt }).IsDescending(false, true).HasDatabaseName("IX_brh_request");
            e.HasOne<BusinessRequest>().WithMany(x => x.History).HasForeignKey(x => x.BusinessRequestId).HasConstraintName("FK_brh_request").OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<RequestComment>(e =>
        {
            e.ToTable("request_comments");
            e.HasKey(x => x.Id).HasName("PK_request_comments");
            e.Property(x => x.Body).HasColumnType("nvarchar(max)").IsRequired();
            e.Property(x => x.CreatedAt).HasColumnType("datetime2(0)").HasDefaultValueSql("SYSUTCDATETIME()");
            e.Property(x => x.CreatedBy).HasMaxLength(255);
            e.HasIndex(x => new { x.BusinessRequestId, x.CreatedAt, x.Id }).HasDatabaseName("IX_comments_request");
            e.HasOne<BusinessRequest>().WithMany(x => x.CommentEntries).HasForeignKey(x => x.BusinessRequestId).HasConstraintName("FK_rc_request").OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<BusinessRequestTechOwner>(e =>
        {
            e.ToTable("business_request_tech_owners");
            e.HasKey(x => new { x.BusinessRequestId, x.TechOwnerId }).HasName("PK_business_request_tech_owners");
            e.HasOne(x => x.BusinessRequest).WithMany(x => x.TechOwners).HasForeignKey(x => x.BusinessRequestId).HasConstraintName("FK_brto_request").OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.TechOwner).WithMany().HasForeignKey(x => x.TechOwnerId).HasConstraintName("FK_brto_tech_owner").OnDelete(DeleteBehavior.NoAction);
        });

        b.Entity<BusinessRequestSystem>(e =>
        {
            e.ToTable("business_request_systems");
            e.HasKey(x => new { x.BusinessRequestId, x.SystemId }).HasName("PK_business_request_systems");
            e.HasOne(x => x.BusinessRequest).WithMany(x => x.Systems).HasForeignKey(x => x.BusinessRequestId).HasConstraintName("FK_brs_request").OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.System).WithMany().HasForeignKey(x => x.SystemId).HasConstraintName("FK_brs_system").OnDelete(DeleteBehavior.NoAction);
        });

        b.Entity<BusinessRequestFollow>(e =>
        {
            e.ToTable("business_request_follows");
            e.HasKey(x => new { x.UserId, x.BusinessRequestId }).HasName("PK_business_request_follows");
            e.Property(x => x.CreatedAt).HasColumnType("datetime2(0)").HasDefaultValueSql("SYSUTCDATETIME()");
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).HasConstraintName("FK_brf_user").OnDelete(DeleteBehavior.Cascade);
            e.HasOne<BusinessRequest>().WithMany(x => x.Follows).HasForeignKey(x => x.BusinessRequestId).HasConstraintName("FK_brf_request").OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureIncidents(ModelBuilder b)
    {
        b.Entity<Incident>(e =>
        {
            e.ToTable("incidents");
            e.HasKey(x => x.Id).HasName("PK_incidents");
            e.Property(x => x.IncidentRef).HasColumnName("incident_id").HasMaxLength(255);
            e.Property(x => x.Cr).HasMaxLength(255);
            e.Property(x => x.Title).HasMaxLength(1000).IsRequired();
            e.Property(x => x.Description).HasColumnType("nvarchar(max)");
            Audit(e);

            e.HasIndex(x => x.IncidentRef).IsUnique().HasFilter("[incident_id] IS NOT NULL").HasDatabaseName("UX_incidents_incident_id");
            e.HasIndex(x => x.Cr).IsUnique().HasFilter("[cr] IS NOT NULL").HasDatabaseName("UX_incidents_cr");
            e.HasIndex(x => x.BusinessUnitId).HasDatabaseName("IX_inc_business_unit");
            e.HasIndex(x => x.RequesterId).HasDatabaseName("IX_inc_requester");
            e.HasIndex(x => x.PriorityId).HasDatabaseName("IX_inc_priority");
            e.HasIndex(x => x.StageId).HasDatabaseName("IX_inc_stage");
            e.HasIndex(x => x.StatusId).HasDatabaseName("IX_inc_status");
            e.HasIndex(x => x.TechOwnerId).HasDatabaseName("IX_inc_tech_owner");
            e.HasIndex(x => x.RmId).HasDatabaseName("IX_inc_rm");
            e.HasIndex(x => x.UpdatedAt).IsDescending().HasDatabaseName("IX_inc_updated_at");

            e.HasOne(x => x.BusinessUnit).WithMany().HasForeignKey(x => x.BusinessUnitId).HasConstraintName("FK_inc_business_unit").OnDelete(DeleteBehavior.NoAction);
            e.HasOne(x => x.Requester).WithMany().HasForeignKey(x => x.RequesterId).HasConstraintName("FK_inc_requester").OnDelete(DeleteBehavior.NoAction);
            e.HasOne(x => x.Priority).WithMany().HasForeignKey(x => x.PriorityId).HasConstraintName("FK_inc_priority").OnDelete(DeleteBehavior.NoAction);
            e.HasOne(x => x.Stage).WithMany().HasForeignKey(x => x.StageId).HasConstraintName("FK_inc_stage").OnDelete(DeleteBehavior.NoAction);
            e.HasOne(x => x.Status).WithMany().HasForeignKey(x => x.StatusId).HasConstraintName("FK_inc_status").OnDelete(DeleteBehavior.NoAction);
            e.HasOne(x => x.TechOwner).WithMany().HasForeignKey(x => x.TechOwnerId).HasConstraintName("FK_inc_tech_owner").OnDelete(DeleteBehavior.NoAction);
            e.HasOne(x => x.Rm).WithMany().HasForeignKey(x => x.RmId).HasConstraintName("FK_inc_rm").OnDelete(DeleteBehavior.NoAction);
        });

        b.Entity<IncidentHistory>(e =>
        {
            e.ToTable("incident_history");
            e.HasKey(x => x.Id).HasName("PK_incident_history");
            e.Property(x => x.ChangedAt).HasColumnType("datetime2(0)").HasDefaultValueSql("SYSUTCDATETIME()");
            e.Property(x => x.ChangedBy).HasMaxLength(255);
            e.Property(x => x.ChangeType).HasMaxLength(50).IsRequired();
            e.Property(x => x.SnapshotJson).HasColumnType("nvarchar(max)").IsRequired();
            e.HasIndex(x => new { x.IncidentId, x.ChangedAt }).IsDescending(false, true).HasDatabaseName("IX_inch_incident");
            e.HasOne<Incident>().WithMany(x => x.History).HasForeignKey(x => x.IncidentId).HasConstraintName("FK_inch_incident").OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<IncidentComment>(e =>
        {
            e.ToTable("incident_comments");
            e.HasKey(x => x.Id).HasName("PK_incident_comments");
            e.Property(x => x.Body).HasColumnType("nvarchar(max)").IsRequired();
            e.Property(x => x.CreatedAt).HasColumnType("datetime2(0)").HasDefaultValueSql("SYSUTCDATETIME()");
            e.Property(x => x.CreatedBy).HasMaxLength(255);
            e.HasIndex(x => new { x.IncidentId, x.CreatedAt, x.Id }).HasDatabaseName("IX_inc_comments");
            e.HasOne<Incident>().WithMany(x => x.CommentEntries).HasForeignKey(x => x.IncidentId).HasConstraintName("FK_ic_incident").OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<IncidentTechOwner>(e =>
        {
            e.ToTable("incident_tech_owners");
            e.HasKey(x => new { x.IncidentId, x.TechOwnerId }).HasName("PK_incident_tech_owners");
            e.HasOne(x => x.Incident).WithMany(x => x.TechOwners).HasForeignKey(x => x.IncidentId).HasConstraintName("FK_incto_incident").OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.TechOwner).WithMany().HasForeignKey(x => x.TechOwnerId).HasConstraintName("FK_incto_tech_owner").OnDelete(DeleteBehavior.NoAction);
        });

        b.Entity<IncidentSystem>(e =>
        {
            e.ToTable("incident_systems");
            e.HasKey(x => new { x.IncidentId, x.SystemId }).HasName("PK_incident_systems");
            e.HasOne(x => x.Incident).WithMany(x => x.Systems).HasForeignKey(x => x.IncidentId).HasConstraintName("FK_incs_incident").OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.System).WithMany().HasForeignKey(x => x.SystemId).HasConstraintName("FK_incs_system").OnDelete(DeleteBehavior.NoAction);
        });

        b.Entity<IncidentFollow>(e =>
        {
            e.ToTable("incident_follows");
            e.HasKey(x => new { x.UserId, x.IncidentId }).HasName("PK_incident_follows");
            e.Property(x => x.CreatedAt).HasColumnType("datetime2(0)").HasDefaultValueSql("SYSUTCDATETIME()");
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).HasConstraintName("FK_incf_user").OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Incident>().WithMany(x => x.Follows).HasForeignKey(x => x.IncidentId).HasConstraintName("FK_incf_incident").OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureSla(ModelBuilder b)
    {
        b.Entity<SlaStageConfig>(e =>
        {
            e.ToTable("sla_stages");
            e.HasKey(x => x.Name).HasName("PK_sla_stages");
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.EffortDays).HasPrecision(6, 2);
        });
        b.Entity<SlaPriorityConfig>(e =>
        {
            e.ToTable("sla_priorities");
            e.HasKey(x => x.Name).HasName("PK_sla_priorities");
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Factor).HasPrecision(5, 2);
            e.Property(x => x.ResponseHours).HasPrecision(6, 1);
        });
        b.Entity<SlaCategoryConfig>(e =>
        {
            e.ToTable("sla_categories");
            e.HasKey(x => x.Name).HasName("PK_sla_categories");
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Factor).HasPrecision(5, 2);
        });
        b.Entity<SlaStatusConfig>(e =>
        {
            e.ToTable("sla_statuses", t => t.HasCheckConstraint("CK_sla_statuses_state", "state IN (N'Active', N'Paused', N'Done')"));
            e.HasKey(x => x.Name).HasName("PK_sla_statuses");
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.State).HasMaxLength(10).IsRequired();
        });
        b.Entity<SlaSetting>(e =>
        {
            e.ToTable("sla_settings");
            e.HasKey(x => x.SettingKey).HasName("PK_sla_settings");
            e.Property(x => x.SettingKey).HasMaxLength(50);
            e.Property(x => x.SettingValue).HasMaxLength(200).IsRequired();
        });

        b.Entity<RequestStage>(e =>
        {
            e.ToTable("request_stages", t =>
            {
                t.HasCheckConstraint("CK_rs_status", "status IN (N'Not Yet Estimated', N'Not Started', N'In Progress', N'On Hold', N'Completed', N'Cancelled')");
                t.HasCheckConstraint("CK_rs_effort", "stage_effort_days IS NULL OR stage_effort_days >= 0");
            });
            e.HasKey(x => x.Id).HasName("PK_request_stages");
            e.Property(x => x.StageName).HasMaxLength(200).IsRequired();
            e.Property(x => x.Status).HasMaxLength(20).IsRequired().HasDefaultValue("Not Started");
            e.Property(x => x.StageEffortDays).HasPrecision(7, 2);
            e.Property(x => x.ActualEffortDays).HasPrecision(7, 2);
            e.Property(x => x.RemainingEffortDays).HasPrecision(7, 2);
            e.Property(x => x.SlaConsumedDays).HasPrecision(7, 2);
            e.Property(x => x.TotalHoldTime).HasPrecision(7, 2);
            foreach (var p in new[] { nameof(RequestStage.StartedAt), nameof(RequestStage.CompletedAt), nameof(RequestStage.PausedAt), nameof(RequestStage.ResumeAt) })
            {
                e.Property(p).HasColumnType("datetime2(0)");
            }
            e.Property(x => x.CreatedAt).HasColumnType("datetime2(0)").HasDefaultValueSql("SYSUTCDATETIME()");
            e.Property(x => x.UpdatedAt).HasColumnType("datetime2(0)").HasDefaultValueSql("SYSUTCDATETIME()");
            e.Property(x => x.UpdatedBy).HasMaxLength(255);
            e.HasIndex(x => new { x.BusinessRequestId, x.StageName }).IsUnique().HasDatabaseName("UQ_rs_request_stage");
            e.HasIndex(x => new { x.BusinessRequestId, x.StageOrder }).HasDatabaseName("IX_rs_request");
            e.HasIndex(x => x.Status).HasDatabaseName("IX_rs_status");
            e.HasOne<BusinessRequest>().WithMany().HasForeignKey(x => x.BusinessRequestId).HasConstraintName("FK_rs_request").OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<SlaEffortTracking>(e =>
        {
            e.ToTable("sla_effort_tracking");
            e.HasKey(x => x.Id).HasName("PK_sla_effort_tracking");
            e.Property(x => x.StartedAt).HasColumnType("datetime2(0)");
            e.Property(x => x.EndedAt).HasColumnType("datetime2(0)");
            e.Property(x => x.EndReason).HasMaxLength(20);
            e.Property(x => x.WorkingDays).HasPrecision(7, 2);
            e.Property(x => x.CreatedBy).HasMaxLength(255);
            // At most one open segment per stage.
            e.HasIndex(x => x.RequestStageId).IsUnique().HasFilter("[ended_at] IS NULL").HasDatabaseName("UX_set_open");
            e.HasIndex(x => x.BusinessRequestId).HasDatabaseName("IX_set_request");
            e.HasOne<RequestStage>().WithMany().HasForeignKey(x => x.RequestStageId).HasConstraintName("FK_set_stage").OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<SlaPauseHistory>(e =>
        {
            e.ToTable("sla_pause_history");
            e.HasKey(x => x.Id).HasName("PK_sla_pause_history");
            e.Property(x => x.PausedAt).HasColumnType("datetime2(0)");
            e.Property(x => x.ResumedAt).HasColumnType("datetime2(0)");
            e.Property(x => x.HoldDays).HasPrecision(7, 2);
            e.Property(x => x.Reason).HasMaxLength(500);
            e.Property(x => x.PausedBy).HasMaxLength(255);
            e.Property(x => x.ResumedBy).HasMaxLength(255);
            e.HasIndex(x => x.RequestStageId).IsUnique().HasFilter("[resumed_at] IS NULL").HasDatabaseName("UX_sph_open");
            e.HasIndex(x => x.BusinessRequestId).HasDatabaseName("IX_sph_request");
            e.HasOne<RequestStage>().WithMany().HasForeignKey(x => x.RequestStageId).HasConstraintName("FK_sph_stage").OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<RequestStageStatusHistory>(e =>
        {
            e.ToTable("request_stage_status_history");
            e.HasKey(x => x.Id).HasName("PK_rssh");
            e.Property(x => x.StageName).HasMaxLength(200).IsRequired();
            e.Property(x => x.FromStatus).HasMaxLength(20);
            e.Property(x => x.ToStatus).HasMaxLength(20).IsRequired();
            e.Property(x => x.Action).HasMaxLength(20).IsRequired();
            e.Property(x => x.Source).HasMaxLength(20).IsRequired().HasDefaultValue("api");
            e.Property(x => x.PlannedEffortDays).HasPrecision(7, 2);
            e.Property(x => x.ConsumedDays).HasPrecision(7, 2);
            e.Property(x => x.RemainingDays).HasPrecision(7, 2);
            e.Property(x => x.Note).HasMaxLength(500);
            e.Property(x => x.ChangedBy).HasMaxLength(255);
            e.Property(x => x.ChangedAt).HasColumnType("datetime2(0)").HasDefaultValueSql("SYSUTCDATETIME()");
            e.HasIndex(x => new { x.BusinessRequestId, x.ChangedAt, x.Id }).IsDescending(false, true, true).HasDatabaseName("IX_rssh_request");
            e.HasOne<RequestStage>().WithMany().HasForeignKey(x => x.RequestStageId).HasConstraintName("FK_rssh_stage").OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<RequestStageHistory>(e =>
        {
            e.ToTable("request_stage_history");
            e.HasKey(x => x.Id).HasName("PK_rsh");
            e.Property(x => x.FromStage).HasMaxLength(200);
            e.Property(x => x.ToStage).HasMaxLength(200).IsRequired();
            e.Property(x => x.Reason).HasMaxLength(30).IsRequired();
            e.Property(x => x.Source).HasMaxLength(20).IsRequired().HasDefaultValue("api");
            e.Property(x => x.ChangedBy).HasMaxLength(255);
            e.Property(x => x.ChangedAt).HasColumnType("datetime2(0)").HasDefaultValueSql("SYSUTCDATETIME()");
            e.HasIndex(x => new { x.BusinessRequestId, x.ChangedAt, x.Id }).IsDescending(false, true, true).HasDatabaseName("IX_rsh_request");
            e.HasOne<BusinessRequest>().WithMany().HasForeignKey(x => x.BusinessRequestId).HasConstraintName("FK_rsh_request").OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureAccounts(ModelBuilder b)
    {
        b.Entity<AppUser>(e =>
        {
            e.ToTable("users", t => t.HasCheckConstraint("CK_users_role", "role IN (N'read_only', N'read_write')"));
            e.HasKey(x => x.Id).HasName("PK_users");
            e.Property(x => x.Username).HasMaxLength(100).IsRequired();
            e.HasIndex(x => x.Username).IsUnique().HasDatabaseName("UQ_users_username");
            e.Property(x => x.PasswordHash).HasMaxLength(255).IsRequired();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Role).HasMaxLength(20).IsRequired();
            e.Property(x => x.CreatedAt).HasColumnType("datetime2(0)").HasDefaultValueSql("SYSUTCDATETIME()");
            e.Property(x => x.UpdatedAt).HasColumnType("datetime2(0)").HasDefaultValueSql("SYSUTCDATETIME()");
        });

        b.Entity<UserSession>(e =>
        {
            e.ToTable("sessions");
            e.HasKey(x => x.Token).HasName("PK_sessions");
            e.Property(x => x.Token).HasMaxLength(128);
            e.Property(x => x.CreatedAt).HasColumnType("datetime2(0)").HasDefaultValueSql("SYSUTCDATETIME()");
            e.Property(x => x.ExpiresAt).HasColumnType("datetime2(0)");
            e.HasIndex(x => x.UserId).HasDatabaseName("IX_sessions_user");
            e.HasIndex(x => x.ExpiresAt).HasDatabaseName("IX_sessions_expires");
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).HasConstraintName("FK_sessions_user").OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ImportBatch>(e =>
        {
            e.ToTable("import_batches");
            e.HasKey(x => x.Id).HasName("PK_import_batches");
            e.Property(x => x.Filename).HasMaxLength(500);
            e.Property(x => x.FileType).HasMaxLength(20);
            e.Property(x => x.Target).HasMaxLength(50).IsRequired();
            e.Property(x => x.CreatedAt).HasColumnType("datetime2(0)").HasDefaultValueSql("SYSUTCDATETIME()");
            e.Property(x => x.CreatedBy).HasMaxLength(255);
            e.Property(x => x.SummaryJson).HasColumnType("nvarchar(max)");
            e.HasIndex(x => new { x.CreatedAt, x.Id }).IsDescending(true, true).HasDatabaseName("IX_import_batches_created");
        });

        b.Entity<ImportBatchRow>(e =>
        {
            e.ToTable("import_batch_rows");
            e.HasKey(x => x.Id).HasName("PK_import_batch_rows");
            e.Property(x => x.Action).HasMaxLength(50).IsRequired();
            e.Property(x => x.Outcome).HasMaxLength(50).IsRequired();
            foreach (var p in new[] { nameof(ImportBatchRow.KeyValue), nameof(ImportBatchRow.Title), nameof(ImportBatchRow.DiffJson), nameof(ImportBatchRow.Message), nameof(ImportBatchRow.RawJson) })
            {
                e.Property(p).HasColumnType("nvarchar(max)");
            }
            e.HasIndex(x => x.BatchId).HasDatabaseName("IX_import_rows_batch");
            e.HasOne<ImportBatch>().WithMany(x => x.Rows).HasForeignKey(x => x.BatchId).HasConstraintName("FK_ibr_batch").OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<LegacySchemaMigration>(e =>
        {
            e.ToTable("schema_migrations");
            e.HasKey(x => x.Name).HasName("PK_schema_migrations");
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.AppliedAt).HasColumnType("datetime2(0)").HasDefaultValueSql("SYSUTCDATETIME()");
        });
    }
}
