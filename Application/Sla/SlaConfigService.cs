using ItrmTracker.Application.Abstractions;
using ItrmTracker.Application.Common;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace ItrmTracker.Application.Sla;

/// <summary>Persistence of the SLA assumption tables.</summary>
public interface ISlaConfigRepository
{
    Task<SlaConfigSource> LoadSourceAsync(CancellationToken ct = default);

    /// <summary>Full replace of sla_stages/priorities/categories/statuses plus upsert of the given settings, in one transaction.</summary>
    Task ReplaceAsync(SlaConfigUpdate update, CancellationToken ct = default);

    /// <summary>Deletes every stored assumption and setting (back to built-in defaults).</summary>
    Task ResetAsync(CancellationToken ct = default);
}

/// <summary>Validated configuration to persist.</summary>
public sealed class SlaConfigUpdate
{
    public List<SlaStageDefinition> Stages { get; } = new();
    public List<SlaPriorityDefinition> Priorities { get; } = new();
    public List<SlaCategoryDefinition> Categories { get; } = new();
    public List<SlaStatusDefinition> Statuses { get; } = new();
    public Dictionary<string, string> Settings { get; } = new();
}

/// <summary>Raw configuration editor input (strings, validated with the original JavaScript number semantics).</summary>
public sealed class SlaConfigInput
{
    public List<SlaStageInput> Stages { get; set; } = new();
    public List<SlaPriorityInput> Priorities { get; set; } = new();
    public List<SlaCategoryInput> Categories { get; set; } = new();
    public List<SlaStatusInput> Statuses { get; set; } = new();
    public List<string> WeekendDays { get; set; } = new();
    public string? AtRiskThresholdPct { get; set; }
    public string? BreachThresholdPct { get; set; }
    public string? PriorityFactorMode { get; set; }
    public string? EnforceStageOrder { get; set; }
    public string? AutoStartNextStage { get; set; }
}

public sealed class SlaStageInput
{
    public string? Name { get; set; }
    public string? StageOrder { get; set; }
    public bool IsPipeline { get; set; }
    public string? BrDays { get; set; }
    public string? IncidentDays { get; set; }
    public string? EffortDays { get; set; }
}

public sealed class SlaPriorityInput
{
    public string? Name { get; set; }
    public string? Factor { get; set; }
    public string? ResponseHours { get; set; }
    public string? UpdateDays { get; set; }
}

public sealed class SlaCategoryInput
{
    public string? Name { get; set; }
    public string? Factor { get; set; }
}

public sealed class SlaStatusInput
{
    public string? Name { get; set; }
    public string? State { get; set; }
}

/// <summary>Reads (cached) and edits the SLA configuration.</summary>
public interface ISlaConfigService
{
    /// <summary>Merged configuration, cached for 15 seconds (assumptions change rarely but every list needs them).</summary>
    Task<SlaConfiguration> GetAsync(CancellationToken ct = default);

    /// <summary>Uncached load (used by the editor).</summary>
    Task<SlaConfiguration> LoadAsync(CancellationToken ct = default);

    Task<SlaConfiguration> SaveAsync(SlaConfigInput input, CancellationToken ct = default);
    Task<SlaConfiguration> ResetAsync(CancellationToken ct = default);
    void Invalidate();
}

/// <inheritdoc />
public sealed class SlaConfigService : ISlaConfigService
{
    private const string CacheKey = "itrm:sla-config";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(15);
    private static readonly string[] States = { "Active", "Paused", "Done" };

    private readonly ISlaConfigRepository _repo;
    private readonly IMemoryCache _cache;
    private readonly IAuditLogger _audit;
    private readonly ILogger<SlaConfigService> _logger;

    public SlaConfigService(ISlaConfigRepository repo, IMemoryCache cache, IAuditLogger audit, ILogger<SlaConfigService> logger)
    {
        _repo = repo;
        _cache = cache;
        _audit = audit;
        _logger = logger;
    }

    public async Task<SlaConfiguration> GetAsync(CancellationToken ct = default)
    {
        if (_cache.TryGetValue(CacheKey, out SlaConfiguration? cached) && cached is not null) return cached;
        var config = await LoadAsync(ct);
        _cache.Set(CacheKey, config, CacheDuration);
        return config;
    }

    public async Task<SlaConfiguration> LoadAsync(CancellationToken ct = default) =>
        SlaConfigurationBuilder.Build(await _repo.LoadSourceAsync(ct));

    public void Invalidate() => _cache.Remove(CacheKey);

    /// <summary>
    /// Full replace of the assumption tables (the editor always submits every row). sla_settings is the exception:
    /// only weekend_days and the engine keys that were sent are replaced.
    /// </summary>
    public async Task<SlaConfiguration> SaveAsync(SlaConfigInput input, CancellationToken ct = default)
    {
        var (errors, update) = Validate(input);
        if (errors.Count > 0) throw new ServiceException(400, errors);

        // Cross-check the thresholds against the stored value of whichever one was not sent.
        var current = await LoadAsync(ct);
        var atRisk = update.Settings.TryGetValue("at_risk_threshold_pct", out var a) ? JsMath.ToNumber(a) : current.Settings.Engine.AtRiskThresholdPct;
        var breach = update.Settings.TryGetValue("breach_threshold_pct", out var b) ? JsMath.ToNumber(b) : current.Settings.Engine.BreachThresholdPct;
        if (atRisk > breach) throw ServiceException.BadRequest("at_risk_threshold_pct must not be greater than breach_threshold_pct.");

        await _repo.ReplaceAsync(update, ct);
        Invalidate();
        _audit.Log("sla-config.save", "sla_config", null, $"{update.Stages.Count} stages, {update.Priorities.Count} priorities, {update.Categories.Count} categories, {update.Statuses.Count} statuses");
        return await LoadAsync(ct);
    }

    public async Task<SlaConfiguration> ResetAsync(CancellationToken ct = default)
    {
        await _repo.ResetAsync(ct);
        Invalidate();
        _audit.Log("sla-config.reset", "sla_config", null);
        _logger.LogWarning("SLA configuration was reset to the built-in defaults.");
        return await LoadAsync(ct);
    }

    private static double Num(string? v) => string.IsNullOrEmpty(v) ? double.NaN : JsMath.ToNumber(v);

    /// <summary>Port of <c>validate()</c> in routes/sla.js – rejects the whole payload before touching the database.</summary>
    public static (List<string> Errors, SlaConfigUpdate Clean) Validate(SlaConfigInput body)
    {
        var errors = new List<string>();
        var seen = new HashSet<string>();
        var clean = new SlaConfigUpdate();

        string? NameOf(string kind, string? raw)
        {
            var name = (raw ?? string.Empty).Trim();
            if (name.Length == 0 || name.Length > 200)
            {
                errors.Add($"{kind}: every row needs a name (max 200 characters).");
                return null;
            }
            var key = $"{kind}:{name.ToLowerInvariant()}";
            if (!seen.Add(key))
            {
                errors.Add($"{kind}: \"{name}\" appears more than once.");
                return null;
            }
            return name;
        }
        bool Check(bool cond, string msg)
        {
            if (!cond) errors.Add(msg);
            return cond;
        }
        static bool WholeIn(double v, double min, double max) => JsMath.IsInteger(v) && v >= min && v <= max;

        foreach (var r in body.Stages ?? new())
        {
            var name = NameOf("Stages", r.Name);
            if (name is null) continue;
            double order = Num(r.StageOrder), br = Num(r.BrDays), inc = Num(r.IncidentDays), effort = Num(r.EffortDays);
            if (Check(WholeIn(order, 0, 99), $"Stage \"{name}\": order must be a whole number 0-99.")
                && Check(WholeIn(br, 0, 365), $"Stage \"{name}\": request days must be a whole number 0-365.")
                && Check(WholeIn(inc, 0, 365), $"Stage \"{name}\": incident days must be a whole number 0-365.")
                && Check(effort >= 0 && effort <= 999, $"Stage \"{name}\": effort must be 0-999."))
            {
                clean.Stages.Add(new SlaStageDefinition(name, (int)order, r.IsPipeline, br, inc, effort));
            }
        }
        foreach (var r in body.Priorities ?? new())
        {
            var name = NameOf("Priorities", r.Name);
            if (name is null) continue;
            double factor = Num(r.Factor), resp = Num(r.ResponseHours), upd = Num(r.UpdateDays);
            if (Check(factor > 0 && factor <= 10, $"Priority \"{name}\": factor must be above 0 and at most 10.")
                && Check(resp >= 0 && resp <= 999, $"Priority \"{name}\": response hours must be 0-999.")
                && Check(WholeIn(upd, 0, 365), $"Priority \"{name}\": update cadence must be a whole number of days 0-365."))
            {
                clean.Priorities.Add(new SlaPriorityDefinition(name, factor, resp, upd));
            }
        }
        foreach (var r in body.Categories ?? new())
        {
            var name = NameOf("Categories", r.Name);
            if (name is null) continue;
            var factor = Num(r.Factor);
            if (Check(factor > 0 && factor <= 10, $"Category \"{name}\": factor must be above 0 and at most 10.")) clean.Categories.Add(new SlaCategoryDefinition(name, factor));
        }
        foreach (var r in body.Statuses ?? new())
        {
            var name = NameOf("Statuses", r.Name);
            if (name is null) continue;
            if (Check(r.State is not null && States.Contains(r.State), $"Status \"{name}\": state must be Active, Paused or Done.")) clean.Statuses.Add(new SlaStatusDefinition(name, r.State!));
        }

        var weekend = (body.WeekendDays ?? new()).Select(JsMath.ToNumber).Distinct().ToList();
        if (Check(weekend.All(d => WholeIn(d, 0, 6)) && weekend.Count < 7, "Weekend days must be day numbers 0-6, and not every day."))
        {
            clean.Settings["weekend_days"] = string.Join(",", weekend.Select(d => JsMath.ToJsString(d)));
        }

        // Engine settings are optional: only the keys that were sent are validated and written.
        var engineInput = new Dictionary<string, string?>
        {
            ["at_risk_threshold_pct"] = body.AtRiskThresholdPct,
            ["breach_threshold_pct"] = body.BreachThresholdPct,
            ["priority_factor_mode"] = body.PriorityFactorMode,
            ["enforce_stage_order"] = body.EnforceStageOrder,
            ["auto_start_next_stage"] = body.AutoStartNextStage,
        };
        var engine = SlaEngine.ValidateEngineSettings(engineInput);
        errors.AddRange(engine.Errors);
        foreach (var kv in engine.Clean) clean.Settings[kv.Key] = kv.Value;
        return (errors, clean);
    }
}
