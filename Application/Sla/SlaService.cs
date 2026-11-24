using ItrmTracker.Application.Abstractions;
using ItrmTracker.Application.Common;
using ItrmTracker.Domain.Constants;
using ItrmTracker.Domain.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ItrmTracker.Application.Sla;

/// <summary>Options bound from the <c>Sla</c> configuration section.</summary>
public sealed class SlaOptions
{
    public const string SectionName = "Sla";

    /// <summary>Reconcile the stage engine after the request form saves (legacy SLA_ENGINE_SYNC, default true).</summary>
    public bool EngineSyncEnabled { get; set; } = true;

    /// <summary>Create missing per-stage tracking at startup (legacy SLA_BACKFILL_ON_START, default true).</summary>
    public bool BackfillOnStartup { get; set; } = true;

    /// <summary>Windows or IANA time zone id that defines a "calendar day"; empty = the server's local zone (original behaviour).</summary>
    public string? BusinessTimeZoneId { get; set; }
}

/// <summary>Rule failure from the SLA engine (status code + messages).</summary>
public sealed class SlaException : ServiceException
{
    public SlaException(int statusCode, IEnumerable<string> errors) : base(statusCode, errors) { }
}

/// <summary>Live SLA status of one request: current-stage headline, request roll-up and every stage.</summary>
public sealed class RequestSlaView
{
    public int RequestId { get; init; }
    public string? Priority { get; init; }
    public double PriorityFactor { get; init; }
    public string PriorityFactorMode { get; init; } = "multiplier";
    public string CurrentStage { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public double? PlannedEffort { get; init; }
    public double ActualConsumed { get; init; }
    public double? Remaining { get; init; }
    public double HoldDays { get; init; }
    public string? ExpectedCompletionDate { get; init; }
    public string? SlaState { get; init; }
    public double ExpectedSlaDays { get; init; }
    public double? UtilizationPct { get; init; }
    public RequestSlaSummary Request { get; init; } = new();
    public IReadOnlyList<StageMetrics> Stages { get; init; } = Array.Empty<StageMetrics>();
}

public sealed record TransitionInfo(string Stage, string From, string To, string Action, bool Changed);

public sealed record StageStatusChangeResult(TransitionInfo Transition, RequestSlaView Sla);

public sealed record SlaHistoryView(
    IReadOnlyList<RequestStageStatusHistory> StatusHistory,
    IReadOnlyList<RequestStageHistory> StageHistory,
    IReadOnlyList<PauseRecord> Pauses);

public sealed class SlaDashboardRow
{
    public int RequestId { get; init; }
    public string? BrNo { get; init; }
    public string Title { get; init; } = string.Empty;
    public string? Priority { get; init; }
    public string CurrentStage { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public double TotalPlannedDays { get; init; }
    public double TotalExpectedDays { get; init; }
    public double TotalConsumedDays { get; init; }
    public double TotalRemainingDays { get; init; }
    public double TotalHoldDays { get; init; }
    public double VarianceDays { get; init; }
    public double? UtilizationPct { get; init; }
    public string? SlaHealth { get; init; }
    public int UnestimatedStages { get; init; }
    public bool IsFinished { get; init; }
    public string? ExpectedCompletionDate { get; init; }
}

public sealed class SlaDashboardStage
{
    public string Stage { get; init; } = string.Empty;
    public int Requests { get; init; }
    public double PlannedEffort { get; init; }
    public double ActualEffort { get; init; }
    public double VarianceDays { get; init; }
    public double HoldDays { get; init; }
    public double? UtilizationPct { get; init; }
    public string? SlaState { get; init; }
}

public sealed record SlaDashboardThresholds(double AtRiskPct, double BreachPct);

public sealed record SlaDashboardTotals(int Requests, double PlannedDays, double ConsumedDays, double HoldDays, IReadOnlyDictionary<string, int> Health);

public sealed record SlaDashboard(
    SlaDashboardThresholds Thresholds,
    SlaDashboardTotals Totals,
    IReadOnlyList<SlaDashboardStage> Stages,
    IReadOnlyList<SlaDashboardRow> Requests);

/// <summary>Stage-driven SLA operations.</summary>
public interface ISlaService
{
    Task<StageStatusChangeResult> UpdateStageStatusAsync(int requestId, StageStatusInput input, string? user, CancellationToken ct = default);
    Task<RequestSlaView> GetRequestSlaAsync(int requestId, CancellationToken ct = default);
    Task<SlaHistoryView> GetHistoryAsync(int requestId, CancellationToken ct = default);
    Task<SlaDashboard> GetDashboardAsync(string? priority, string? stage, string? health, CancellationToken ct = default);
    Task<(int Total, int Created)> BackfillMissingAsync(string user = "sla-migration", CancellationToken ct = default);
    Task<bool> SyncFromLegacyAsync(int requestId, string? user, CancellationToken ct = default);

    /// <summary>Called after the request form saves; never throws (the request save already succeeded).</summary>
    Task SyncLegacyRequestAsync(int requestId, string? user, CancellationToken ct = default);
}

/// <summary>
/// Orchestrates the pure <see cref="SlaEngine"/> and the <see cref="ISlaRepository"/>. Port of <c>lib/sla-service.js</c>.
/// Every write runs in one transaction that first locks the request row (UPDLOCK), so two people changing the same
/// request serialize instead of corrupting the open work/hold segments.
/// </summary>
public sealed class SlaService : ISlaService
{
    private readonly ISlaRepository _repo;
    private readonly ISlaConfigService _config;
    private readonly IClock _clock;
    private readonly IAuditLogger _audit;
    private readonly ILogger<SlaService> _logger;
    private readonly SlaOptions _options;

    public SlaService(ISlaRepository repo, ISlaConfigService config, IClock clock, IAuditLogger audit, ILogger<SlaService> logger, IOptions<SlaOptions> options)
    {
        _repo = repo;
        _config = config;
        _clock = clock;
        _audit = audit;
        _logger = logger;
        _options = options.Value;
    }

    private sealed class RuntimeConfig
    {
        public required SlaConfiguration Config { get; init; }
        public required EngineSettings Settings { get; init; }
        public required SlaCalendar Calendar { get; init; }
    }

    private sealed class Context
    {
        public required SlaRequestInfo Request { get; init; }
        public required RuntimeConfig Cfg { get; init; }
        public required DateTime Now { get; init; }
        public string? User { get; init; }
        public required string Source { get; init; }
        public required List<StageRecord> Stages { get; set; }
        public bool Initialized { get; init; }
    }

    private sealed record ApplyResult(string Action, string From, bool Changed);

    private static string Norm(string? s) => LegacyFormat.Norm(s);
    private static bool IsDone(StageRecord s) => s.Status is StageStatus.Completed or StageStatus.Cancelled;

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Fail(int statusCode, params string[] errors) => throw new SlaException(statusCode, errors);

    // ------------------------------------------------------------------ state-changing operations

    public async Task<StageStatusChangeResult> UpdateStageStatusAsync(int requestId, StageStatusInput input, string? user, CancellationToken ct = default)
    {
        var (errors, clean) = SlaEngine.ValidateStageStatusInput(input);
        if (errors.Count > 0) throw new SlaException(400, errors);

        var result = await _repo.InTransactionAsync(async () =>
        {
            var ctx = await LoadAsync(requestId, user, "api", ct);
            var stage = ctx.Stages.FirstOrDefault(s => Norm(s.StageName) == Norm(clean.Stage));
            if (stage is null)
            {
                Fail(404, $"Stage \"{clean.Stage}\" is not tracked for this request. Tracked stages: {string.Join(", ", ctx.Stages.Select(s => s.StageName))}.");
            }

            var before = CurrentName(ctx);
            var applied = await ApplyAsync(ctx, stage, clean.Status!, clean.PlannedEffort, clean.ActualEffortDays, clean.Note, force: false, ct);
            if (applied.Changed) await SettleAsync(ctx, before, applied.Action, ct);
            var transition = new TransitionInfo(stage.StageName, applied.From, stage.Status, applied.Action, applied.Changed);
            return new StageStatusChangeResult(transition, await BuildSlaAsync(ctx, ct));
        }, ct);

        if (result.Transition.Changed)
        {
            _audit.Log($"sla.stage.{result.Transition.Action}", "business_request", requestId,
                $"{result.Transition.Stage}: {result.Transition.From} -> {result.Transition.To}");
        }
        return result;
    }

    // ------------------------------------------------------------------ reads

    public async Task<RequestSlaView> GetRequestSlaAsync(int requestId, CancellationToken ct = default)
    {
        var request = await _repo.GetRequestAsync(requestId, lockRow: false, ct);
        if (request is null) Fail(404, "Request not found.");
        var stages = await _repo.ListStagesAsync(requestId, ct);
        var cfg = await RuntimeAsync(ct);
        if (NeedsReconcile(stages, cfg))
        {
            // No tracking yet, or the pipeline changed since it was created: reconcile first (writes, so in a transaction).
            return await _repo.InTransactionAsync(async () => await BuildSlaAsync(await LoadAsync(requestId, null, "migration", ct), ct), ct);
        }
        var visible = SlaEngine.PartitionStages(stages, PipelineNames(cfg)).Visible;
        return await BuildSlaAsync(new Context { Request = request, Cfg = cfg, Now = _clock.UtcNow, Source = "api", Stages = visible }, ct);
    }

    public async Task<SlaHistoryView> GetHistoryAsync(int requestId, CancellationToken ct = default)
    {
        if (await _repo.GetRequestAsync(requestId, lockRow: false, ct) is null) Fail(404, "Request not found.");
        var statusHistory = await _repo.ListStatusHistoryAsync(requestId, ct);
        var stageHistory = await _repo.ListStageHistoryAsync(requestId, ct);
        var pauses = await _repo.ListPausesAsync(requestId, ct);
        return new SlaHistoryView(statusHistory, stageHistory, pauses);
    }

    /// <summary>Request- and stage-level metrics across every tracked request. Filters: priority, stage (current), health.</summary>
    public async Task<SlaDashboard> GetDashboardAsync(string? priority, string? stage, string? health, CancellationToken ct = default)
    {
        var cfg = await RuntimeAsync(ct);
        var now = _clock.UtcNow;
        var requests = await _repo.ListTrackedRequestsAsync(ct);
        var stages = await _repo.ListAllStagesAsync(ct);
        var work = await _repo.ListAllWorkAsync(ct);
        var pauses = await _repo.ListAllPausesAsync(ct);
        var stagesByRequest = stages.GroupBy(s => s.BusinessRequestId).ToDictionary(g => g.Key, g => g.ToList());
        var workByStage = work.GroupBy(w => w.RequestStageId).ToDictionary(g => g.Key, g => g.ToList());
        var pausesByStage = pauses.GroupBy(p => p.RequestStageId).ToDictionary(g => g.Key, g => g.ToList());
        var names = PipelineNames(cfg);

        var rows = new List<SlaDashboardRow>();
        var stageAgg = new Dictionary<string, (int Order, int Requests, double Planned, double Expected, double Actual, double Hold)>();
        var stageAggOrder = new List<string>();
        foreach (var req in requests)
        {
            var reqStages = SlaEngine.PartitionStages(stagesByRequest.GetValueOrDefault(req.Id) ?? new List<StageRecord>(), names).Visible;
            var factor = PriorityFactor(cfg, req.Priority);
            var metrics = reqStages.Select(s => SlaEngine.CalculateStageSla(
                s, workByStage.GetValueOrDefault(s.Id) ?? new List<WorkSegment>(), pausesByStage.GetValueOrDefault(s.Id) ?? new List<PauseRecord>(),
                factor, cfg.Settings, cfg.Calendar, now)).ToList();
            var summary = SlaEngine.SummarizeRequest(metrics, cfg.Settings, cfg.Calendar, now);
            var current = SlaEngine.DeriveCurrentStage(reqStages);
            var row = new SlaDashboardRow
            {
                RequestId = req.Id,
                BrNo = req.BrNo,
                Title = req.Title,
                Priority = req.Priority,
                CurrentStage = current?.StageName ?? "Closed",
                Status = current?.Status ?? StageStatus.Completed,
                TotalPlannedDays = summary.TotalPlannedDays,
                TotalExpectedDays = summary.TotalExpectedDays,
                TotalConsumedDays = summary.TotalConsumedDays,
                TotalRemainingDays = summary.TotalRemainingDays,
                TotalHoldDays = summary.TotalHoldDays,
                VarianceDays = summary.VarianceDays,
                UtilizationPct = summary.UtilizationPct,
                SlaHealth = summary.SlaHealth,
                UnestimatedStages = summary.UnestimatedStages,
                IsFinished = summary.IsFinished,
                ExpectedCompletionDate = summary.ExpectedCompletionDate,
            };
            if (!string.IsNullOrEmpty(priority) && Norm(row.Priority) != Norm(priority)) continue;
            if (!string.IsNullOrEmpty(stage) && Norm(row.CurrentStage) != Norm(stage)) continue;
            if (!string.IsNullOrEmpty(health) && Norm(row.SlaHealth) != Norm(health)) continue;
            rows.Add(row);

            foreach (var m in metrics)
            {
                if (m.IsBackfilled || m.Status is not (StageStatus.InProgress or StageStatus.OnHold or StageStatus.Completed)) continue;
                if (!stageAgg.TryGetValue(m.Stage, out var a))
                {
                    a = (m.Order, 0, 0, 0, 0, 0);
                    stageAggOrder.Add(m.Stage);
                }
                stageAgg[m.Stage] = (a.Order, a.Requests + 1, a.Planned + (m.PlannedEffort ?? 0), a.Expected + m.ExpectedSlaDays, a.Actual + m.ActualEffort, a.Hold + m.HoldDays);
            }
        }

        var stageMetrics = stageAggOrder.Select(name => (Name: name, Agg: stageAgg[name]))
            .OrderBy(x => x.Agg.Order)
            .Select(x =>
            {
                double? utilization = x.Agg.Expected > 0 ? x.Agg.Actual / x.Agg.Expected * 100 : null;
                return new SlaDashboardStage
                {
                    Stage = x.Name,
                    Requests = x.Agg.Requests,
                    PlannedEffort = JsMath.Round2(x.Agg.Planned),
                    ActualEffort = JsMath.Round2(x.Agg.Actual),
                    VarianceDays = JsMath.Round2(x.Agg.Actual - x.Agg.Planned),
                    HoldDays = JsMath.Round2(x.Agg.Hold),
                    UtilizationPct = utilization is null ? null : JsMath.Round2(utilization.Value),
                    SlaState = SlaEngine.ClassifyHealth(utilization, cfg.Settings),
                };
            }).ToList();

        double Sum(Func<SlaDashboardRow, double> f) => JsMath.Round2(rows.Aggregate(0d, (t, r) => t + f(r)));
        var counts = new Dictionary<string, int> { [SlaHealth.OnTrack] = 0, [SlaHealth.AtRisk] = 0, [SlaHealth.Breached] = 0 };
        foreach (var r in rows) if (r.SlaHealth is not null) counts[r.SlaHealth]++;

        return new SlaDashboard(
            new SlaDashboardThresholds(cfg.Settings.AtRiskThresholdPct, cfg.Settings.BreachThresholdPct),
            new SlaDashboardTotals(rows.Count, Sum(r => r.TotalPlannedDays), Sum(r => r.TotalConsumedDays), Sum(r => r.TotalHoldDays), counts),
            stageMetrics,
            rows);
    }

    // ------------------------------------------------------------------ migration / compatibility

    /// <summary>
    /// Creates tracking for every request that has none, and re-syncs requests whose tracking no longer matches the
    /// configured pipeline (idempotent). Used at startup (background service).
    /// </summary>
    public async Task<(int Total, int Created)> BackfillMissingAsync(string user = "sla-migration", CancellationToken ct = default)
    {
        var cfg = await RuntimeAsync(ct);
        var ids = new List<int>(await _repo.ListRequestIdsWithoutTrackingAsync(ct));
        ids.AddRange(await _repo.ListRequestIdsWithStaleActiveStagesAsync(PipelineNames(cfg).ToList(), ct));
        var done = 0;
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await _repo.InTransactionAsync(async () => { await LoadAsync(id, user, "migration", ct); return true; }, ct);
                done++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "SLA backfill failed for request {RequestId}", id);
            }
        }
        return (ids.Count, done);
    }

    /// <summary>
    /// Reconciles the engine with the request's legacy stage/status after the old form saved it. The legacy fields win
    /// (they were just written by the user), so nothing is written back to them. Returns true when tracking was created.
    /// </summary>
    public Task<bool> SyncFromLegacyAsync(int requestId, string? user, CancellationToken ct = default) =>
        _repo.InTransactionAsync(async () =>
        {
            var ctx = await LoadAsync(requestId, user, "legacy-sync", ct);
            if (ctx.Initialized) return true;

            var before = CurrentName(ctx);
            var status = ctx.Cfg.Config.Statuses.FirstOrDefault(s => Norm(s.Name) == Norm(ctx.Request.Status));
            var intent = SlaEngine.ClassifyLegacyStatus(ctx.Request.Status, status?.State);
            async Task Step(StageRecord stage, string to, string note)
            {
                try
                {
                    await ApplyAsync(ctx, stage, to, null, null, note, force: true, ct);
                }
                catch (SlaException)
                {
                    // e.g. a stage without an estimate cannot be started: leave it as it is.
                }
            }

            if (intent is "done" or "cancelled")
            {
                foreach (var s in ctx.Stages.ToList())
                {
                    if (IsDone(s)) continue;
                    var active = s.Status is StageStatus.InProgress or StageStatus.OnHold;
                    await Step(s, active && intent == "done" ? StageStatus.Completed : StageStatus.Cancelled, "Request closed in the request form");
                }
            }
            else
            {
                var target = ctx.Stages.FirstOrDefault(s => Norm(s.StageName) == Norm(ctx.Request.Stage));
                if (target is not null) await AlignToStageAsync(ctx, target, intent, Step);
                await ApplyIntentAsync(ctx, intent, Step);
            }
            await SettleAsync(ctx, before, null, ct);
            return false;
        }, ct);

    public async Task SyncLegacyRequestAsync(int requestId, string? user, CancellationToken ct = default)
    {
        if (!_options.EngineSyncEnabled) return;
        try
        {
            await SyncFromLegacyAsync(requestId, user, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "SLA sync failed for request {RequestId}", requestId);
        }
    }

    // ------------------------------------------------------------------ internals

    private async Task<RuntimeConfig> RuntimeAsync(CancellationToken ct)
    {
        var config = await _config.GetAsync(ct);
        return new RuntimeConfig
        {
            Config = config,
            Settings = config.Settings.Engine,
            Calendar = new SlaCalendar(config.Settings.WeekendDays, _clock.BusinessTimeZone),
        };
    }

    private static double PriorityFactor(RuntimeConfig cfg, string? priorityName)
    {
        var p = cfg.Config.Priorities.FirstOrDefault(x => Norm(x.Name) == Norm(priorityName));
        return p is not null && p.Factor > 0 ? p.Factor : 1;
    }

    private static string ClosedName(RuntimeConfig cfg, IReadOnlyCollection<StageRecord> stages)
    {
        var closed = cfg.Config.Stages.FirstOrDefault(s => Norm(s.Name) == "closed");
        if (closed is not null) return closed.Name;
        return stages.Count > 0 ? stages.OrderBy(s => s.StageOrder).Last().StageName : "Closed";
    }

    private static string CurrentName(Context ctx) =>
        SlaEngine.DeriveCurrentStage(ctx.Stages)?.StageName ?? ClosedName(ctx.Cfg, ctx.Stages);

    private static HashSet<string> PipelineNames(RuntimeConfig cfg) =>
        new(cfg.Config.Stages.Where(s => s.IsPipeline).Select(s => Norm(s.Name)));

    /// <summary>True when the stored stages cannot be used as they are: none yet, a pipeline stage is missing, or a removed stage is still running.</summary>
    private static bool NeedsReconcile(List<StageRecord> stages, RuntimeConfig cfg)
    {
        var names = PipelineNames(cfg);
        var tracked = new HashSet<string>(stages.Select(s => Norm(s.StageName)));
        return stages.Count == 0 || names.Any(n => !tracked.Contains(n)) || SlaEngine.HasStaleActiveStage(stages, names);
    }

    /// <summary>
    /// Locks the request and loads its stages, making them match the configured pipeline: no tracking yet → initialized
    /// from the legacy stage/status; a removed stage still running on a request nobody used the engine on → rebuilt;
    /// a stage joined the pipeline → added; removed untouched stages → ignored (kept in the database).
    /// </summary>
    private async Task<Context> LoadAsync(int requestId, string? user, string source, CancellationToken ct)
    {
        var request = await _repo.GetRequestAsync(requestId, lockRow: true, ct);
        if (request is null) Fail(404, "Request not found.");
        var cfg = await RuntimeAsync(ct);
        var now = _clock.UtcNow;
        var names = PipelineNames(cfg);
        var all = await _repo.ListStagesAsync(requestId, ct);
        var initialized = false;
        if (all.Count == 0)
        {
            all = await InitializeAsync(request, cfg, now, user, ct);
            initialized = true;
        }
        else if (SlaEngine.HasStaleActiveStage(all, names) && !await _repo.HasUserActivityAsync(requestId, ct))
        {
            await _repo.DeleteTrackingAsync(requestId, ct);
            all = await InitializeAsync(request, cfg, now, user, ct);
            initialized = true;
        }
        var stages = SlaEngine.PartitionStages(all, names).Visible;
        if (await AddMissingStagesAsync(request, cfg, stages, now, user, ct))
        {
            stages = SlaEngine.PartitionStages(await _repo.ListStagesAsync(requestId, ct), names).Visible;
        }
        return new Context { Request = request, Cfg = cfg, Now = now, User = user, Source = source, Stages = stages, Initialized = initialized };
    }

    private async Task<bool> AddMissingStagesAsync(SlaRequestInfo request, RuntimeConfig cfg, List<StageRecord> stages, DateTime now, string? user, CancellationToken ct)
    {
        var tracked = new HashSet<string>(stages.Select(s => Norm(s.StageName)));
        var missing = cfg.Config.Stages.Where(s => s.IsPipeline && !tracked.Contains(Norm(s.Name))).ToList();
        if (missing.Count == 0) return false;
        var current = SlaEngine.DeriveCurrentStage(stages);
        foreach (var m in missing)
        {
            double? planned = m.EffortDays > 0 ? m.EffortDays : null;
            var before = current is null || m.StageOrder < current.StageOrder; // already past it (or the request is finished)
            var status = before ? StageStatus.Completed : planned is null ? StageStatus.NotYetEstimated : StageStatus.NotStarted;
            var stageId = await _repo.InsertStageAsync(new StageRecord
            {
                BusinessRequestId = request.Id,
                StageName = m.Name,
                StageOrder = m.StageOrder,
                Status = status,
                StageEffortDays = planned,
                ActualEffortDays = 0,
                RemainingEffortDays = before ? 0 : planned,
                CompletedAt = before ? now : null,
                IsBackfilled = before,
                UpdatedBy = user,
                UpdatedAt = now,
            }, ct);
            await _repo.InsertStatusHistoryAsync(new StatusHistoryEntry(
                stageId, request.Id, m.Name, null, status, SlaAction.Init, "migration", planned, 0, before ? 0 : planned,
                "Added to the request when the stage joined the SLA pipeline", user, now), ct);
        }
        return true;
    }

    private async Task<List<StageRecord>> InitializeAsync(SlaRequestInfo request, RuntimeConfig cfg, DateTime now, string? user, CancellationToken ct)
    {
        var pipeline = cfg.Config.Stages.Where(s => s.IsPipeline).ToList();
        if (pipeline.Count == 0) Fail(409, "No pipeline stages are configured in the SLA settings.");
        var legacyStage = cfg.Config.Stages.FirstOrDefault(s => Norm(s.Name) == Norm(request.Stage));
        var status = cfg.Config.Statuses.FirstOrDefault(s => Norm(s.Name) == Norm(request.Status));
        var intent = SlaEngine.ClassifyLegacyStatus(request.Status, status?.State);

        foreach (var p in SlaEngine.PlanInitialStages(pipeline, legacyStage, intent))
        {
            var finished = p.Status is StageStatus.Completed or StageStatus.Cancelled;
            var running = p.Status is StageStatus.InProgress or StageStatus.OnHold;
            var stageId = await _repo.InsertStageAsync(new StageRecord
            {
                BusinessRequestId = request.Id,
                StageName = p.StageName,
                StageOrder = p.StageOrder,
                Status = p.Status,
                StageEffortDays = p.StageEffortDays,
                ActualEffortDays = 0,
                RemainingEffortDays = p.Status == StageStatus.NotYetEstimated ? null : finished ? 0 : p.StageEffortDays,
                StartedAt = running ? now : null,
                CompletedAt = p.Status == StageStatus.Completed ? now : null,
                PausedAt = p.Status == StageStatus.OnHold ? now : null,
                IsBackfilled = p.IsBackfilled,
                UpdatedBy = user,
                UpdatedAt = now,
            }, ct);
            // Consumption starts now: legacy data has no record of earlier effort.
            if (p.Status == StageStatus.InProgress)
            {
                await _repo.OpenWorkAsync(new WorkSegment { RequestStageId = stageId, BusinessRequestId = request.Id, StartedAt = now, CreatedBy = user }, ct);
            }
            if (p.Status == StageStatus.OnHold)
            {
                await _repo.OpenPauseAsync(new PauseRecord { RequestStageId = stageId, BusinessRequestId = request.Id, PausedAt = now, Reason = "Imported from request status", PausedBy = user }, ct);
            }
            await _repo.InsertStatusHistoryAsync(new StatusHistoryEntry(
                stageId, request.Id, p.StageName, null, p.Status, SlaAction.Init, "migration", p.StageEffortDays, 0, p.StageEffortDays,
                p.IsBackfilled ? "Backfilled from legacy request data" : "Initialized from legacy request data", user, now), ct);
        }
        var stages = await _repo.ListStagesAsync(request.Id, ct);
        var current = SlaEngine.DeriveCurrentStage(stages);
        await _repo.InsertStageHistoryAsync(new StageHistoryEntry(
            request.Id, string.IsNullOrEmpty(request.Stage) ? null : request.Stage, current?.StageName ?? ClosedName(cfg, stages),
            "initialized", "migration", user, now), ct);
        return stages;
    }

    /// <summary>Applies one validated transition: segments, aggregates, audit row.</summary>
    private async Task<ApplyResult> ApplyAsync(Context ctx, StageRecord stage, string toStatus, double? plannedEffort, double? actualEffortDays, string? note, bool force, CancellationToken ct)
    {
        var now = ctx.Now;
        var cal = ctx.Cfg.Calendar;
        var user = ctx.User;
        var plan = SlaEngine.PlanTransition(stage, toStatus, ctx.Stages, plannedEffort, ctx.Cfg.Settings, force);
        if (plan.Errors.Count > 0) throw new SlaException(409, plan.Errors);
        var action = plan.Action!;
        if (action == SlaAction.Noop)
        {
            if (actualEffortDays is not null && stage.Status == StageStatus.Completed)
            {
                Fail(409, $"\"{stage.StageName}\" is already Completed: reopen it to change its recorded effort.");
            }
            return new ApplyResult(action, stage.Status, false);
        }

        var from = stage.Status;
        // Sequential on purpose: a SQL Server transaction has a single connection.
        var allWork = await _repo.ListWorkAsync(stage.BusinessRequestId, ct);
        var allPauses = await _repo.ListPausesAsync(stage.BusinessRequestId, ct);
        var work = allWork.Where(w => w.RequestStageId == stage.Id).ToList();
        var pauses = allPauses.Where(p => p.RequestStageId == stage.Id).ToList();
        var openWork = work.FirstOrDefault(w => w.EndedAt is null);
        var openPause = pauses.FirstOrDefault(p => p.ResumedAt is null);
        int Days(DateTime since) => SlaEngine.WorkingDaysBetween(since, now, cal);

        async Task CloseWork(string reason)
        {
            if (openWork is null) return;
            await _repo.CloseWorkAsync(openWork.Id, now, reason, Days(openWork.StartedAt), ct);
            openWork.EndedAt = now;
        }
        async Task ClosePause()
        {
            if (openPause is null) return;
            await _repo.ClosePauseAsync(openPause.Id, now, Days(openPause.PausedAt), user, ct);
            openPause.ResumedAt = now;
        }
        async Task OpenNewWork()
        {
            await _repo.OpenWorkAsync(new WorkSegment { RequestStageId = stage.Id, BusinessRequestId = stage.BusinessRequestId, StartedAt = now, CreatedBy = user }, ct);
            work.Add(new WorkSegment { RequestStageId = stage.Id, StartedAt = now, EndedAt = null });
        }

        var startedAt = stage.StartedAt;
        var completedAt = stage.CompletedAt;
        var pausedAt = stage.PausedAt;
        var resumeAt = stage.ResumeAt;
        var reopenCount = stage.ReopenCount;
        switch (action)
        {
            case SlaAction.Start:
            case SlaAction.Reopen:
                startedAt = stage.StartedAt ?? now;
                completedAt = null;
                pausedAt = null;
                if (action == SlaAction.Reopen) reopenCount = stage.ReopenCount + 1;
                await OpenNewWork();
                break;
            case SlaAction.Pause:
                await CloseWork("Hold");
                await _repo.OpenPauseAsync(new PauseRecord
                {
                    RequestStageId = stage.Id, BusinessRequestId = stage.BusinessRequestId, PausedAt = now,
                    Reason = string.IsNullOrEmpty(note) ? null : note, PausedBy = user,
                }, ct);
                pauses.Add(new PauseRecord { RequestStageId = stage.Id, PausedAt = now, ResumedAt = null });
                pausedAt = now;
                break;
            case SlaAction.Resume:
                await ClosePause();
                await OpenNewWork();
                pausedAt = null;
                resumeAt = now;
                break;
            case SlaAction.Complete:
                await CloseWork("Completed");
                await ClosePause();
                completedAt = now;
                pausedAt = null;
                break;
            case SlaAction.Cancel:
                await CloseWork("Cancelled");
                await ClosePause();
                pausedAt = null;
                completedAt = null;
                break;
            case SlaAction.Reinstate:
                completedAt = null;
                break;
            default: // estimate / unestimate: effort only
                break;
        }

        // Consumed effort comes from the segments only – priority never enters. A completed stage keeps its actual
        // effort (manual override if given), which then stays fixed until the stage is reopened.
        var consumed = SlaEngine.CalculateConsumedEffort(work, now, cal);
        var actual = action == SlaAction.Complete ? actualEffortDays ?? consumed
            : plan.ToStatus == StageStatus.Completed ? stage.ActualEffortDays
            : consumed;
        var hold = SlaEngine.CalculateHoldDays(pauses, now, cal);
        var remaining = SlaEngine.CalculateRemainingEffort(plan.ToStatus, plan.PlannedEffort, actual);

        stage.Status = plan.ToStatus;
        stage.StageEffortDays = plan.PlannedEffort;
        stage.UpdatedBy = user;
        stage.UpdatedAt = now;
        stage.StartedAt = startedAt;
        stage.CompletedAt = completedAt;
        stage.PausedAt = pausedAt;
        stage.ResumeAt = resumeAt;
        stage.ReopenCount = reopenCount;
        stage.SlaConsumedDays = consumed;
        stage.ActualEffortDays = actual;
        stage.TotalHoldTime = hold;
        stage.RemainingEffortDays = remaining;
        await _repo.UpdateStageAsync(stage, ct);
        await _repo.InsertStatusHistoryAsync(new StatusHistoryEntry(
            stage.Id, stage.BusinessRequestId, stage.StageName, from, plan.ToStatus, action, ctx.Source,
            plan.PlannedEffort, consumed, remaining, string.IsNullOrEmpty(note) ? null : note, user, now), ct);
        return new ApplyResult(action, from, true);
    }

    /// <summary>
    /// After a change: optionally auto-start the next stage, audit the stage movement, and mirror the result into the
    /// legacy stage/status columns (not for legacy-sync, where those fields are the input).
    /// </summary>
    private async Task SettleAsync(Context ctx, string beforeName, string? action, CancellationToken ct)
    {
        if (action == SlaAction.Complete && ctx.Cfg.Settings.AutoStartNextStage)
        {
            var next = SlaEngine.DeriveCurrentStage(ctx.Stages);
            if (next is not null && next.Status == StageStatus.NotStarted)
            {
                try
                {
                    await ApplyAsync(ctx, next, StageStatus.InProgress, null, null, "Started automatically when the previous stage completed", force: false, ct);
                }
                catch (SlaException)
                {
                    // not startable (e.g. blocked by another active stage): stays Not Started
                }
            }
        }

        var afterName = CurrentName(ctx);
        if (afterName != beforeName)
        {
            await _repo.InsertStageHistoryAsync(new StageHistoryEntry(ctx.Request.Id, beforeName, afterName, "stage-change", ctx.Source, ctx.User, ctx.Now), ct);
        }
        if (ctx.Source == "legacy-sync") return;

        var statusName = PickLegacyStatus(ctx.Cfg.Config.Statuses, ctx.Request.Status, ctx.Stages);
        var stageName = afterName != beforeName ? afterName : null;
        if (statusName is not null || stageName is not null)
        {
            await _repo.SetLegacyFieldsAsync(ctx.Request.Id, stageName, statusName, ctx.User, ctx.Now, ct);
            if (stageName is not null) ctx.Request.Stage = stageName;
            if (statusName is not null) ctx.Request.Status = statusName;
        }
    }

    /// <summary>Legacy stage moved: close what was skipped (forward) or bring the target back (backward).</summary>
    private static async Task AlignToStageAsync(Context ctx, StageRecord target, string intent, Func<StageRecord, string, string, Task> step)
    {
        foreach (var s in ctx.Stages.ToList())
        {
            if (s.StageOrder < target.StageOrder && !IsDone(s))
            {
                var active = s.Status is StageStatus.InProgress or StageStatus.OnHold;
                await step(s, active ? StageStatus.Completed : StageStatus.Cancelled, "Skipped: request moved forward in the request form");
            }
        }
        if (IsDone(target) && intent != "idle")
        {
            foreach (var s in ctx.Stages.ToList())
            {
                if (s.StageOrder > target.StageOrder && s.Status == StageStatus.InProgress)
                {
                    await step(s, StageStatus.OnHold, "Paused: request moved back to an earlier stage");
                }
            }
            await step(target, target.Status == StageStatus.Cancelled ? StageStatus.NotStarted : StageStatus.InProgress, "Request moved back in the request form");
        }
    }

    /// <summary>
    /// Legacy status → the current stage's status (Active starts/resumes, Paused holds). A finished request is never
    /// reopened from here (only when the legacy stage was moved back to a real pipeline stage).
    /// </summary>
    private static async Task ApplyIntentAsync(Context ctx, string intent, Func<StageRecord, string, string, Task> step)
    {
        if (intent is not ("active" or "hold")) return;
        var subject = SlaEngine.DeriveCurrentStage(ctx.Stages);
        if (subject is null) return;
        if (intent == "active" && subject.Status is StageStatus.NotStarted or StageStatus.OnHold or StageStatus.Completed)
        {
            await step(subject, StageStatus.InProgress, "Status set to active in the request form");
        }
        else if (intent == "hold" && subject.Status == StageStatus.Completed)
        {
            await step(subject, StageStatus.InProgress, "Request reopened in the request form");
            await step(subject, StageStatus.OnHold, "Status set to on hold in the request form");
        }
        else if (intent == "hold" && subject.Status == StageStatus.InProgress)
        {
            await step(subject, StageStatus.OnHold, "Status set to on hold in the request form");
        }
    }

    /// <summary>
    /// Chooses the legacy status lookup value matching the engine's state, or null to leave it alone. A legacy status is
    /// kept when it already expresses the same intent (e.g. "Pending for Approval" while work is In Progress).
    /// </summary>
    internal static string? PickLegacyStatus(IReadOnlyList<SlaStatusDefinition> statuses, string? legacyStatus, IReadOnlyList<StageRecord> stages)
    {
        var current = SlaEngine.DeriveCurrentStage(stages);
        string intent, state;
        string[] prefer;
        if (current is null)
        {
            var allCancelled = stages.Count > 0 && stages.All(s => s.Status == StageStatus.Cancelled);
            intent = allCancelled ? "cancelled" : "done";
            state = "Done";
            prefer = allCancelled ? new[] { "cancelled", "canceled" } : new[] { "completed", "closed" };
        }
        else if (current.Status == StageStatus.OnHold)
        {
            (intent, state, prefer) = ("hold", "Paused", new[] { "on hold", "hold" });
        }
        else if (current.Status == StageStatus.InProgress)
        {
            (intent, state, prefer) = ("active", "Active", new[] { "in progress" });
        }
        else if (current.Status == StageStatus.NotYetEstimated)
        {
            (intent, state, prefer) = ("idle", "Active", new[] { "not yet estimated", "not estimated", "not started" });
        }
        else
        {
            (intent, state, prefer) = ("idle", "Active", new[] { "not started" });
        }

        var existing = statuses.FirstOrDefault(s => Norm(s.Name) == Norm(legacyStatus));
        if (existing is not null && SlaEngine.ClassifyLegacyStatus(existing.Name, existing.State) == intent) return null;
        foreach (var name in prefer)
        {
            var hit = statuses.FirstOrDefault(s => Norm(s.Name) == name && s.State == state);
            if (hit is not null) return hit.Name;
        }
        return null;
    }

    /// <summary>Full response for one request: current-stage headline fields, request roll-up, per-stage metrics.</summary>
    private async Task<RequestSlaView> BuildSlaAsync(Context ctx, CancellationToken ct)
    {
        var work = await _repo.ListWorkAsync(ctx.Request.Id, ct);
        var pauses = await _repo.ListPausesAsync(ctx.Request.Id, ct);
        var workByStage = work.GroupBy(w => w.RequestStageId).ToDictionary(g => g.Key, g => g.ToList());
        var pausesByStage = pauses.GroupBy(p => p.RequestStageId).ToDictionary(g => g.Key, g => g.ToList());
        var factor = PriorityFactor(ctx.Cfg, ctx.Request.Priority);

        var sorted = ctx.Stages.OrderBy(s => s.StageOrder).ThenBy(s => s.Id).ToList();
        var metrics = sorted.Select(s => SlaEngine.CalculateStageSla(
            s, workByStage.GetValueOrDefault(s.Id) ?? new List<WorkSegment>(), pausesByStage.GetValueOrDefault(s.Id) ?? new List<PauseRecord>(),
            factor, ctx.Cfg.Settings, ctx.Cfg.Calendar, ctx.Now)).ToList();
        var summary = SlaEngine.SummarizeRequest(metrics, ctx.Cfg.Settings, ctx.Cfg.Calendar, ctx.Now);

        var current = SlaEngine.DeriveCurrentStage(ctx.Stages);
        var headline = current is not null
            ? metrics.FirstOrDefault(m => m.Stage == current.StageName)
            : metrics.OrderByDescending(m => m.CompletedAt ?? DateTime.MinValue).ThenByDescending(m => m.Order).FirstOrDefault();
        var allCancelled = ctx.Stages.Count > 0 && ctx.Stages.All(s => s.Status == StageStatus.Cancelled);

        return new RequestSlaView
        {
            RequestId = ctx.Request.Id,
            Priority = string.IsNullOrEmpty(ctx.Request.Priority) ? null : ctx.Request.Priority,
            PriorityFactor = factor,
            PriorityFactorMode = ctx.Cfg.Settings.PriorityFactorMode,
            CurrentStage = current?.StageName ?? ClosedName(ctx.Cfg, ctx.Stages),
            Status = current?.Status ?? (allCancelled ? StageStatus.Cancelled : StageStatus.Completed),
            PlannedEffort = headline?.PlannedEffort,
            ActualConsumed = headline?.ActualEffort ?? 0,
            Remaining = headline?.Remaining,
            HoldDays = headline?.HoldDays ?? 0,
            ExpectedCompletionDate = headline?.ExpectedCompletionDate,
            SlaState = headline?.SlaState,
            ExpectedSlaDays = headline?.ExpectedSlaDays ?? 0,
            UtilizationPct = headline?.UtilizationPct,
            Request = summary,
            Stages = metrics,
        };
    }
}
