using ItrmTracker.Application.Common;
using ItrmTracker.Domain.Constants;

namespace ItrmTracker.Application.Sla;

/// <summary>Per-stage contribution to the legacy SLA target.</summary>
public sealed record SlaBreakdownItem(string Stage, double Days);

/// <summary>Legacy SLA fields added to every request/incident row (the <c>sla_*</c> columns of the original API).</summary>
public sealed record LegacySlaResult(
    string SlaState,
    double SlaRemainingDays,
    double SlaEffortDays,
    string? SlaTargetDate,
    int? SlaGapDays,
    string SlaFlag,
    IReadOnlyList<SlaBreakdownItem> SlaBreakdown);

/// <summary>The fields the legacy SLA calculation reads from a record.</summary>
public interface ILegacySlaSubject
{
    string? Status { get; }
    string? Priority { get; }
    string? BusinessCategory { get; }
    string? Stage { get; }
    DateOnly? DeploymentPlannedEndDate { get; }
}

/// <summary>Which tracker a record belongs to (affects stage day columns and category factors).</summary>
public enum TrackerKind
{
    BusinessRequest,
    Incident,
}

/// <summary>
/// Legacy SLA calculation: stage durations × priority factor × category factor, counted from a record's current stage
/// through Deployment, in working days. Port of <c>computeSla()</c> in <c>lib/sla.js</c>.
/// </summary>
public sealed class LegacySlaCalculator
{
    private readonly Dictionary<string, SlaStageDefinition> _stages;
    private readonly Dictionary<string, SlaPriorityDefinition> _priorities;
    private readonly Dictionary<string, SlaCategoryDefinition> _categories;
    private readonly Dictionary<string, SlaStatusDefinition> _statuses;
    private readonly List<SlaStageDefinition> _pipeline;
    private readonly IReadOnlySet<int> _weekend;

    public LegacySlaCalculator(SlaConfiguration config)
    {
        _stages = Map(config.Stages, s => s.Name);
        _priorities = Map(config.Priorities, s => s.Name);
        _categories = Map(config.Categories, s => s.Name);
        _statuses = Map(config.Statuses, s => s.Name);
        // OrderBy is stable, like Array#sort.
        _pipeline = config.Stages.Where(s => s.IsPipeline).OrderBy(s => s.StageOrder).ToList();
        _weekend = new HashSet<int>(config.Settings.WeekendDays);
    }

    private static Dictionary<string, T> Map<T>(IEnumerable<T> list, Func<T, string> name)
    {
        var map = new Dictionary<string, T>();
        foreach (var x in list) map[LegacyFormat.Norm(name(x))] = x;
        return map;
    }

    private SlaStageDefinition StageOf(string? name)
    {
        var n = LegacyFormat.Norm(name);
        return _stages.TryGetValue(n, out var s) ? s : SlaDefaults.Stages.TryGetValue(n, out var d) ? d : SlaDefaults.UnknownStage;
    }

    private SlaPriorityDefinition PriorityOf(string? name) =>
        _priorities.TryGetValue(LegacyFormat.Norm(name), out var p) ? p : SlaDefaults.UnknownPriority;

    private string StateOf(string? name) =>
        _statuses.TryGetValue(LegacyFormat.Norm(name), out var s) ? s.State : SlaDefaults.DefaultState(name);

    /// <summary>Computes the <c>sla_*</c> fields for one record; <paramref name="today"/> is the business-local date.</summary>
    public LegacySlaResult Compute(ILegacySlaSubject record, TrackerKind kind, DateOnly today)
    {
        var state = StateOf(record.Status);
        if (state == "Done")
        {
            return new LegacySlaResult(state, 0, 0, null, null, SlaFlags.Done, Array.Empty<SlaBreakdownItem>());
        }

        var isIncident = kind == TrackerKind.Incident;
        double categoryFactor = 1;
        if (!isIncident && _categories.TryGetValue(LegacyFormat.Norm(record.BusinessCategory), out var cat)) categoryFactor = cat.Factor;
        var factor = PriorityOf(record.Priority).Factor * (isIncident ? 1 : categoryFactor);
        double Scale(double baseDays) => baseDays > 0 ? Math.Max(1, Math.Ceiling(JsMath.Round(baseDays * factor * 1e6) / 1e6)) : 0;
        double DaysOf(SlaStageDefinition s) => isIncident ? s.IncidentDays : s.BrDays;

        var cur = StageOf(record.Stage);
        var breakdown = new List<SlaBreakdownItem>();
        double total = 0;
        double effort = 0;
        void Add(SlaStageDefinition stage, string name)
        {
            var days = Scale(DaysOf(stage));
            if (days <= 0) return;
            breakdown.Add(new SlaBreakdownItem(name, days));
            total += days;
            effort += stage.EffortDays;
        }

        if (!cur.IsPipeline && DaysOf(cur) > 0) Add(cur, record.Stage ?? string.Empty);
        foreach (var s in _pipeline)
        {
            if (cur.IsPipeline ? s.StageOrder >= cur.StageOrder : s.StageOrder > cur.StageOrder) Add(s, s.Name);
        }

        var target = AddWorkingDays(today, total);
        var planned = record.DeploymentPlannedEndDate;
        int? gap = planned is { } p ? target.DayNumber - p.DayNumber : null;

        string flag;
        if (state == "Paused") flag = SlaFlags.Paused;
        else if (planned is null) flag = SlaFlags.NoPlan;
        else if (planned.Value < today) flag = SlaFlags.Passed;
        else if (gap > 0) flag = SlaFlags.AtRisk;
        else flag = SlaFlags.Ok;

        return new LegacySlaResult(state, total, effort, LegacyFormat.Date(target), gap, flag, breakdown);
    }

    private DateOnly AddWorkingDays(DateOnly start, double days)
    {
        // Guard: the original looped forever with a 7-day weekend (prevented by settings validation anyway).
        var weekend = _weekend.Count >= 7 ? new HashSet<int>() : _weekend;
        var day = start;
        var left = days;
        while (left > 0)
        {
            day = day.AddDays(1);
            if (!weekend.Contains((int)day.DayOfWeek)) left--;
        }
        return day;
    }
}
