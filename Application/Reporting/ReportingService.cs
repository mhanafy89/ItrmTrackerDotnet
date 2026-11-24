using System.Globalization;
using ItrmTracker.Application.Abstractions;
using ItrmTracker.Application.Common;
using ItrmTracker.Application.Sla;
using ItrmTracker.Application.Tracker;
using ItrmTracker.Domain.Constants;

namespace ItrmTracker.Application.Reporting;

public sealed record KpiTile(int Value, string Label, string Variant);

public sealed record BreakdownItem(string Label, double Value);

public sealed record TrendPoint(string Label, int Created, int Closed);

/// <summary>Everything the Dashboard screen shows.</summary>
public sealed class DashboardData
{
    public List<KpiTile> Tiles { get; } = new();
    public List<BreakdownItem> BrByStage { get; set; } = new();
    public List<BreakdownItem> BrByStatus { get; set; } = new();
    public List<BreakdownItem> BrByUnit { get; set; } = new();
    public List<BreakdownItem> IncByStatus { get; set; } = new();
    public List<BreakdownItem> BrBySla { get; set; } = new();
    public List<BreakdownItem> IncBySla { get; set; } = new();
    public List<BreakdownItem> SlaDaysByStage { get; set; } = new();
    public List<BreakdownItem> AvgCloseByUnit { get; set; } = new();
    public List<BreakdownItem> AvgCloseByCategory { get; set; } = new();
    public List<TrendPoint> MonthlyTrend { get; set; } = new();
}

/// <summary>A record that is overdue or due soon in its current stage.</summary>
public sealed record FollowUpRow(TrackerKind Kind, TrackerRecord Record, string? RefNo, string DueLabel, string DueDate, int Remaining);

/// <summary>A record whose plan is at risk against the SLA target.</summary>
public sealed record SlaRiskRow(TrackerKind Kind, TrackerRecord Record, string? RefNo);

/// <summary>A record the current user follows.</summary>
public sealed record FollowedRow(TrackerKind Kind, TrackerRecord Record, string? RefNo);

public sealed class FollowUpData
{
    public List<FollowUpRow> Due { get; init; } = new();
    public List<SlaRiskRow> SlaRisks { get; init; } = new();
}

public interface IReportingService
{
    Task<DashboardData> GetDashboardAsync(CancellationToken ct = default);
    Task<FollowUpData> GetFollowUpsAsync(bool includeIncidents, CancellationToken ct = default);
    Task<List<FollowedRow>> GetMyFollowUpsAsync(CancellationToken ct = default);
}

/// <summary>
/// Dashboard, Follow-up and My Follow-ups. These were computed in the browser by the original app
/// (renderDashboard, computeFollowupRows, computeSlaRiskRows, ...); the formulas are preserved exactly.
/// </summary>
public sealed class ReportingService : IReportingService
{
    private static readonly IReadOnlyDictionary<string, (Func<TrackerRecord, DateOnly?> Field, string Label)> StageDueDateFields =
        new Dictionary<string, (Func<TrackerRecord, DateOnly?>, string)>
        {
            ["analysis"] = (r => r.AnalysisPlannedEndDate, "Analysis Planned End"),
            ["development"] = (r => r.DevPlannedEndDate, "Dev Planned End"),
            ["qa"] = (r => r.QaPlannedEndDate, "QA Planned End"),
            ["uat"] = (r => r.UatPlannedDate, "UAT Planned End"),
            ["cab"] = (r => r.CabDate, "CAB Date"),
            ["deployment"] = (r => r.DeploymentPlannedEndDate, "Deployment Planned End"),
        };

    private readonly ITrackerService _tracker;
    private readonly IClock _clock;

    public ReportingService(ITrackerService tracker, IClock clock)
    {
        _tracker = tracker;
        _clock = clock;
    }

    public async Task<DashboardData> GetDashboardAsync(CancellationToken ct = default)
    {
        var requests = await _tracker.ListAllAsync(TrackerKind.BusinessRequest, ct);
        var incidents = await _tracker.ListAllAsync(TrackerKind.Incident, ct);
        var openRequests = requests.Where(r => !TrackerRules.IsDoneStatus(r.Status)).ToList();
        var openIncidents = incidents.Where(r => !TrackerRules.IsDoneStatus(r.Status)).ToList();
        var regulatoryOpen = requests.Count(r => r.Regulatory && !TrackerRules.IsDoneStatus(r.Status));
        var followUp = ComputeFollowUpRows(requests, incidents, includeIncidents: true);
        var overdue = followUp.Count(f => f.Remaining < 0);
        var dueSoon = followUp.Count(f => f.Remaining >= 0);
        var all = requests.Concat(incidents).ToList();
        var slaAtRisk = all.Count(r => r.Sla?.SlaFlag == SlaFlags.AtRisk);
        var slaRebaseline = all.Count(r => r.Sla?.SlaFlag == SlaFlags.Passed);
        var staleRequests = openRequests.Count(r => r.IsStale);
        var staleIncidents = openIncidents.Count(r => r.IsStale);

        var d = new DashboardData();
        d.Tiles.Add(new KpiTile(requests.Count, "Business Requests", ""));
        d.Tiles.Add(new KpiTile(openRequests.Count, "Open Business Requests", ""));
        d.Tiles.Add(new KpiTile(incidents.Count, "Incidents", ""));
        d.Tiles.Add(new KpiTile(openIncidents.Count, "Open Incidents", ""));
        d.Tiles.Add(new KpiTile(overdue, "Overdue", overdue > 0 ? "danger" : ""));
        d.Tiles.Add(new KpiTile(dueSoon, $"Due within {TrackerRules.FollowUpThresholdDays} Days", dueSoon > 0 ? "warning" : ""));
        d.Tiles.Add(new KpiTile(slaAtRisk, "SLA At Risk", slaAtRisk > 0 ? "danger" : ""));
        d.Tiles.Add(new KpiTile(slaRebaseline, "Needs Re-baseline", slaRebaseline > 0 ? "warning" : ""));
        d.Tiles.Add(new KpiTile(regulatoryOpen, "Open Regulatory BRs", ""));
        d.Tiles.Add(new KpiTile(staleRequests, $"Stale BRs ({TrackerRules.StaleThresholdDays}+ days)", staleRequests > 0 ? "warning" : ""));
        d.Tiles.Add(new KpiTile(staleIncidents, $"Stale Incidents ({TrackerRules.StaleThresholdDays}+ days)", staleIncidents > 0 ? "warning" : ""));

        d.BrByStage = CountBy(requests, r => r.Stage);
        d.BrByStatus = CountBy(requests, r => r.Status);
        d.BrByUnit = CountBy(requests, r => r.BusinessUnit);
        d.IncByStatus = CountBy(incidents, r => r.Status);
        d.BrBySla = CountBy(openRequests, r => r.Sla?.SlaFlag);
        d.IncBySla = CountBy(openIncidents, r => r.Sla?.SlaFlag);

        var daysByStage = new List<(string Stage, double Days)>();
        foreach (var r in openRequests.Concat(openIncidents))
        {
            if (r.Sla is null || r.Sla.SlaState == "Done") continue;
            foreach (var s in r.Sla.SlaBreakdown)
            {
                var idx = daysByStage.FindIndex(x => x.Stage == s.Stage);
                if (idx < 0) daysByStage.Add((s.Stage, s.Days));
                else daysByStage[idx] = (s.Stage, daysByStage[idx].Days + s.Days);
            }
        }
        d.SlaDaysByStage = daysByStage.OrderByDescending(x => x.Days).Select(x => new BreakdownItem(x.Stage, x.Days)).ToList();
        d.AvgCloseByUnit = AvgCloseDays(all, r => r.BusinessUnit);
        d.AvgCloseByCategory = AvgCloseDays(requests, r => r.BusinessCategory);
        d.MonthlyTrend = MonthlyTrend(all);
        return d;
    }

    public async Task<FollowUpData> GetFollowUpsAsync(bool includeIncidents, CancellationToken ct = default)
    {
        var requests = await _tracker.ListAllAsync(TrackerKind.BusinessRequest, ct);
        var incidents = includeIncidents ? await _tracker.ListAllAsync(TrackerKind.Incident, ct) : new List<TrackerRecord>();

        var risks = new List<SlaRiskRow>();
        foreach (var r in requests) if (IsSlaRisk(r)) risks.Add(new SlaRiskRow(TrackerKind.BusinessRequest, r, r.RefNo));
        foreach (var r in incidents) if (IsSlaRisk(r)) risks.Add(new SlaRiskRow(TrackerKind.Incident, r, r.RefNo));

        return new FollowUpData
        {
            Due = ComputeFollowUpRows(requests, incidents, includeIncidents),
            // Largest gap first; records without a gap last (JavaScript: ?? -Infinity, stable sort).
            SlaRisks = risks.OrderByDescending(x => x.Record.Sla?.SlaGapDays is { } g ? g : double.NegativeInfinity).ToList(),
        };
    }

    public async Task<List<FollowedRow>> GetMyFollowUpsAsync(CancellationToken ct = default)
    {
        var result = new List<FollowedRow>();
        foreach (var kind in new[] { TrackerKind.BusinessRequest, TrackerKind.Incident })
        {
            var followed = await _tracker.FollowedIdsAsync(kind, ct);
            if (followed.Count == 0) continue;
            foreach (var r in await _tracker.ListAllAsync(kind, ct))
            {
                if (followed.Contains(r.Id)) result.Add(new FollowedRow(kind, r, r.RefNo));
            }
        }
        return result;
    }

    private static bool IsSlaRisk(TrackerRecord r) => r.Sla?.SlaFlag is SlaFlags.AtRisk or SlaFlags.Passed;

    /// <summary>Records whose current stage's planned end date is overdue or due within the threshold.</summary>
    private List<FollowUpRow> ComputeFollowUpRows(List<TrackerRecord> requests, List<TrackerRecord> incidents, bool includeIncidents)
    {
        var today = _clock.Today;
        var sources = new List<(TrackerKind Kind, List<TrackerRecord> Rows)> { (TrackerKind.BusinessRequest, requests) };
        if (includeIncidents) sources.Add((TrackerKind.Incident, incidents));

        var rows = new List<FollowUpRow>();
        foreach (var (kind, list) in sources)
        {
            foreach (var r in list)
            {
                if (!StageDueDateFields.TryGetValue(LegacyFormat.Norm(r.Stage), out var mapping)) continue;
                var due = mapping.Field(r);
                if (due is null) continue;
                if (TrackerRules.IsDoneStatus(r.Status)) continue;
                var remaining = due.Value.DayNumber - today.DayNumber;
                if (remaining > TrackerRules.FollowUpThresholdDays) continue;
                rows.Add(new FollowUpRow(kind, r, r.RefNo, mapping.Label, LegacyFormat.Date(due)!, remaining));
            }
        }
        return rows.OrderBy(f => f.Remaining).ToList();
    }

    private static List<BreakdownItem> CountBy(IEnumerable<TrackerRecord> rows, Func<TrackerRecord, string?> key)
    {
        var order = new List<string>();
        var counts = new Dictionary<string, int>();
        foreach (var r in rows)
        {
            var label = string.IsNullOrWhiteSpace(key(r)) ? "(none)" : key(r)!.Trim();
            if (!counts.ContainsKey(label)) { counts[label] = 0; order.Add(label); }
            counts[label]++;
        }
        return order.Select(l => new BreakdownItem(l, counts[l])).OrderByDescending(x => x.Value).ToList();
    }

    private static DateTime? StartOf(DateOnly? d) => d?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

    /// <summary>Average days from submission (or creation) to close, grouped by a field; one decimal.</summary>
    private static List<BreakdownItem> AvgCloseDays(IEnumerable<TrackerRecord> rows, Func<TrackerRecord, string?> group)
    {
        var order = new List<string>();
        var sums = new Dictionary<string, (double Total, int Count)>();
        foreach (var r in rows)
        {
            if (!TrackerRules.IsDoneStatus(r.Status)) continue;
            var start = StartOf(r.SubmissionDate) ?? r.CreatedAt;
            var end = StartOf(r.ClosedDate) ?? r.UpdatedAt;
            var days = JsMath.Round((end - start).TotalDays);
            if (days < 0) continue;
            var label = string.IsNullOrWhiteSpace(group(r)) ? "(none)" : group(r)!.Trim();
            if (!sums.TryGetValue(label, out var cur)) { cur = (0, 0); order.Add(label); }
            sums[label] = (cur.Total + days, cur.Count + 1);
        }
        return order
            .Select(l => new BreakdownItem(l, JsMath.Round(sums[l].Total / sums[l].Count * 10) / 10))
            .OrderByDescending(x => x.Value)
            .ToList();
    }

    /// <summary>Records created vs. closed per month for the last six months (UTC months).</summary>
    private List<TrendPoint> MonthlyTrend(IEnumerable<TrackerRecord> rows, int months = 6)
    {
        var now = _clock.UtcNow;
        var keys = new List<DateTime>();
        for (var i = months - 1; i >= 0; i--) keys.Add(new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-i));
        var created = keys.ToDictionary(k => k, _ => 0);
        var closed = keys.ToDictionary(k => k, _ => 0);
        static DateTime MonthOf(DateTime d) => new(d.Year, d.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        foreach (var r in rows)
        {
            var ck = MonthOf(r.CreatedAt);
            if (created.ContainsKey(ck)) created[ck]++;
            if (TrackerRules.IsDoneStatus(r.Status))
            {
                var closedKey = MonthOf(StartOf(r.ClosedDate) ?? r.UpdatedAt);
                if (closed.ContainsKey(closedKey)) closed[closedKey]++;
            }
        }
        return keys.Select(k => new TrendPoint(k.ToString("MMM yyyy", CultureInfo.CurrentCulture), created[k], closed[k])).ToList();
    }
}
