using System.ComponentModel.DataAnnotations;
using ItrmTracker.Application.Import;
using ItrmTracker.Application.Lookups;
using ItrmTracker.Application.Query;
using ItrmTracker.Application.Reporting;
using ItrmTracker.Application.Sla;
using ItrmTracker.Application.Tracker;
using ItrmTracker.Application.Users;
using ItrmTracker.Domain.Entities;

namespace ItrmTracker.Web.ViewModels;

// ------------------------------------------------------------------ shared tracker metadata

/// <summary>Kind-specific labels and routes used by the shared tracker views.</summary>
public sealed record TrackerMeta(TrackerKind Kind, string Controller, string Plural, string Singular, string RefLabel, string RefField)
{
    public bool IsIncident => Kind == TrackerKind.Incident;

    public static readonly TrackerMeta BusinessRequests = new(TrackerKind.BusinessRequest, "BusinessRequests", "Business Requests", "Business Request", "BR No", "br_no");
    public static readonly TrackerMeta Incidents = new(TrackerKind.Incident, "Incidents", "Incidents", "Incident", "Incident ID", "incident_id");

    public static TrackerMeta For(TrackerKind kind) => kind == TrackerKind.Incident ? Incidents : BusinessRequests;

    /// <summary>localStorage key for column preferences (same keys as the original app).</summary>
    public string ColumnStorageKey => IsIncident ? "itrm_incident_cols" : "itrm_visible_cols";
}

/// <summary>A checkbox-dropdown filter (with optional search, "Only" and "Exclude" helpers in site.js).</summary>
public sealed record MultiSelectModel(string Name, string Label, IReadOnlyList<(string Value, string Text)> Options, IReadOnlyCollection<string> Selected, bool Searchable = false);

/// <summary>Bulk-editable lookup fields: column → (lookup key, label).</summary>
public static class BulkFieldInfo
{
    public static readonly IReadOnlyDictionary<string, (string LookupKey, string Label)> Map = new Dictionary<string, (string, string)>
    {
        ["business_unit_id"] = ("business-units", "Business Unit"),
        ["business_category_id"] = ("business-categories", "Business Category"),
        ["requester_id"] = ("requesters", "Requester"),
        ["priority_id"] = ("priorities", "Priority"),
        ["stage_id"] = ("stages", "Stage"),
        ["status_id"] = ("statuses", "Status"),
        ["tech_owner_id"] = ("tech-owners", "Tech Owner"),
        ["regulator_id"] = ("regulators", "Regulator"),
        ["rm_id"] = ("rms", "RM"),
    };
}

/// <summary>Register screen (Business Requests / Incidents tab).</summary>
public sealed class TrackerIndexViewModel
{
    public required TrackerMeta Meta { get; init; }
    public required IReadOnlyList<TrackerRecord> Rows { get; init; }
    public required TrackerQuery Query { get; init; }
    public required LookupSet Lookups { get; init; }
    public required IReadOnlyList<TrackerColumn> Columns { get; init; }
    public required HashSet<int> FollowedIds { get; init; }
    public required IReadOnlyList<string> BulkFields { get; init; }
    public bool CanWrite { get; init; }
}

/// <summary>Create / edit form. Dates are strings so the original validation messages can be reproduced exactly.</summary>
public sealed class TrackerFormViewModel
{
    public int? Id { get; set; }

    [Display(Name = "Reference")]
    [StringLength(255)]
    public string? RefNo { get; set; }

    [Display(Name = "CR")]
    [StringLength(255)]
    public string? Cr { get; set; }

    [Display(Name = "Submission Date")]
    public string? SubmissionDate { get; set; }
    public int? BusinessUnitId { get; set; }
    public int? BusinessCategoryId { get; set; }
    public int? RequesterId { get; set; }

    [Display(Name = "Title")]
    [StringLength(1000)]
    public string? Title { get; set; }
    public string? Description { get; set; }
    public int? PriorityId { get; set; }
    public int? StageId { get; set; }
    public int? StatusId { get; set; }
    public bool NewgenRelated { get; set; }
    public bool Estimated { get; set; }
    public bool BaRequired { get; set; }
    public bool BrdAssigned { get; set; }
    public bool Regulatory { get; set; }
    public int? RegulatorId { get; set; }
    public string? RegulatoryDeadline { get; set; }
    public string? AnalysisPlannedStartDate { get; set; }
    public string? AnalysisPlannedEndDate { get; set; }
    public string? DevPlannedStartDate { get; set; }
    public string? DevPlannedEndDate { get; set; }
    public string? QaPlannedStartDate { get; set; }
    public string? QaPlannedEndDate { get; set; }
    public string? UatPlannedStartDate { get; set; }
    public string? UatPlannedDate { get; set; }
    public string? CabDate { get; set; }
    public string? DeploymentPlannedEndDate { get; set; }
    public string? ClosedDate { get; set; }
    public int? RmId { get; set; }
    public List<int> TechOwnerIds { get; set; } = new();
    public List<int> SystemIds { get; set; } = new();

    // View-only data
    public TrackerKind Kind { get; set; }
    public LookupSet? Lookups { get; set; }
    public bool IsFollowed { get; set; }

    public TrackerInput ToInput() => new()
    {
        RefNo = RefNo, Cr = Cr, SubmissionDate = SubmissionDate, BusinessUnitId = BusinessUnitId, BusinessCategoryId = BusinessCategoryId,
        RequesterId = RequesterId, Title = Title, Description = Description, PriorityId = PriorityId, StageId = StageId, StatusId = StatusId,
        NewgenRelated = NewgenRelated, Estimated = Estimated, BaRequired = BaRequired, BrdAssigned = BrdAssigned, Regulatory = Regulatory,
        RegulatorId = RegulatorId, RegulatoryDeadline = RegulatoryDeadline, AnalysisPlannedStartDate = AnalysisPlannedStartDate,
        AnalysisPlannedEndDate = AnalysisPlannedEndDate, DevPlannedStartDate = DevPlannedStartDate, DevPlannedEndDate = DevPlannedEndDate,
        QaPlannedStartDate = QaPlannedStartDate, QaPlannedEndDate = QaPlannedEndDate, UatPlannedStartDate = UatPlannedStartDate,
        UatPlannedDate = UatPlannedDate, CabDate = CabDate, DeploymentPlannedEndDate = DeploymentPlannedEndDate, ClosedDate = ClosedDate,
        RmId = RmId, TechOwnerIds = TechOwnerIds, SystemIds = SystemIds,
    };

    public static TrackerFormViewModel From(TrackerRecord r) => new()
    {
        Id = r.Id, Kind = r.Kind, RefNo = r.RefNo, Cr = r.Cr, SubmissionDate = D(r.SubmissionDate), BusinessUnitId = r.BusinessUnitId,
        BusinessCategoryId = r.BusinessCategoryId, RequesterId = r.RequesterId, Title = r.Title, Description = r.Description,
        PriorityId = r.PriorityId, StageId = r.StageId, StatusId = r.StatusId, NewgenRelated = r.NewgenRelated, Estimated = r.Estimated,
        BaRequired = r.BaRequired, BrdAssigned = r.BrdAssigned, Regulatory = r.Regulatory, RegulatorId = r.RegulatorId,
        RegulatoryDeadline = D(r.RegulatoryDeadline), AnalysisPlannedStartDate = D(r.AnalysisPlannedStartDate),
        AnalysisPlannedEndDate = D(r.AnalysisPlannedEndDate), DevPlannedStartDate = D(r.DevPlannedStartDate), DevPlannedEndDate = D(r.DevPlannedEndDate),
        QaPlannedStartDate = D(r.QaPlannedStartDate), QaPlannedEndDate = D(r.QaPlannedEndDate), UatPlannedStartDate = D(r.UatPlannedStartDate),
        UatPlannedDate = D(r.UatPlannedDate), CabDate = D(r.CabDate), DeploymentPlannedEndDate = D(r.DeploymentPlannedEndDate),
        ClosedDate = D(r.ClosedDate), RmId = r.RmId, TechOwnerIds = r.TechOwnerIds.ToList(), SystemIds = r.SystemIds.ToList(),
    };

    private static string? D(DateOnly? d) => Application.Common.LegacyFormat.Date(d);
}

/// <summary>Read-only details page (the original details drawer).</summary>
public sealed class TrackerDetailsViewModel
{
    public required TrackerMeta Meta { get; init; }
    public required TrackerRecord Record { get; init; }
    public required IReadOnlyList<CommentDto> Comments { get; init; }
    public required IReadOnlyList<HistoryEntryView> History { get; init; }
    public bool IsFollowed { get; init; }
    public bool CanWrite { get; init; }

    /// <summary>Stage-driven SLA (business requests only).</summary>
    public RequestSlaView? StageSla { get; init; }
    public SlaHistoryView? StageSlaHistory { get; init; }
    public string? StageSlaError { get; init; }
    public CommentInputModel NewComment { get; init; } = new();
}

public sealed class CommentInputModel
{
    [Display(Name = "Comment")]
    public string? Body { get; set; }
}

/// <summary>Bulk lookup update of the selected rows.</summary>
public sealed class BulkEditInputModel
{
    public List<int> Ids { get; set; } = new();

    /// <summary>snake_case lookup column → id ("" = leave unchanged).</summary>
    public Dictionary<string, string?> Fields { get; set; } = new();
    public string? ReturnUrl { get; set; }
}

/// <summary>Outlook draft (.eml) of the selected rows.</summary>
public sealed class EmailDraftInputModel
{
    public List<int> Ids { get; set; } = new();
    public string? To { get; set; }
    public string? Cc { get; set; }
    public string? Subject { get; set; }
    public string? Message { get; set; }

    /// <summary>Comma-separated visible column keys (from the column chooser).</summary>
    public string? Columns { get; set; }
}

/// <summary>A stage action from the Stage SLA panel.</summary>
public sealed class StageActionInputModel
{
    public string? Stage { get; set; }
    public string? Status { get; set; }
    public string? PlannedEffort { get; set; }
    public string? ActualEffortDays { get; set; }
    public string? Note { get; set; }
}

// ------------------------------------------------------------------ account

public sealed class LoginViewModel
{
    [Display(Name = "Username")]
    public string? Username { get; set; }

    [Display(Name = "Password")]
    [DataType(DataType.Password)]
    public string? Password { get; set; }
    public string? ReturnUrl { get; set; }
}

public sealed class ChangePasswordViewModel
{
    [Display(Name = "Current password")]
    [DataType(DataType.Password)]
    public string? CurrentPassword { get; set; }

    [Display(Name = "New password")]
    [DataType(DataType.Password)]
    public string? NewPassword { get; set; }

    [Display(Name = "Confirm new password")]
    [DataType(DataType.Password)]
    public string? ConfirmNewPassword { get; set; }
}

public sealed class UserCreateViewModel
{
    public string? Username { get; set; }
    public string? Name { get; set; }

    [DataType(DataType.Password)]
    public string? Password { get; set; }
    public string? Role { get; set; } = Domain.Constants.AppRoles.ReadOnly;
}

public sealed class UserEditViewModel
{
    public int Id { get; set; }
    public string? Username { get; set; }
    public string? Name { get; set; }
    public string? Role { get; set; }
    public bool IsActive { get; set; }

    [DataType(DataType.Password)]
    [Display(Name = "New password (leave blank to keep)")]
    public string? Password { get; set; }
}

public sealed class UsersIndexViewModel
{
    public required IReadOnlyList<UserListItem> Users { get; init; }
    public int? CurrentUserId { get; init; }
}

// ------------------------------------------------------------------ lookups & SLA settings

public sealed class LookupsViewModel
{
    public required IReadOnlyList<LookupType> Types { get; init; }
    public required LookupType Active { get; init; }
    public required IReadOnlyList<LookupItem> Items { get; init; }
}

public sealed class LookupNameInputModel
{
    public string? Type { get; set; }
    public int Id { get; set; }

    [Display(Name = "Name")]
    public string? Name { get; set; }
}

public sealed class SlaSettingsViewModel
{
    public required SlaConfiguration Current { get; init; }
    public SlaConfigInput Form { get; init; } = new();
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    public static SlaConfigInput ToInput(SlaConfiguration c) => new()
    {
        Stages = c.Stages.Select(s => new SlaStageInput { Name = s.Name, StageOrder = N(s.StageOrder), IsPipeline = s.IsPipeline, BrDays = N(s.BrDays), IncidentDays = N(s.IncidentDays), EffortDays = N(s.EffortDays) }).ToList(),
        Priorities = c.Priorities.Select(p => new SlaPriorityInput { Name = p.Name, Factor = N(p.Factor), ResponseHours = N(p.ResponseHours), UpdateDays = N(p.UpdateDays) }).ToList(),
        Categories = c.Categories.Select(x => new SlaCategoryInput { Name = x.Name, Factor = N(x.Factor) }).ToList(),
        Statuses = c.Statuses.Select(x => new SlaStatusInput { Name = x.Name, State = x.State }).ToList(),
        WeekendDays = c.Settings.WeekendDays.Select(d => N(d)).ToList(),
        AtRiskThresholdPct = N(c.Settings.Engine.AtRiskThresholdPct),
        BreachThresholdPct = N(c.Settings.Engine.BreachThresholdPct),
        PriorityFactorMode = c.Settings.Engine.PriorityFactorMode,
        EnforceStageOrder = c.Settings.Engine.EnforceStageOrder ? "true" : "false",
        AutoStartNextStage = c.Settings.Engine.AutoStartNextStage ? "true" : "false",
    };

    private static string N(double v) => Application.Common.JsMath.ToJsString(v);
}

// ------------------------------------------------------------------ reporting

/// <summary>A dashboard panel of horizontal bars (renderBreakdown).</summary>
public sealed record BreakdownPanel(string Title, IReadOnlyList<BreakdownItem> Items, string Suffix = "");

public sealed class FollowUpViewModel
{
    public required FollowUpData Data { get; init; }
    public bool IncludeIncidents { get; init; }
}

public sealed class StageSlaDashboardViewModel
{
    public required SlaDashboard Data { get; init; }
    public string? Health { get; init; }
}

// ------------------------------------------------------------------ query console

public sealed class QueryViewModel
{
    public string? Sql { get; set; }
    public QueryResult? Result { get; set; }
    public string? Error { get; set; }
    public IReadOnlyList<SchemaTable> Schema { get; set; } = Array.Empty<SchemaTable>();
}

// ------------------------------------------------------------------ import wizard

/// <summary>Server-side wizard state (kept in memory for 30 minutes, keyed by an unguessable token + user id).</summary>
public sealed class ImportSession
{
    public required string Token { get; init; }
    public required int UserId { get; init; }
    public required string Target { get; set; }
    public required ParsedFile File { get; init; }
    public Dictionary<string, string?> Mapping { get; set; } = new();
    public AnalysisResult? Analysis { get; set; }
    public CommitResult? Result { get; set; }
}

public sealed class ImportUploadViewModel
{
    public string Target { get; set; } = "business_requests";
    public IReadOnlyList<ImportTarget> Targets { get; init; } = ImportDefinitions.Targets.Values.ToList();
}

public sealed class ImportStepViewModel
{
    public required ImportSession Session { get; init; }
    public required ImportTarget Target { get; init; }
    public int Step { get; init; }
}

public sealed class ImportHistoryViewModel
{
    public required IReadOnlyList<ImportBatch> Batches { get; init; }
}

public sealed class ImportHistoryDetailViewModel
{
    public required ImportBatch Batch { get; init; }
    public required IReadOnlyList<ImportBatchRow> Rows { get; init; }
}

public sealed class ErrorViewModel
{
    public int StatusCode { get; init; }
    public string? TraceId { get; init; }
    public string Message => StatusCode switch
    {
        403 => "You do not have permission to perform this action. Read-only accounts cannot change data.",
        404 => "The page or record you asked for was not found. It may have been deleted.",
        429 => "Too many attempts. Please wait a minute and try again.",
        _ => "Something went wrong while processing your request. Please try again; if the problem persists, contact support with the reference below.",
    };
}
