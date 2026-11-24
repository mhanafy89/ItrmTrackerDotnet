using System.Text.Encodings.Web;
using System.Text.Json;
using ItrmTracker.Application.Common;
using ItrmTracker.Application.Sla;

namespace ItrmTracker.Application.Tracker;

/// <summary>
/// Builds the JSON snapshots stored in business_request_history / incident_history. The keys and value formats are
/// identical to the rows the original Node.js API stored (snake_case, dates as strings, booleans as 0/1), so history
/// written by both applications can be diffed together.
/// </summary>
public static class TrackerSnapshot
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    private static int B(bool v) => v ? 1 : 0;
    private static string? Ids(List<int> ids) => ids.Count == 0 ? null : string.Join(",", ids);

    /// <summary>Snapshot of the full list row (LIST_SELECT) – used for create / update / bulk-update.</summary>
    public static string FromListRow(TrackerRecord r)
    {
        var d = new Dictionary<string, object?>();
        d["id"] = r.Id;
        if (r.Kind == TrackerKind.BusinessRequest)
        {
            d["br_no"] = r.RefNo;
            d["cr"] = r.Cr;
            d["submission_date"] = LegacyFormat.Date(r.SubmissionDate);
            d["business_unit_id"] = r.BusinessUnitId;
            d["business_category_id"] = r.BusinessCategoryId;
            d["requester_id"] = r.RequesterId;
            d["title"] = r.Title;
            d["description"] = r.Description;
            d["priority_id"] = r.PriorityId;
            d["stage_id"] = r.StageId;
            d["status_id"] = r.StatusId;
            d["newgen_related"] = B(r.NewgenRelated);
            d["estimated"] = B(r.Estimated);
            d["ba_required"] = B(r.BaRequired);
            d["brd_assigned"] = B(r.BrdAssigned);
            d["tech_owner_id"] = r.TechOwnerId;
            d["regulatory"] = B(r.Regulatory);
            d["regulator_id"] = r.RegulatorId;
            d["regulatory_deadline"] = LegacyFormat.Date(r.RegulatoryDeadline);
            d["analysis_planned_start_date"] = LegacyFormat.Date(r.AnalysisPlannedStartDate);
            d["analysis_planned_end_date"] = LegacyFormat.Date(r.AnalysisPlannedEndDate);
        }
        else
        {
            d["incident_id"] = r.RefNo;
            d["cr"] = r.Cr;
            d["business_unit_id"] = r.BusinessUnitId;
            d["requester_id"] = r.RequesterId;
            d["title"] = r.Title;
            d["description"] = r.Description;
            d["priority_id"] = r.PriorityId;
            d["stage_id"] = r.StageId;
            d["status_id"] = r.StatusId;
            d["newgen_related"] = B(r.NewgenRelated);
            d["estimated"] = B(r.Estimated);
            d["tech_owner_id"] = r.TechOwnerId;
        }
        d["dev_planned_start_date"] = LegacyFormat.Date(r.DevPlannedStartDate);
        d["dev_planned_end_date"] = LegacyFormat.Date(r.DevPlannedEndDate);
        d["qa_planned_start_date"] = LegacyFormat.Date(r.QaPlannedStartDate);
        d["qa_planned_end_date"] = LegacyFormat.Date(r.QaPlannedEndDate);
        d["uat_planned_start_date"] = LegacyFormat.Date(r.UatPlannedStartDate);
        d["uat_planned_date"] = LegacyFormat.Date(r.UatPlannedDate);
        d["cab_date"] = LegacyFormat.Date(r.CabDate);
        d["deployment_planned_end_date"] = LegacyFormat.Date(r.DeploymentPlannedEndDate);
        d["closed_date"] = LegacyFormat.Date(r.ClosedDate);
        d["rm_id"] = r.RmId;
        if (r.Kind == TrackerKind.BusinessRequest) d["comments"] = r.LegacyComments;
        d["created_at"] = LegacyFormat.DateTime(r.CreatedAt);
        d["updated_at"] = LegacyFormat.DateTime(r.UpdatedAt);
        d["created_by"] = r.CreatedBy;
        d["updated_by"] = r.UpdatedBy;
        d["business_unit"] = r.BusinessUnit;
        if (r.Kind == TrackerKind.BusinessRequest) d["business_category"] = r.BusinessCategory;
        d["priority"] = r.Priority;
        d["stage"] = r.Stage;
        d["status"] = r.Status;
        d["tech_owner"] = r.TechOwner;
        d["tech_owner_ids"] = Ids(r.TechOwnerIds);
        d["system"] = r.System;
        d["system_ids"] = Ids(r.SystemIds);
        if (r.Kind == TrackerKind.BusinessRequest) d["regulator"] = r.Regulator;
        d["requester"] = r.Requester;
        d["rm"] = r.Rm;
        d["last_comment"] = r.LastComment;
        d["last_comment_at"] = LegacyFormat.DateTime(r.LastCommentAt);
        return JsonSerializer.Serialize(d, JsonOptions);
    }

    /// <summary>Serializes a raw table row (<c>SELECT *</c>) – used for delete and import history.</summary>
    public static string FromRawRow(IReadOnlyDictionary<string, object?> row) => JsonSerializer.Serialize(row, JsonOptions);

    public static string Serialize(object value) => JsonSerializer.Serialize(value, JsonOptions);
}

/// <summary>
/// Computes field-level changes between consecutive history snapshots, exactly like the audit trail of the original
/// details drawer (renderHistoryList / diffSnapshots).
/// </summary>
public static class HistoryDiffer
{
    private static readonly HashSet<string> Exclude = new() { "created_at", "updated_at", "last_comment" };

    public static readonly IReadOnlyDictionary<string, string> ChangeLabels = new Dictionary<string, string>
    {
        ["create"] = "Created",
        ["update"] = "Updated",
        ["bulk-update"] = "Bulk Updated",
        ["delete"] = "Deleted",
    };

    /// <param name="entries">Newest first (as returned by the repository).</param>
    /// <param name="columns">Register columns of the tracker (the fields that are compared).</param>
    /// <param name="formatDateTime">Formats a UTC <c>YYYY-MM-DD HH:MM:SS</c> string for display.</param>
    public static List<HistoryEntryView> Build(IReadOnlyList<HistoryRow> entries, IReadOnlyList<TrackerColumn> columns, Func<string, string> formatDateTime)
    {
        var result = new List<HistoryEntryView>();
        for (var i = 0; i < entries.Count; i++)
        {
            var h = entries[i];
            var snapshot = Parse(h.SnapshotJson);
            var older = i + 1 < entries.Count ? entries[i + 1] : null;
            var prev = older is null ? null : Parse(older.SnapshotJson);
            // No older entry (first recorded state) or a delete – nothing meaningful to diff.
            var changes = snapshot is not null && older is not null && h.ChangeType != "delete"
                ? Diff(prev, snapshot, columns, formatDateTime)
                : new List<HistoryChange>();
            result.Add(new HistoryEntryView(h.Id, h.ChangedAt, h.ChangedBy, h.ChangeType,
                ChangeLabels.TryGetValue(h.ChangeType, out var label) ? label : h.ChangeType, changes));
        }
        return result;
    }

    private static Dictionary<string, JsonElement>? Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>JavaScript <c>String(value)</c> of a JSON value (null/undefined → empty).</summary>
    private static string? Raw(Dictionary<string, JsonElement>? snap, string key)
    {
        if (snap is null || !snap.TryGetValue(key, out var el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.String => el.GetString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => el.GetRawText(),
        };
    }

    private static List<HistoryChange> Diff(Dictionary<string, JsonElement>? prev, Dictionary<string, JsonElement> curr, IReadOnlyList<TrackerColumn> columns, Func<string, string> formatDateTime)
    {
        var changes = new List<HistoryChange>();
        foreach (var col in columns)
        {
            if (Exclude.Contains(col.Key)) continue;
            var before = Raw(prev, col.Key);
            var after = Raw(curr, col.Key);
            if ((before ?? string.Empty) == (after ?? string.Empty)) continue;
            changes.Add(new HistoryChange(col.Label, Format(col, before, formatDateTime), Format(col, after, formatDateTime)));
        }
        return changes;
    }

    private static string Format(TrackerColumn col, string? value, Func<string, string> formatDateTime)
    {
        if (string.IsNullOrEmpty(value)) return "\u2014";
        if (col.IsBool) return value is "0" or "false" ? "No" : "Yes";
        if (col.IsDateTime) return formatDateTime(value);
        return col.MaxLen is { } max && value.Length > max ? value[..max] + "\u2026" : value;
    }
}

/// <summary>Client-side filtering and sorting of the original register, moved to the server.</summary>
public static class TrackerFilter
{
    private static readonly HashSet<string> FlagKeys = new() { "newgen_related", "estimated", "ba_required", "brd_assigned", "regulatory" };

    public static List<TrackerRecord> Apply(IEnumerable<TrackerRecord> rows, TrackerQuery q)
    {
        var view = (q.View ?? "open").ToLowerInvariant();
        if (view != "all")
        {
            var closed = view == "closed";
            rows = rows.Where(r => Domain.Constants.TrackerRules.IsDoneStatus(r.Status) == closed);
        }
        var search = (q.Search ?? string.Empty).Trim().ToLowerInvariant();
        if (search.Length > 0)
        {
            rows = rows.Where(r => new[] { r.RefNo, r.Title, r.Description, r.Cr }.Any(f => (f ?? string.Empty).ToLowerInvariant().Contains(search)));
        }
        if (q.StageIds.Count > 0) rows = rows.Where(r => r.StageId is { } v && q.StageIds.Contains(v));
        if (q.StatusIds.Count > 0) rows = rows.Where(r => r.StatusId is { } v && q.StatusIds.Contains(v));
        if (q.PriorityIds.Count > 0) rows = rows.Where(r => r.PriorityId is { } v && q.PriorityIds.Contains(v));
        if (q.BusinessUnitIds.Count > 0) rows = rows.Where(r => r.BusinessUnitId is { } v && q.BusinessUnitIds.Contains(v));
        if (q.BusinessCategoryIds.Count > 0) rows = rows.Where(r => r.BusinessCategoryId is { } v && q.BusinessCategoryIds.Contains(v));
        if (q.RequesterIds.Count > 0) rows = rows.Where(r => r.RequesterId is { } v && q.RequesterIds.Contains(v));
        if (q.TechOwnerIds.Count > 0) rows = rows.Where(r => r.TechOwnerIds.Any(q.TechOwnerIds.Contains));
        if (q.RmIds.Count > 0) rows = rows.Where(r => r.RmId is { } v && q.RmIds.Contains(v));
        if (q.SystemIds.Count > 0) rows = rows.Where(r => r.SystemIds.Any(q.SystemIds.Contains));
        if (q.SlaFlags.Count > 0) rows = rows.Where(r => r.Sla is not null && q.SlaFlags.Contains(r.Sla.SlaFlag));
        if (q.Stale) rows = rows.Where(r => r.IsStale);
        foreach (var flag in q.Flags.Where(FlagKeys.Contains))
        {
            rows = rows.Where(r => flag switch
            {
                "newgen_related" => r.NewgenRelated,
                "estimated" => r.Estimated,
                "ba_required" => r.BaRequired,
                "brd_assigned" => r.BrdAssigned,
                _ => r.Regulatory,
            });
        }
        return rows.ToList();
    }

    /// <summary>
    /// Sorts like the original <c>applySorting()</c>: empty values always last, booleans and numbers numerically,
    /// everything else as lower-case strings (ordinal, like JavaScript's &lt; on strings).
    /// </summary>
    public static List<TrackerRecord> Sort(List<TrackerRecord> rows, string? key, string? dir, IReadOnlyList<TrackerColumn> columns)
    {
        if (string.IsNullOrEmpty(key)) return rows;
        var col = columns.FirstOrDefault(c => c.Key == key);
        if (col is null) return rows;
        var desc = string.Equals(dir, "desc", StringComparison.OrdinalIgnoreCase);

        int Compare(TrackerRecord a, TrackerRecord b)
        {
            var av = TrackerColumns.Value(a, key);
            var bv = TrackerColumns.Value(b, key);
            var aEmpty = av is null || (av is string sa && sa.Length == 0);
            var bEmpty = bv is null || (bv is string sb && sb.Length == 0);
            if (aEmpty && !bEmpty) return 1;
            if (bEmpty && !aEmpty) return -1;
            int cmp;
            if (col.IsBool) cmp = (av is true ? 1 : 0) - (bv is true ? 1 : 0);
            else if (col.IsNum) cmp = ToDouble(av).CompareTo(ToDouble(bv));
            else cmp = string.CompareOrdinal((av?.ToString() ?? string.Empty).ToLowerInvariant(), (bv?.ToString() ?? string.Empty).ToLowerInvariant());
            cmp = Math.Sign(cmp);
            return desc ? -cmp : cmp;
        }

        // List.Sort is not stable; order by a comparer through LINQ, which is.
        return rows.OrderBy(r => r, Comparer<TrackerRecord>.Create(Compare)).ToList();
    }

    private static double ToDouble(object? v) => v switch
    {
        null => 0,
        int i => i,
        double d => d,
        _ => double.TryParse(v.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x) ? x : 0,
    };
}
