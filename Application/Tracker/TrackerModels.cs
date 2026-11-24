using ItrmTracker.Application.Common;
using ItrmTracker.Application.Sla;

namespace ItrmTracker.Application.Tracker;

/// <summary>
/// One business request or incident as shown in the register (the LIST_SELECT projection of the original routes)
/// plus the computed legacy SLA and aging fields.
/// </summary>
public sealed class TrackerRecord : ILegacySlaSubject
{
    public TrackerKind Kind { get; set; }
    public int Id { get; set; }

    /// <summary>br_no for business requests, incident_id for incidents.</summary>
    public string? RefNo { get; set; }
    public string? Cr { get; set; }
    public DateOnly? SubmissionDate { get; set; }
    public int? BusinessUnitId { get; set; }
    public int? BusinessCategoryId { get; set; }
    public int? RequesterId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int? PriorityId { get; set; }
    public int? StageId { get; set; }
    public int? StatusId { get; set; }
    public bool NewgenRelated { get; set; }
    public bool Estimated { get; set; }
    public bool BaRequired { get; set; }
    public bool BrdAssigned { get; set; }
    public int? TechOwnerId { get; set; }
    public bool Regulatory { get; set; }
    public int? RegulatorId { get; set; }
    public DateOnly? RegulatoryDeadline { get; set; }
    public DateOnly? AnalysisPlannedStartDate { get; set; }
    public DateOnly? AnalysisPlannedEndDate { get; set; }
    public DateOnly? DevPlannedStartDate { get; set; }
    public DateOnly? DevPlannedEndDate { get; set; }
    public DateOnly? QaPlannedStartDate { get; set; }
    public DateOnly? QaPlannedEndDate { get; set; }
    public DateOnly? UatPlannedStartDate { get; set; }
    public DateOnly? UatPlannedDate { get; set; }
    public DateOnly? CabDate { get; set; }
    public DateOnly? DeploymentPlannedEndDate { get; set; }
    public DateOnly? ClosedDate { get; set; }
    public string? LegacyComments { get; set; }
    public int? RmId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public string? UpdatedBy { get; set; }

    public string? BusinessUnit { get; set; }
    public string? BusinessCategory { get; set; }
    public string? Priority { get; set; }
    public string? Stage { get; set; }
    public string? Status { get; set; }

    /// <summary>All tech owner names, comma separated and sorted.</summary>
    public string? TechOwner { get; set; }
    public List<int> TechOwnerIds { get; set; } = new();
    public string? System { get; set; }
    public List<int> SystemIds { get; set; } = new();
    public string? Regulator { get; set; }
    public string? Requester { get; set; }
    public string? Rm { get; set; }
    public string? LastComment { get; set; }
    public DateTime? LastCommentAt { get; set; }

    /// <summary>Legacy SLA (populated by the service).</summary>
    public LegacySlaResult? Sla { get; set; }

    /// <summary>Days since the last update (computed when listed).</summary>
    public int? AgeDays { get; set; }
    public bool IsStale { get; set; }

    /// <summary>Concatenated comment thread (export only).</summary>
    public string? AllComments { get; set; }
}

/// <summary>Form input for create/update. Dates are strings so validation messages match the original exactly.</summary>
public sealed class TrackerInput
{
    public string? RefNo { get; set; }
    public string? Cr { get; set; }
    public string? SubmissionDate { get; set; }
    public int? BusinessUnitId { get; set; }
    public int? BusinessCategoryId { get; set; }
    public int? RequesterId { get; set; }
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

    /// <summary>null = leave the systems untouched (partial update semantics of the original API).</summary>
    public List<int>? SystemIds { get; set; }

    /// <summary>Snake-case view used by <see cref="Validation.RequestValidation"/> (same keys as the legacy JSON body).</summary>
    public IReadOnlyDictionary<string, string?> ToValidationBody() => new Dictionary<string, string?>
    {
        ["title"] = Title,
        ["submission_date"] = SubmissionDate,
        ["regulatory_deadline"] = RegulatoryDeadline,
        ["closed_date"] = ClosedDate,
        ["analysis_planned_start_date"] = AnalysisPlannedStartDate,
        ["analysis_planned_end_date"] = AnalysisPlannedEndDate,
        ["dev_planned_start_date"] = DevPlannedStartDate,
        ["dev_planned_end_date"] = DevPlannedEndDate,
        ["qa_planned_start_date"] = QaPlannedStartDate,
        ["qa_planned_end_date"] = QaPlannedEndDate,
        ["uat_planned_start_date"] = UatPlannedStartDate,
        ["uat_planned_date"] = UatPlannedDate,
        ["cab_date"] = CabDate,
        ["deployment_planned_end_date"] = DeploymentPlannedEndDate,
    };
}

/// <summary>Normalized values written to the database (port of <c>normalizeBody()</c>).</summary>
public sealed class TrackerData
{
    public string? RefNo { get; set; }
    public string? Cr { get; set; }
    public DateOnly? SubmissionDate { get; set; }
    public int? BusinessUnitId { get; set; }
    public int? BusinessCategoryId { get; set; }
    public int? RequesterId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int? PriorityId { get; set; }
    public int? StageId { get; set; }
    public int? StatusId { get; set; }
    public bool NewgenRelated { get; set; }
    public bool Estimated { get; set; }
    public bool BaRequired { get; set; }
    public bool BrdAssigned { get; set; }
    public int? TechOwnerId { get; set; }
    public bool Regulatory { get; set; }
    public int? RegulatorId { get; set; }
    public DateOnly? RegulatoryDeadline { get; set; }
    public DateOnly? AnalysisPlannedStartDate { get; set; }
    public DateOnly? AnalysisPlannedEndDate { get; set; }
    public DateOnly? DevPlannedStartDate { get; set; }
    public DateOnly? DevPlannedEndDate { get; set; }
    public DateOnly? QaPlannedStartDate { get; set; }
    public DateOnly? QaPlannedEndDate { get; set; }
    public DateOnly? UatPlannedStartDate { get; set; }
    public DateOnly? UatPlannedDate { get; set; }
    public DateOnly? CabDate { get; set; }
    public DateOnly? DeploymentPlannedEndDate { get; set; }
    public DateOnly? ClosedDate { get; set; }
    public int? RmId { get; set; }
    public List<int> TechOwnerIds { get; set; } = new();
    public List<int>? SystemIds { get; set; }

    private static string? Blank(string? s) => string.IsNullOrEmpty(s) ? null : s;
    private static DateOnly? D(string? s) => LegacyFormat.TryParseIsoDate(s, out var d) ? d : null;
    private static List<int> Ids(IEnumerable<int>? ids) => (ids ?? Enumerable.Empty<int>()).Where(n => n > 0).Distinct().ToList();

    /// <summary>
    /// "" → null for every field, booleans as given, BR-only fields dropped for incidents, the first selected tech owner
    /// becomes the legacy "primary" <c>tech_owner_id</c>.
    /// </summary>
    public static TrackerData From(TrackerInput i, TrackerKind kind)
    {
        var isBr = kind == TrackerKind.BusinessRequest;
        var techOwnerIds = Ids(i.TechOwnerIds);
        return new TrackerData
        {
            RefNo = Blank(i.RefNo),
            Cr = Blank(i.Cr),
            SubmissionDate = isBr ? D(i.SubmissionDate) : null,
            BusinessUnitId = i.BusinessUnitId,
            BusinessCategoryId = isBr ? i.BusinessCategoryId : null,
            RequesterId = i.RequesterId,
            Title = i.Title ?? string.Empty,
            Description = Blank(i.Description),
            PriorityId = i.PriorityId,
            StageId = i.StageId,
            StatusId = i.StatusId,
            NewgenRelated = i.NewgenRelated,
            Estimated = i.Estimated,
            BaRequired = isBr && i.BaRequired,
            BrdAssigned = isBr && i.BrdAssigned,
            TechOwnerId = techOwnerIds.Count > 0 ? techOwnerIds[0] : null,
            Regulatory = isBr && i.Regulatory,
            RegulatorId = isBr ? i.RegulatorId : null,
            RegulatoryDeadline = isBr ? D(i.RegulatoryDeadline) : null,
            AnalysisPlannedStartDate = isBr ? D(i.AnalysisPlannedStartDate) : null,
            AnalysisPlannedEndDate = isBr ? D(i.AnalysisPlannedEndDate) : null,
            DevPlannedStartDate = D(i.DevPlannedStartDate),
            DevPlannedEndDate = D(i.DevPlannedEndDate),
            QaPlannedStartDate = D(i.QaPlannedStartDate),
            QaPlannedEndDate = D(i.QaPlannedEndDate),
            UatPlannedStartDate = D(i.UatPlannedStartDate),
            UatPlannedDate = D(i.UatPlannedDate),
            CabDate = D(i.CabDate),
            DeploymentPlannedEndDate = D(i.DeploymentPlannedEndDate),
            ClosedDate = D(i.ClosedDate),
            RmId = i.RmId,
            TechOwnerIds = techOwnerIds,
            SystemIds = i.SystemIds is null ? null : Ids(i.SystemIds),
        };
    }
}

/// <summary>Register filter/sort state (the toolbar of the original single-page app).</summary>
public sealed class TrackerQuery
{
    /// <summary>open (default) | closed | all</summary>
    public string View { get; set; } = "open";
    public string? Search { get; set; }
    public List<int> StageIds { get; set; } = new();
    public List<int> StatusIds { get; set; } = new();
    public List<int> PriorityIds { get; set; } = new();
    public List<int> BusinessUnitIds { get; set; } = new();
    public List<int> BusinessCategoryIds { get; set; } = new();
    public List<int> RequesterIds { get; set; } = new();
    public List<int> TechOwnerIds { get; set; } = new();
    public List<int> SystemIds { get; set; } = new();
    public List<int> RmIds { get; set; } = new();
    public List<string> SlaFlags { get; set; } = new();
    public bool Stale { get; set; }

    /// <summary>Boolean flag filters (BR only): newgen_related, estimated, ba_required, brd_assigned, regulatory.</summary>
    public List<string> Flags { get; set; } = new();
    public string? Sort { get; set; }

    /// <summary>asc | desc</summary>
    public string? Dir { get; set; }

    public bool HasActiveFilters =>
        !string.IsNullOrWhiteSpace(Search) || StageIds.Count > 0 || StatusIds.Count > 0 || PriorityIds.Count > 0 ||
        BusinessUnitIds.Count > 0 || BusinessCategoryIds.Count > 0 || RequesterIds.Count > 0 || TechOwnerIds.Count > 0 ||
        SystemIds.Count > 0 || RmIds.Count > 0 || SlaFlags.Count > 0 || Stale || Flags.Count > 0;
}

/// <summary>A comment on a request or incident.</summary>
public sealed record CommentDto(int Id, string Body, DateTime CreatedAt, string? CreatedBy);

/// <summary>A raw history row (snapshot JSON).</summary>
public sealed record HistoryRow(int Id, DateTime ChangedAt, string? ChangedBy, string ChangeType, string SnapshotJson);

/// <summary>One field change between two consecutive snapshots.</summary>
public sealed record HistoryChange(string Label, string Before, string After);

/// <summary>A history entry ready to display.</summary>
public sealed record HistoryEntryView(int Id, DateTime ChangedAt, string? ChangedBy, string ChangeType, string ChangeLabel, IReadOnlyList<HistoryChange> Changes);

/// <summary>Result of a bulk lookup update.</summary>
public sealed record BulkUpdateResult(int Updated, int Total, IReadOnlyList<string> Errors);
