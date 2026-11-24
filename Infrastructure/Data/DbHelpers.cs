using System.Globalization;
using ItrmTracker.Application.Tracker;
using ItrmTracker.Domain.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace ItrmTracker.Infrastructure.Data;

/// <summary>Unit-of-work helpers shared by the repositories.</summary>
internal static class DbHelpers
{
    /// <summary>
    /// Runs <paramref name="work"/> in one database transaction (nested calls run inline). The whole unit is retried by
    /// the SQL Server execution strategy on transient failures; on error the transaction is rolled back and the change
    /// tracker cleared so the scoped context stays usable.
    /// </summary>
    public static async Task<T> InTransactionAsync<T>(ItrmDbContext db, Func<Task<T>> work, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is not null) return await work();
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            try
            {
                var result = await work();
                await tx.CommitAsync(ct);
                return result;
            }
            catch
            {
                await tx.RollbackAsync(CancellationToken.None);
                db.ChangeTracker.Clear();
                throw;
            }
        });
    }

    /// <summary>SaveChanges with SQL error translation (unique index → <see cref="DuplicateKeyException"/>, FK → <see cref="ReferenceConstraintException"/>).</summary>
    public static async Task<int> SaveAsync(ItrmDbContext db, CancellationToken ct)
    {
        try
        {
            return await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (Translate(ex) is { } translated)
        {
            db.ChangeTracker.Clear();
            throw translated;
        }
    }

    public static Exception? Translate(Exception ex)
    {
        var sql = ex as SqlException ?? ex.InnerException as SqlException;
        return sql?.Number switch
        {
            2627 or 2601 => new DuplicateKeyException("A unique value is already in use.", ex),
            547 => new ReferenceConstraintException("The row is still referenced by other records.", ex),
            _ => null,
        };
    }

    /// <summary>Wraps raw SQL execution with the same translation.</summary>
    public static async Task<T> TranslateAsync<T>(Func<Task<T>> work)
    {
        try
        {
            return await work();
        }
        catch (Exception ex) when (Translate(ex) is { } translated)
        {
            throw translated;
        }
    }
}

/// <summary>
/// Converts entities to the "SELECT *" row shape the legacy API produced (snake_case keys in table column order,
/// DATE → YYYY-MM-DD, DATETIME2 → YYYY-MM-DD HH:MM:SS, BIT → 0/1). Used for history snapshots and the import diff.
/// </summary>
internal static class RawRows
{
    private static string? D(DateOnly? d) => d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string DT(DateTime d) => d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    private static int B(bool b) => b ? 1 : 0;

    public static Dictionary<string, object?> Of(BusinessRequest e) => new()
    {
        ["id"] = e.Id, ["br_no"] = e.BrNo, ["cr"] = e.Cr, ["submission_date"] = D(e.SubmissionDate),
        ["business_unit_id"] = e.BusinessUnitId, ["business_category_id"] = e.BusinessCategoryId, ["requester"] = e.RequesterText,
        ["requester_id"] = e.RequesterId, ["title"] = e.Title, ["description"] = e.Description, ["priority_id"] = e.PriorityId,
        ["stage_id"] = e.StageId, ["status_id"] = e.StatusId, ["newgen_related"] = B(e.NewgenRelated), ["estimated"] = B(e.Estimated),
        ["ba_required"] = B(e.BaRequired), ["brd_assigned"] = B(e.BrdAssigned), ["tech_owner_id"] = e.TechOwnerId, ["regulatory"] = B(e.Regulatory),
        ["regulator_id"] = e.RegulatorId, ["regulatory_deadline"] = D(e.RegulatoryDeadline),
        ["analysis_planned_start_date"] = D(e.AnalysisPlannedStartDate), ["analysis_planned_end_date"] = D(e.AnalysisPlannedEndDate),
        ["dev_planned_start_date"] = D(e.DevPlannedStartDate), ["dev_planned_end_date"] = D(e.DevPlannedEndDate),
        ["qa_planned_start_date"] = D(e.QaPlannedStartDate), ["qa_planned_end_date"] = D(e.QaPlannedEndDate),
        ["uat_planned_start_date"] = D(e.UatPlannedStartDate), ["uat_planned_date"] = D(e.UatPlannedDate), ["cab_date"] = D(e.CabDate),
        ["deployment_planned_end_date"] = D(e.DeploymentPlannedEndDate), ["closed_date"] = D(e.ClosedDate), ["comments"] = e.Comments,
        ["rm_id"] = e.RmId, ["created_at"] = DT(e.CreatedAt), ["updated_at"] = DT(e.UpdatedAt), ["created_by"] = e.CreatedBy, ["updated_by"] = e.UpdatedBy,
    };

    public static Dictionary<string, object?> Of(Incident e) => new()
    {
        ["id"] = e.Id, ["incident_id"] = e.IncidentRef, ["cr"] = e.Cr, ["business_unit_id"] = e.BusinessUnitId, ["requester_id"] = e.RequesterId,
        ["title"] = e.Title, ["description"] = e.Description, ["priority_id"] = e.PriorityId, ["stage_id"] = e.StageId, ["status_id"] = e.StatusId,
        ["newgen_related"] = B(e.NewgenRelated), ["estimated"] = B(e.Estimated), ["tech_owner_id"] = e.TechOwnerId,
        ["dev_planned_start_date"] = D(e.DevPlannedStartDate), ["dev_planned_end_date"] = D(e.DevPlannedEndDate),
        ["qa_planned_start_date"] = D(e.QaPlannedStartDate), ["qa_planned_end_date"] = D(e.QaPlannedEndDate),
        ["uat_planned_start_date"] = D(e.UatPlannedStartDate), ["uat_planned_date"] = D(e.UatPlannedDate), ["cab_date"] = D(e.CabDate),
        ["deployment_planned_end_date"] = D(e.DeploymentPlannedEndDate), ["closed_date"] = D(e.ClosedDate), ["rm_id"] = e.RmId,
        ["created_at"] = DT(e.CreatedAt), ["updated_at"] = DT(e.UpdatedAt), ["created_by"] = e.CreatedBy, ["updated_by"] = e.UpdatedBy,
    };

    private static string? S(object? v) => v switch { null => null, string s => s.Length == 0 ? null : s, _ => Convert.ToString(v, CultureInfo.InvariantCulture) };
    private static int? I(object? v) => v switch { null => null, int i => i, long l => (int)l, string s when int.TryParse(s, out var x) => x, _ => null };
    private static bool Bo(object? v) => v switch { null => false, bool b => b, int i => i != 0, long l => l != 0, string s => s.Length > 0 && s != "0", _ => true };
    private static DateOnly? Dt(object? v) => S(v) is { } s && DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    /// <summary>Applies an import body (snake_case entity columns) to a business request.</summary>
    public static void Apply(BusinessRequest e, IReadOnlyDictionary<string, object?> b)
    {
        e.BrNo = S(b.GetValueOrDefault("br_no")); e.Cr = S(b.GetValueOrDefault("cr")); e.SubmissionDate = Dt(b.GetValueOrDefault("submission_date"));
        e.BusinessUnitId = I(b.GetValueOrDefault("business_unit_id")); e.BusinessCategoryId = I(b.GetValueOrDefault("business_category_id"));
        e.RequesterId = I(b.GetValueOrDefault("requester_id")); e.Title = S(b.GetValueOrDefault("title")) ?? string.Empty;
        e.Description = S(b.GetValueOrDefault("description")); e.PriorityId = I(b.GetValueOrDefault("priority_id"));
        e.StageId = I(b.GetValueOrDefault("stage_id")); e.StatusId = I(b.GetValueOrDefault("status_id"));
        e.NewgenRelated = Bo(b.GetValueOrDefault("newgen_related")); e.Estimated = Bo(b.GetValueOrDefault("estimated"));
        e.BaRequired = Bo(b.GetValueOrDefault("ba_required")); e.BrdAssigned = Bo(b.GetValueOrDefault("brd_assigned"));
        e.TechOwnerId = I(b.GetValueOrDefault("tech_owner_id")); e.Regulatory = Bo(b.GetValueOrDefault("regulatory"));
        e.RegulatorId = I(b.GetValueOrDefault("regulator_id")); e.RegulatoryDeadline = Dt(b.GetValueOrDefault("regulatory_deadline"));
        e.AnalysisPlannedStartDate = Dt(b.GetValueOrDefault("analysis_planned_start_date")); e.AnalysisPlannedEndDate = Dt(b.GetValueOrDefault("analysis_planned_end_date"));
        ApplyPipeline(b, out var ds, out var de, out var qs, out var qe, out var us, out var ue, out var cab, out var dep, out var closed);
        e.DevPlannedStartDate = ds; e.DevPlannedEndDate = de; e.QaPlannedStartDate = qs; e.QaPlannedEndDate = qe;
        e.UatPlannedStartDate = us; e.UatPlannedDate = ue; e.CabDate = cab; e.DeploymentPlannedEndDate = dep; e.ClosedDate = closed;
        e.RmId = I(b.GetValueOrDefault("rm_id"));
    }

    /// <summary>Applies an import body to an incident.</summary>
    public static void Apply(Incident e, IReadOnlyDictionary<string, object?> b)
    {
        e.IncidentRef = S(b.GetValueOrDefault("incident_id")); e.Cr = S(b.GetValueOrDefault("cr"));
        e.BusinessUnitId = I(b.GetValueOrDefault("business_unit_id")); e.RequesterId = I(b.GetValueOrDefault("requester_id"));
        e.Title = S(b.GetValueOrDefault("title")) ?? string.Empty; e.Description = S(b.GetValueOrDefault("description"));
        e.PriorityId = I(b.GetValueOrDefault("priority_id")); e.StageId = I(b.GetValueOrDefault("stage_id")); e.StatusId = I(b.GetValueOrDefault("status_id"));
        e.NewgenRelated = Bo(b.GetValueOrDefault("newgen_related")); e.Estimated = Bo(b.GetValueOrDefault("estimated"));
        e.TechOwnerId = I(b.GetValueOrDefault("tech_owner_id"));
        ApplyPipeline(b, out var ds, out var de, out var qs, out var qe, out var us, out var ue, out var cab, out var dep, out var closed);
        e.DevPlannedStartDate = ds; e.DevPlannedEndDate = de; e.QaPlannedStartDate = qs; e.QaPlannedEndDate = qe;
        e.UatPlannedStartDate = us; e.UatPlannedDate = ue; e.CabDate = cab; e.DeploymentPlannedEndDate = dep; e.ClosedDate = closed;
        e.RmId = I(b.GetValueOrDefault("rm_id"));
    }

    private static void ApplyPipeline(IReadOnlyDictionary<string, object?> b, out DateOnly? ds, out DateOnly? de, out DateOnly? qs, out DateOnly? qe,
        out DateOnly? us, out DateOnly? ue, out DateOnly? cab, out DateOnly? dep, out DateOnly? closed)
    {
        ds = Dt(b.GetValueOrDefault("dev_planned_start_date")); de = Dt(b.GetValueOrDefault("dev_planned_end_date"));
        qs = Dt(b.GetValueOrDefault("qa_planned_start_date")); qe = Dt(b.GetValueOrDefault("qa_planned_end_date"));
        us = Dt(b.GetValueOrDefault("uat_planned_start_date")); ue = Dt(b.GetValueOrDefault("uat_planned_date"));
        cab = Dt(b.GetValueOrDefault("cab_date")); dep = Dt(b.GetValueOrDefault("deployment_planned_end_date")); closed = Dt(b.GetValueOrDefault("closed_date"));
    }
}
