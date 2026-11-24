using ItrmTracker.Application.Common;

namespace ItrmTracker.Application.Sla;

/// <summary>A stage's SLA assumptions (dbo.sla_stages row or built-in default).</summary>
public sealed record SlaStageDefinition(string Name, int StageOrder, bool IsPipeline, double BrDays, double IncidentDays, double EffortDays);

/// <summary>A priority's SLA assumptions.</summary>
public sealed record SlaPriorityDefinition(string Name, double Factor, double ResponseHours, double UpdateDays);

/// <summary>A business category's SLA factor.</summary>
public sealed record SlaCategoryDefinition(string Name, double Factor);

/// <summary>A status mapped to an SLA clock state (Active / Paused / Done).</summary>
public sealed record SlaStatusDefinition(string Name, string State);

/// <summary>Weekend + engine settings.</summary>
public sealed record SlaSettingsModel(IReadOnlyList<int> WeekendDays, EngineSettings Engine);

/// <summary>
/// The complete, merged SLA configuration: every lookup value (in lookup order) followed by any stored row whose
/// lookup no longer exists, with built-in defaults for values without a stored row. Port of <c>loadConfig()</c>.
/// </summary>
public sealed record SlaConfiguration(
    IReadOnlyList<SlaStageDefinition> Stages,
    IReadOnlyList<SlaPriorityDefinition> Priorities,
    IReadOnlyList<SlaCategoryDefinition> Categories,
    IReadOnlyList<SlaStatusDefinition> Statuses,
    SlaSettingsModel Settings);

/// <summary>Raw rows needed to build a <see cref="SlaConfiguration"/> (returned by the repository).</summary>
public sealed class SlaConfigSource
{
    public List<string> StageNames { get; init; } = new();
    public List<string> PriorityNames { get; init; } = new();
    public List<string> CategoryNames { get; init; } = new();
    public List<string> StatusNames { get; init; } = new();
    public List<SlaStageDefinition> StoredStages { get; init; } = new();
    public List<SlaPriorityDefinition> StoredPriorities { get; init; } = new();
    public List<SlaCategoryDefinition> StoredCategories { get; init; } = new();
    public List<SlaStatusDefinition> StoredStatuses { get; init; } = new();
    public Dictionary<string, string> Settings { get; init; } = new();
}

/// <summary>Built-in defaults of the original <c>lib/sla.js</c>.</summary>
public static class SlaDefaults
{
    public static readonly IReadOnlyDictionary<string, SlaStageDefinition> Stages = new Dictionary<string, SlaStageDefinition>
    {
        ["analysis"] = new("analysis", 1, true, 5, 2, 2),
        ["ba"] = new("ba", 2, true, 10, 0, 5),
        ["development"] = new("development", 3, true, 15, 5, 8),
        ["qa"] = new("qa", 4, true, 7, 3, 4),
        ["uat"] = new("uat", 5, true, 5, 2, 2),
        ["cab"] = new("cab", 6, true, 3, 2, 0.5),
        ["deployment"] = new("deployment", 7, true, 3, 2, 1),
        // Not part of the standard pipeline: a vendor stage replacing Development, a triage stage, and "past the end".
        ["newgen"] = new("newgen", 3, false, 30, 30, 0),
        ["dmo"] = new("dmo", 0, false, 2, 2, 0),
        ["closed"] = new("closed", 8, false, 0, 0, 0),
    };

    /// <summary>Unknown stage names (e.g. "Draft") restart the pipeline from Analysis with no extra time.</summary>
    public static readonly SlaStageDefinition UnknownStage = new(string.Empty, 0, false, 0, 0, 0);

    public static readonly IReadOnlyDictionary<string, SlaPriorityDefinition> Priorities = new Dictionary<string, SlaPriorityDefinition>
    {
        ["critical"] = new("critical", 0.4, 1, 1),
        ["critcal"] = new("critcal", 0.4, 1, 1),
        ["high"] = new("high", 0.7, 4, 2),
        ["medium"] = new("medium", 1, 8, 7),
        ["low"] = new("low", 1.5, 16, 14),
        ["very low"] = new("very low", 2, 24, 14),
    };

    public static readonly SlaPriorityDefinition UnknownPriority = new(string.Empty, 1, 8, 7);

    public static readonly IReadOnlyDictionary<string, double> Categories = new Dictionary<string, double>
    {
        ["normal"] = 1,
        ["regulatory"] = 0.8,
        ["fast track"] = 0.6,
        ["strategic projects"] = 1.5,
    };

    private static readonly HashSet<string> DoneStatuses = new() { "completed", "compeleted", "closed", "cancelled", "canceled" };
    private static readonly HashSet<string> PausedStatuses = new() { "on hold" };

    /// <summary>Default clock state of a status without a stored row.</summary>
    public static string DefaultState(string? name)
    {
        var n = LegacyFormat.Norm(name);
        return DoneStatuses.Contains(n) ? "Done" : PausedStatuses.Contains(n) ? "Paused" : "Active";
    }

    public static string[] Flags => Domain.Constants.SlaFlags.All.ToArray();
}

/// <summary>Builds the merged <see cref="SlaConfiguration"/> from raw rows (port of <c>loadConfig()</c>).</summary>
public static class SlaConfigurationBuilder
{
    private static string Norm(string? s) => LegacyFormat.Norm(s);

    /// <summary>Lookup values first (in lookup order), then any stored rows whose lookup no longer exists.</summary>
    private static List<string> MergeNames(IEnumerable<string> lookups, IEnumerable<string> stored)
    {
        var list = lookups.ToList();
        var seen = new HashSet<string>(list.Select(Norm));
        list.AddRange(stored.Where(n => !seen.Contains(Norm(n))));
        return list;
    }

    public static SlaConfiguration Build(SlaConfigSource src)
    {
        var st = ByName(src.StoredStages, s => s.Name);
        var pr = ByName(src.StoredPriorities, s => s.Name);
        var ca = ByName(src.StoredCategories, s => s.Name);
        var ss = ByName(src.StoredStatuses, s => s.Name);

        var stages = MergeNames(src.StageNames, src.StoredStages.Select(s => s.Name)).Select(name =>
        {
            var row = st.TryGetValue(Norm(name), out var stored) ? stored
                : SlaDefaults.Stages.TryGetValue(Norm(name), out var def) ? def : SlaDefaults.UnknownStage;
            return row with { Name = name };
        }).ToList();

        var priorities = MergeNames(src.PriorityNames, src.StoredPriorities.Select(s => s.Name)).Select(name =>
        {
            var row = pr.TryGetValue(Norm(name), out var stored) ? stored
                : SlaDefaults.Priorities.TryGetValue(Norm(name), out var def) ? def : SlaDefaults.UnknownPriority;
            return row with { Name = name };
        }).ToList();

        var categories = MergeNames(src.CategoryNames, src.StoredCategories.Select(s => s.Name)).Select(name =>
            ca.TryGetValue(Norm(name), out var stored)
                ? new SlaCategoryDefinition(name, stored.Factor)
                : new SlaCategoryDefinition(name, SlaDefaults.Categories.TryGetValue(Norm(name), out var f) ? f : 1)).ToList();

        var statuses = MergeNames(src.StatusNames, src.StoredStatuses.Select(s => s.Name)).Select(name =>
            new SlaStatusDefinition(name, ss.TryGetValue(Norm(name), out var stored) ? stored.State : SlaDefaults.DefaultState(name))).ToList();

        IReadOnlyList<int> weekend = src.Settings.TryGetValue("weekend_days", out var w)
            ? w.Split(',').Where(s => s.Length > 0)
                .Select(JsMath.ToNumber)
                .Where(n => JsMath.IsInteger(n) && n >= 0 && n <= 6)
                .Select(n => (int)n).ToList()
            : SlaEngine.DefaultWeekend;

        var engine = SlaEngine.ParseEngineSettings(src.Settings.ToDictionary(k => k.Key, v => (string?)v.Value));
        return new SlaConfiguration(stages, priorities, categories, statuses, new SlaSettingsModel(weekend, engine));
    }

    private static Dictionary<string, T> ByName<T>(IEnumerable<T> rows, Func<T, string> name)
    {
        var map = new Dictionary<string, T>();
        foreach (var r in rows) map[Norm(name(r))] = r; // later rows win, like new Map(rows)
        return map;
    }
}
