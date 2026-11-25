using System.Globalization;
using ItrmTracker.Application.Abstractions;
using ItrmTracker.Application.Common;
using ItrmTracker.Application.Tracker;
using ItrmTracker.Domain.Constants;

namespace ItrmTracker.Web.Infrastructure;

/// <summary>Display formatting shared by views, copy, e-mail and history (server side).</summary>
public sealed class DisplayFormatter
{
    private readonly IClock _clock;

    public DisplayFormatter(IClock clock) => _clock = clock;

    /// <summary>UTC timestamp → business-local "24 Nov 2026, 09:30" (the original used the browser's locale, medium/short).</summary>
    public string DateTime(DateTime? utc)
    {
        if (utc is null) return string.Empty;
        var local = TimeZoneInfo.ConvertTimeFromUtc(System.DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc), _clock.BusinessTimeZone);
        return local.ToString("dd MMM yyyy, HH:mm", CultureInfo.CurrentCulture);
    }

    /// <summary>Formats a legacy "YYYY-MM-DD HH:MM:SS" (UTC) string.</summary>
    public string DateTimeString(string value) =>
        System.DateTime.TryParseExact(value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
            ? DateTime(d)
            : value;

    /// <summary>ISO 8601 UTC value for &lt;time datetime&gt; (converted to the viewer's local time by site.js).</summary>
    public static string Iso(DateTime? utc) => utc is null ? string.Empty : System.DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    /// <summary>Cell text exactly like getTableCopyCellValue (copy / e-mail): Yes/No, local date-times, truncation, Comments = last comment.</summary>
    public string CopyCell(TrackerRecord r, TrackerColumn col)
    {
        if (col.IsBool) return TrackerColumns.Value(r, col.Key) is true ? "Yes" : "No";
        if (col.IsDateTime) return DateTime(col.Key == "created_at" ? r.CreatedAt : r.UpdatedAt);
        var key = col.Key == "all_comments" ? "last_comment" : col.Key;
        var raw = TrackerColumns.Text(r, new TrackerColumn(key, col.Label));
        return col.MaxLen is { } max && raw.Length > max ? raw[..max] + "\u2026" : raw;
    }

    /// <summary>Tab-separated text of the given rows (header + rows; tabs/newlines collapsed like toClipboardSafeText).</summary>
    public string Tsv(IEnumerable<TrackerRecord> rows, IReadOnlyList<TrackerColumn> cols)
    {
        static string Safe(string? v) => System.Text.RegularExpressions.Regex.Replace(v ?? string.Empty, "[\t\r\n]+", " ").Trim();
        var lines = new List<string> { string.Join("\t", cols.Select(c => Safe(c.Label))) };
        lines.AddRange(rows.Select(r => string.Join("\t", cols.Select(c => Safe(CopyCell(r, c))))));
        return string.Join("\n", lines);
    }

    /// <summary>"N days after planned" / "N days before planned".</summary>
    public static string Gap(int? gap) => gap switch
    {
        null => "\u2014",
        > 0 => $"{gap} days after planned",
        _ => $"{Math.Abs(gap.Value)} days before planned",
    };

    /// <summary>Bootstrap badge class for a legacy SLA flag.</summary>
    public static string SlaFlagClass(string? flag) => flag switch
    {
        SlaFlags.AtRisk => "text-bg-danger",
        SlaFlags.Passed => "text-bg-warning",
        SlaFlags.NoPlan => "text-bg-secondary",
        SlaFlags.Ok => "text-bg-success",
        SlaFlags.Paused => "text-bg-info",
        SlaFlags.Done => "text-bg-light border",
        _ => "text-bg-light border",
    };

    /// <summary>Bootstrap badge class for engine health.</summary>
    public static string HealthClass(string? health) => health switch
    {
        SlaHealth.Breached => "text-bg-danger",
        SlaHealth.AtRisk => "text-bg-warning",
        SlaHealth.OnTrack => "text-bg-success",
        _ => "text-bg-light border",
    };

    /// <summary>Badge class for a stage status.</summary>
    public static string StageStatusClass(string? status) => status switch
    {
        StageStatus.InProgress => "text-bg-primary",
        StageStatus.OnHold => "text-bg-warning",
        StageStatus.Completed => "text-bg-success",
        StageStatus.Cancelled => "text-bg-secondary",
        StageStatus.NotYetEstimated => "text-bg-light border",
        _ => "text-bg-light border",
    };

    public static string Num(double? v) => v is null ? "\u2014" : JsMath.ToJsString(v.Value);

    public static string Pct(double? v) => v is null ? "\u2014" : JsMath.ToJsString(v.Value) + "%";

    public static string FollowUpLabel(int remaining) => remaining < 0 ? $"{Math.Abs(remaining)}d overdue" : remaining == 0 ? "Due today" : $"{remaining}d left";
}
