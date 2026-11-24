using System.Globalization;
using ItrmTracker.Application.Common;
using ItrmTracker.Application.Sla;

namespace ItrmTracker.Application.Tracker;

/// <summary>Column metadata shared by the register table, history diff, copy, e-mail and Excel export.</summary>
public sealed record TrackerColumn(
    string Key,
    string Label,
    bool DefaultVisible = false,
    bool IsBool = false,
    bool IsNum = false,
    bool IsDateTime = false,
    bool Wrap = false,
    int? MaxLen = null,
    bool Required = false,
    bool IsSla = false);

/// <summary>
/// Column definitions identical to TABLE_COLUMNS / INCIDENT_TABLE_COLUMNS / EXPORT_COLUMNS of the original app,
/// including the post-hoc insertion of the System and SLA columns.
/// </summary>
public static class TrackerColumns
{
    private static readonly TrackerColumn[] SlaTableColumns =
    {
        new("sla_flag", "SLA Status", DefaultVisible: true, IsSla: true),
        new("sla_target_date", "SLA Target", DefaultVisible: true),
        new("sla_remaining_days", "SLA Days Left", IsNum: true),
    };

    private static readonly TrackerColumn[] SlaExportColumns =
    {
        new("sla_flag", "SLA Status"),
        new("sla_state", "SLA State"),
        new("sla_remaining_days", "SLA Remaining (working days)"),
        new("sla_effort_days", "SLA Remaining Effort (man-days)"),
        new("sla_target_date", "SLA Target Date"),
        new("sla_gap_days", "SLA Target vs Planned (days)"),
    };

    public static readonly IReadOnlyList<TrackerColumn> BusinessRequestTable = WithInserts(new List<TrackerColumn>
    {
        new("br_no", "BR No", true),
        new("cr", "CR", true, Wrap: true),
        new("submission_date", "Submission Date", true),
        new("title", "Title", true, Required: true, Wrap: true),
        new("description", "Description", Wrap: true, MaxLen: 120),
        new("business_unit", "Business Unit", true),
        new("business_category", "Business Category"),
        new("requester", "Requester"),
        new("priority", "Priority", true),
        new("stage", "Stage", true),
        new("status", "Status", true),
        new("tech_owner", "Tech Owner", true),
        new("rm", "RM", true),
        new("newgen_related", "Newgen Related", IsBool: true),
        new("estimated", "Estimated", IsBool: true),
        new("ba_required", "BA Required", IsBool: true),
        new("brd_assigned", "BRD Assigned", IsBool: true),
        new("regulatory", "Regulatory", IsBool: true),
        new("regulator", "Regulator"),
        new("regulatory_deadline", "Regulatory Deadline"),
        new("analysis_planned_start_date", "Analysis Planned Start"),
        new("analysis_planned_end_date", "Analysis Planned"),
        new("dev_planned_start_date", "Dev Planned Start"),
        new("dev_planned_end_date", "Dev Planned End"),
        new("qa_planned_start_date", "QA Planned Start"),
        new("qa_planned_end_date", "QA Planned End"),
        new("uat_planned_start_date", "UAT Planned Start"),
        new("uat_planned_date", "UAT Planned End"),
        new("cab_date", "CAB Date"),
        new("deployment_planned_end_date", "Deployment Planned End", true),
        new("closed_date", "Closed Date"),
        new("last_comment", "Last Comment", true, Wrap: true, MaxLen: 80),
        new("created_at", "Created Date", IsDateTime: true),
        new("updated_at", "Last Updated", IsDateTime: true),
        new("age_days", "Days Since Update", IsNum: true),
    }, table: true);

    public static readonly IReadOnlyList<TrackerColumn> IncidentTable = WithInserts(new List<TrackerColumn>
    {
        new("incident_id", "Incident ID", true),
        new("cr", "CR", true),
        new("title", "Title", true, Required: true, Wrap: true),
        new("description", "Description", Wrap: true, MaxLen: 120),
        new("business_unit", "Business Unit", true),
        new("requester", "Requester"),
        new("priority", "Priority", true),
        new("stage", "Stage", true),
        new("status", "Status", true),
        new("tech_owner", "Tech Owner", true),
        new("rm", "RM", true),
        new("newgen_related", "Newgen Related", IsBool: true),
        new("estimated", "Estimated", IsBool: true),
        new("dev_planned_start_date", "Dev Planned Start"),
        new("dev_planned_end_date", "Dev Planned End"),
        new("qa_planned_start_date", "QA Planned Start"),
        new("qa_planned_end_date", "QA Planned End"),
        new("uat_planned_start_date", "UAT Planned Start"),
        new("uat_planned_date", "UAT Planned End"),
        new("cab_date", "CAB Date"),
        new("deployment_planned_end_date", "Deployment Planned End", true),
        new("closed_date", "Closed Date"),
        new("last_comment", "Last Comment", true, Wrap: true, MaxLen: 80),
        new("created_at", "Created Date", IsDateTime: true),
        new("updated_at", "Last Updated", IsDateTime: true),
        new("age_days", "Days Since Update", IsNum: true),
    }, table: true);

    public static readonly IReadOnlyList<TrackerColumn> BusinessRequestExport = WithInserts(new List<TrackerColumn>
    {
        new("br_no", "BR No"), new("cr", "CR"), new("submission_date", "Submission Date"), new("title", "Title"),
        new("business_unit", "Business Unit"), new("business_category", "Business Category"), new("requester", "Requester"),
        new("priority", "Priority"), new("stage", "Stage"), new("status", "Status"), new("tech_owner", "Tech Owner"), new("rm", "RM"),
        new("newgen_related", "Newgen Related", IsBool: true), new("estimated", "Estimated", IsBool: true),
        new("ba_required", "BA Required", IsBool: true), new("brd_assigned", "BRD Assigned", IsBool: true),
        new("regulatory", "Regulatory", IsBool: true), new("regulator", "Regulator"), new("regulatory_deadline", "Regulatory Deadline"),
        new("analysis_planned_start_date", "Analysis Planned Start"), new("analysis_planned_end_date", "Analysis Planned End"),
        new("dev_planned_start_date", "Dev Planned Start"), new("dev_planned_end_date", "Dev Planned End"),
        new("qa_planned_start_date", "QA Planned Start"), new("qa_planned_end_date", "QA Planned End"),
        new("uat_planned_start_date", "UAT Planned Start"), new("uat_planned_date", "UAT Planned End"),
        new("cab_date", "CAB Date"), new("deployment_planned_end_date", "Deployment Planned End"), new("closed_date", "Closed Date"),
        new("all_comments", "Comments"), new("created_at", "Created Date"), new("updated_at", "Last Updated"), new("age_days", "Days Since Update"),
    }, table: false);

    public static readonly IReadOnlyList<TrackerColumn> IncidentExport = WithInserts(new List<TrackerColumn>
    {
        new("incident_id", "Incident ID"), new("cr", "CR"), new("title", "Title"), new("business_unit", "Business Unit"),
        new("requester", "Requester"), new("priority", "Priority"), new("stage", "Stage"), new("status", "Status"),
        new("tech_owner", "Tech Owner"), new("rm", "RM"),
        new("newgen_related", "Newgen Related", IsBool: true), new("estimated", "Estimated", IsBool: true),
        new("dev_planned_start_date", "Dev Planned Start"), new("dev_planned_end_date", "Dev Planned End"),
        new("qa_planned_start_date", "QA Planned Start"), new("qa_planned_end_date", "QA Planned End"),
        new("uat_planned_start_date", "UAT Planned Start"), new("uat_planned_date", "UAT Planned End"),
        new("cab_date", "CAB Date"), new("deployment_planned_end_date", "Deployment Planned End"), new("closed_date", "Closed Date"),
        new("all_comments", "Comments"), new("created_at", "Created Date"), new("updated_at", "Last Updated"), new("age_days", "Days Since Update"),
    }, table: false);

    private static List<TrackerColumn> WithInserts(List<TrackerColumn> cols, bool table)
    {
        var statusIdx = cols.FindIndex(c => c.Key == "status");
        cols.Insert(statusIdx + 1, new TrackerColumn("system", "System", DefaultVisible: table));
        if (table) cols.InsertRange(cols.FindIndex(c => c.Key == "last_comment"), SlaTableColumns);
        else cols.InsertRange(cols.FindIndex(c => c.Key == "all_comments"), SlaExportColumns);
        return cols;
    }

    public static IReadOnlyList<TrackerColumn> Table(TrackerKind kind) => kind == TrackerKind.Incident ? IncidentTable : BusinessRequestTable;
    public static IReadOnlyList<TrackerColumn> Export(TrackerKind kind) => kind == TrackerKind.Incident ? IncidentExport : BusinessRequestExport;

    /// <summary>Raw value of a column (null when empty).</summary>
    public static object? Value(TrackerRecord r, string key) => key switch
    {
        "br_no" or "incident_id" => r.RefNo,
        "cr" => r.Cr,
        "submission_date" => LegacyFormat.Date(r.SubmissionDate),
        "title" => r.Title,
        "description" => r.Description,
        "business_unit" => r.BusinessUnit,
        "business_category" => r.BusinessCategory,
        "requester" => r.Requester,
        "priority" => r.Priority,
        "stage" => r.Stage,
        "status" => r.Status,
        "system" => r.System,
        "tech_owner" => r.TechOwner,
        "rm" => r.Rm,
        "newgen_related" => r.NewgenRelated,
        "estimated" => r.Estimated,
        "ba_required" => r.BaRequired,
        "brd_assigned" => r.BrdAssigned,
        "regulatory" => r.Regulatory,
        "regulator" => r.Regulator,
        "regulatory_deadline" => LegacyFormat.Date(r.RegulatoryDeadline),
        "analysis_planned_start_date" => LegacyFormat.Date(r.AnalysisPlannedStartDate),
        "analysis_planned_end_date" => LegacyFormat.Date(r.AnalysisPlannedEndDate),
        "dev_planned_start_date" => LegacyFormat.Date(r.DevPlannedStartDate),
        "dev_planned_end_date" => LegacyFormat.Date(r.DevPlannedEndDate),
        "qa_planned_start_date" => LegacyFormat.Date(r.QaPlannedStartDate),
        "qa_planned_end_date" => LegacyFormat.Date(r.QaPlannedEndDate),
        "uat_planned_start_date" => LegacyFormat.Date(r.UatPlannedStartDate),
        "uat_planned_date" => LegacyFormat.Date(r.UatPlannedDate),
        "cab_date" => LegacyFormat.Date(r.CabDate),
        "deployment_planned_end_date" => LegacyFormat.Date(r.DeploymentPlannedEndDate),
        "closed_date" => LegacyFormat.Date(r.ClosedDate),
        "last_comment" => r.LastComment,
        "all_comments" => r.AllComments,
        "created_at" => LegacyFormat.DateTime(r.CreatedAt),
        "updated_at" => LegacyFormat.DateTime(r.UpdatedAt),
        "age_days" => r.AgeDays,
        "sla_flag" => r.Sla?.SlaFlag,
        "sla_state" => r.Sla?.SlaState,
        "sla_remaining_days" => r.Sla?.SlaRemainingDays,
        "sla_effort_days" => r.Sla?.SlaEffortDays,
        "sla_target_date" => r.Sla?.SlaTargetDate,
        "sla_gap_days" => r.Sla?.SlaGapDays,
        _ => null,
    };

    /// <summary>Plain-text cell value for copy / e-mail / export (booleans as Yes/No, numbers invariant).</summary>
    public static string Text(TrackerRecord r, TrackerColumn col)
    {
        var v = Value(r, col.Key);
        return v switch
        {
            null => string.Empty,
            bool b => b ? "Yes" : "No",
            double d => JsMath.ToJsString(d),
            int i => i.ToString(CultureInfo.InvariantCulture),
            _ => v.ToString() ?? string.Empty,
        };
    }
}
