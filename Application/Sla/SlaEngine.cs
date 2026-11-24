using System.Globalization;
using ItrmTracker.Application.Common;
using ItrmTracker.Domain.Constants;

namespace ItrmTracker.Application.Sla;

/// <summary>
/// Pure SLA calculation engine – no database, no clock, no I/O; everything is passed in.
/// Faithful port of <c>lib/sla-engine.js</c> (same formulas, rounding and messages).
/// </summary>
/// <remarks>
/// Concepts:
/// <list type="bullet">
/// <item><b>plannedEffort</b> – man-days planned for a stage (raw estimate, never changed by priority).</item>
/// <item><b>expectedSla</b> – stage target in working days = planned adjusted by the request priority.</item>
/// <item><b>consumed</b> – working days the stage spent In Progress (holds excluded, never priority-adjusted).</item>
/// <item><b>utilization</b> – consumed / expectedSla, classified with the configurable thresholds.</item>
/// </list>
/// Dates are counted at calendar-day granularity in the business time zone: a segment covers the working days
/// in (start day, end day], so consecutive segments never double count.
/// </remarks>
public static class SlaEngine
{
    /// <summary>Fri, Sat (0 = Sunday numbering) – the original default weekend.</summary>
    public static readonly IReadOnlyList<int> DefaultWeekend = new[] { 5, 6 };

    private static readonly Dictionary<string, string[]> Allowed = new()
    {
        [StageStatus.NotYetEstimated] = new[] { StageStatus.NotStarted, StageStatus.InProgress, StageStatus.Cancelled },
        [StageStatus.NotStarted] = new[] { StageStatus.NotYetEstimated, StageStatus.InProgress, StageStatus.Cancelled },
        [StageStatus.InProgress] = new[] { StageStatus.OnHold, StageStatus.Completed, StageStatus.Cancelled },
        [StageStatus.OnHold] = new[] { StageStatus.InProgress, StageStatus.Completed, StageStatus.Cancelled },
        [StageStatus.Completed] = new[] { StageStatus.InProgress }, // reopen
        [StageStatus.Cancelled] = new[] { StageStatus.NotStarted, StageStatus.NotYetEstimated }, // reinstate
    };

    private static string Norm(string? s) => LegacyFormat.Norm(s);
    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ settings

    private static bool Bool(string? v, bool fallback) =>
        string.IsNullOrEmpty(v) ? fallback : Norm(v) is "true" or "1" or "yes";

    /// <summary>Lenient: unknown / invalid values fall back to the defaults (used when loading stored rows).</summary>
    public static EngineSettings ParseEngineSettings(IReadOnlyDictionary<string, string?> raw)
    {
        var d = EngineSettings.Default;
        string? Get(string key) => raw.TryGetValue(key, out var v) ? v : null;

        var atRisk = Get("at_risk_threshold_pct") is { } a ? JsMath.ToNumber(a) : double.NaN;
        var breach = Get("breach_threshold_pct") is { } b ? JsMath.ToNumber(b) : double.NaN;
        var mode = Norm(Get("priority_factor_mode"));

        var outAtRisk = double.IsFinite(atRisk) && atRisk > 0 ? atRisk : d.AtRiskThresholdPct;
        var outBreach = double.IsFinite(breach) && breach > 0 ? breach : d.BreachThresholdPct;
        if (outAtRisk > outBreach) outAtRisk = outBreach;

        return new EngineSettings(
            outAtRisk,
            outBreach,
            mode is "multiplier" or "divisor" ? mode : d.PriorityFactorMode,
            Bool(Get("enforce_stage_order"), d.EnforceStageOrder),
            Bool(Get("auto_start_next_stage"), d.AutoStartNextStage));
    }

    /// <summary>
    /// Strict: used by the configuration editor. Only keys present in <paramref name="input"/> are validated and
    /// returned (as the strings that will be stored in dbo.sla_settings).
    /// </summary>
    public static (List<string> Errors, Dictionary<string, string> Clean) ValidateEngineSettings(IReadOnlyDictionary<string, string?> input)
    {
        var errors = new List<string>();
        var clean = new Dictionary<string, string>();
        bool Has(string k) => input.TryGetValue(k, out var v) && !string.IsNullOrEmpty(v);

        foreach (var key in new[] { "at_risk_threshold_pct", "breach_threshold_pct" })
        {
            if (!Has(key)) continue;
            var v = JsMath.ToNumber(input[key]);
            if (double.IsFinite(v) && v > 0 && v <= 1000) clean[key] = JsMath.ToJsString(v);
            else errors.Add($"{key}: must be a percentage above 0 and at most 1000.");
        }
        if (Has("priority_factor_mode"))
        {
            var mode = Norm(input["priority_factor_mode"]);
            if (mode is "multiplier" or "divisor") clean["priority_factor_mode"] = mode;
            else errors.Add("priority_factor_mode: must be 'multiplier' or 'divisor'.");
        }
        foreach (var key in new[] { "enforce_stage_order", "auto_start_next_stage" })
        {
            if (!Has(key)) continue;
            var v = Norm(input[key]);
            if (v is "true" or "false") clean[key] = v;
            else errors.Add($"{key}: must be true or false.");
        }
        if (errors.Count == 0)
        {
            var mergedInput = new Dictionary<string, string?>(input);
            foreach (var kv in clean) mergedInput[kv.Key] = kv.Value;
            var merged = ParseEngineSettings(mergedInput);
            var atRisk = Has("at_risk_threshold_pct") ? JsMath.ToNumber(input["at_risk_threshold_pct"]) : merged.AtRiskThresholdPct;
            var breach = Has("breach_threshold_pct") ? JsMath.ToNumber(input["breach_threshold_pct"]) : merged.BreachThresholdPct;
            if ((Has("at_risk_threshold_pct") || Has("breach_threshold_pct")) && atRisk > breach)
            {
                errors.Add("at_risk_threshold_pct must not be greater than breach_threshold_pct.");
            }
        }
        return (errors, clean);
    }

    // ------------------------------------------------------------------ working-day arithmetic

    /// <summary>Working days in (from, to] by calendar day. 0 when <paramref name="to"/> is not after <paramref name="from"/>.</summary>
    public static int WorkingDaysBetween(DateTime? from, DateTime? to, SlaCalendar calendar)
    {
        if (from is null || to is null) return 0;
        var day = calendar.DayKey(from.Value);
        var end = calendar.DayKey(to.Value);
        var n = 0;
        while (day < end)
        {
            day = day.AddDays(1);
            if (!calendar.IsWeekend(day)) n++;
        }
        return n;
    }

    /// <summary>ISO date <paramref name="days"/> working days after <paramref name="start"/> (fractions round up; the start day is not counted).</summary>
    public static string AddWorkingDays(DateOnly start, double days, SlaCalendar calendar)
    {
        var left = Math.Ceiling(days - 1e-9);
        var day = start;
        while (left > 0)
        {
            day = day.AddDays(1);
            if (!calendar.IsWeekend(day)) left--;
        }
        return Iso(day);
    }

    // ------------------------------------------------------------------ core calculations

    /// <summary>Target in working days for a planned effort. Priority only moves the target, never the consumed effort.</summary>
    public static double CalculateExpectedSlaDays(double? plannedEffort, double priorityFactor, string mode = "multiplier")
    {
        if (plannedEffort is not { } planned || !(planned > 0)) return 0;
        var f = priorityFactor > 0 ? priorityFactor : 1;
        return mode == "divisor" ? planned / f : planned * f;
    }

    /// <summary>Working days spent In Progress; open segments (EndedAt null) run up to <paramref name="now"/>.</summary>
    public static double CalculateConsumedEffort(IEnumerable<WorkSegment> workSegments, DateTime now, SlaCalendar calendar)
    {
        double sum = 0;
        foreach (var s in workSegments) sum += WorkingDaysBetween(s.StartedAt, s.EndedAt ?? now, calendar);
        return JsMath.Round2(sum);
    }

    /// <summary>Working days spent On Hold across every hold period, including one that is still open.</summary>
    public static double CalculateHoldDays(IEnumerable<PauseRecord> pauses, DateTime now, SlaCalendar calendar)
    {
        double sum = 0;
        foreach (var p in pauses) sum += WorkingDaysBetween(p.PausedAt, p.ResumedAt ?? now, calendar);
        return JsMath.Round2(sum);
    }

    /// <summary>Planned effort not yet consumed. Unknown (null) until estimated; nothing remains once Completed/Cancelled.</summary>
    public static double? CalculateRemainingEffort(string status, double? plannedEffort, double actualEffort)
    {
        if (status == StageStatus.NotYetEstimated || plannedEffort is null) return null;
        if (status is StageStatus.Completed or StageStatus.Cancelled) return 0;
        return JsMath.Round2(Math.Max(plannedEffort.Value - actualEffort, 0));
    }

    public static string CalculateExpectedCompletionDate(DateTime now, double remainingDays, SlaCalendar calendar) =>
        AddWorkingDays(calendar.DayKey(now), Math.Max(double.IsNaN(remainingDays) ? 0 : remainingDays, 0), calendar);

    public static string? ClassifyHealth(double? utilizationPct, EngineSettings settings)
    {
        if (utilizationPct is not { } u) return null;
        if (u > settings.BreachThresholdPct) return SlaHealth.Breached;
        if (u > settings.AtRiskThresholdPct) return SlaHealth.AtRisk;
        return SlaHealth.OnTrack;
    }

    public static string? WorstHealth(IEnumerable<string?> list)
    {
        string? worst = null;
        foreach (var h in list)
        {
            if (string.IsNullOrEmpty(h)) continue;
            if (worst is null || SlaHealth.Rank(h) > SlaHealth.Rank(worst)) worst = h;
        }
        return worst;
    }

    /// <summary>Every metric for one stage. Segments/pauses belong to that stage.</summary>
    public static StageMetrics CalculateStageSla(
        StageRecord stage,
        IEnumerable<WorkSegment> workSegments,
        IEnumerable<PauseRecord> pauses,
        double priorityFactor,
        EngineSettings settings,
        SlaCalendar calendar,
        DateTime now)
    {
        var status = stage.Status;
        var planned = stage.StageEffortDays;
        var consumed = CalculateConsumedEffort(workSegments, now, calendar);
        var holdDays = CalculateHoldDays(pauses, now, calendar);
        // A completed stage keeps the stored figure (which may be a manual override of the calendar-derived one).
        var actual = status == StageStatus.Completed ? stage.ActualEffortDays : consumed;
        var expectedRaw = CalculateExpectedSlaDays(planned, priorityFactor, settings.PriorityFactorMode);

        var started = status is StageStatus.InProgress or StageStatus.OnHold or StageStatus.Completed;
        var done = status is StageStatus.Completed or StageStatus.Cancelled;
        var remaining = CalculateRemainingEffort(status, planned, actual);
        double? remainingExpected = status == StageStatus.NotYetEstimated || planned is null ? null : done ? 0 : Math.Max(expectedRaw - consumed, 0);
        double? utilizationPct = status == StageStatus.NotYetEstimated || status == StageStatus.Cancelled || expectedRaw <= 0
            ? null
            : actual / expectedRaw * 100;

        string? expectedCompletionDate = null;
        if (status == StageStatus.Completed)
        {
            expectedCompletionDate = stage.CompletedAt is { } c ? Iso(calendar.DayKey(c)) : null;
        }
        else if (remainingExpected is { } re && !done)
        {
            // On Hold projects as if the stage resumed today.
            expectedCompletionDate = CalculateExpectedCompletionDate(now, re, calendar);
        }

        return new StageMetrics
        {
            Stage = stage.StageName,
            Order = stage.StageOrder,
            Status = status,
            PlannedEffort = planned,
            ExpectedSlaDays = JsMath.Round2(expectedRaw),
            ConsumedDays = consumed,
            ActualEffort = JsMath.Round2(actual),
            Remaining = remaining,
            RemainingExpectedDays = remainingExpected is null ? null : JsMath.Round2(remainingExpected.Value),
            HoldDays = holdDays,
            VarianceDays = started && planned is not null ? JsMath.Round2(actual - planned.Value) : null,
            UtilizationPct = utilizationPct is null ? null : JsMath.Round2(utilizationPct.Value),
            SlaState = ClassifyHealth(utilizationPct, settings),
            StartedAt = stage.StartedAt,
            CompletedAt = stage.CompletedAt,
            PausedAt = stage.PausedAt,
            ResumedAt = stage.ResumeAt,
            ReopenCount = stage.ReopenCount,
            IsBackfilled = stage.IsBackfilled,
            ExpectedCompletionDate = expectedCompletionDate,
        };
    }

    /// <summary>
    /// The stage the request is "at": the earliest stage that is In Progress / On Hold, else the earliest unfinished
    /// one; null when every stage is Completed/Cancelled (request finished).
    /// </summary>
    public static StageRecord? DeriveCurrentStage(IEnumerable<StageRecord> stages)
    {
        var sorted = stages.OrderBy(s => s.StageOrder).ThenBy(s => s.Id).ToList();
        return sorted.FirstOrDefault(s => s.Status is StageStatus.InProgress or StageStatus.OnHold)
            ?? sorted.FirstOrDefault(s => s.Status is not (StageStatus.Completed or StageStatus.Cancelled));
    }

    /// <summary>
    /// Request-level roll-up of <see cref="CalculateStageSla"/> results.
    /// Backfilled stages are excluded from all totals; a cancelled stage contributes only the effort already spent;
    /// health = worst of the overall utilization and every individual stage's health.
    /// </summary>
    public static RequestSlaSummary SummarizeRequest(IReadOnlyList<StageMetrics> stageMetrics, EngineSettings settings, SlaCalendar calendar, DateTime now)
    {
        var tracked = stageMetrics.Where(m => !m.IsBackfilled).ToList();
        double planned = 0, expected = 0, consumed = 0, remaining = 0, hold = 0, remainingExpected = 0;
        var unestimated = 0;
        foreach (var m in tracked)
        {
            hold += m.HoldDays;
            if (m.Status == StageStatus.NotYetEstimated) { unestimated++; continue; }
            consumed += m.ActualEffort;
            if (m.Status == StageStatus.Cancelled) { planned += m.ActualEffort; expected += m.ActualEffort; continue; }
            planned += m.PlannedEffort ?? 0;
            expected += m.ExpectedSlaDays;
            remaining += m.Remaining ?? 0;
            remainingExpected += m.RemainingExpectedDays ?? 0;
        }
        double? utilizationPct = expected > 0 ? consumed / expected * 100 : null;
        var pending = stageMetrics.Where(m => m.Status is not (StageStatus.Completed or StageStatus.Cancelled)).ToList();
        var lastCompleted = stageMetrics.Where(m => m.CompletedAt is not null).OrderByDescending(m => m.CompletedAt).FirstOrDefault();

        var healthCandidates = new List<string?> { ClassifyHealth(utilizationPct, settings) };
        healthCandidates.AddRange(tracked.Where(m => m.Status != StageStatus.Cancelled).Select(m => m.SlaState));

        return new RequestSlaSummary
        {
            TotalPlannedDays = JsMath.Round2(planned),
            TotalExpectedDays = JsMath.Round2(expected),
            TotalConsumedDays = JsMath.Round2(consumed),
            TotalRemainingDays = JsMath.Round2(remaining),
            TotalHoldDays = JsMath.Round2(hold),
            VarianceDays = JsMath.Round2(consumed - planned),
            UtilizationPct = utilizationPct is null ? null : JsMath.Round2(utilizationPct.Value),
            SlaHealth = WorstHealth(healthCandidates),
            UnestimatedStages = unestimated,
            IsFinished = pending.Count == 0,
            ExpectedCompletionDate = pending.Count > 0
                ? CalculateExpectedCompletionDate(now, remainingExpected, calendar)
                : lastCompleted is not null ? Iso(calendar.DayKey(lastCompleted.CompletedAt!.Value)) : null,
        };
    }

    // ------------------------------------------------------------------ status transitions (state machine)

    /// <summary>Maps a user-supplied status ("on hold", "ON HOLD") to its canonical spelling, or null.</summary>
    public static string? CanonicalStatus(string? s) => StageStatus.All.FirstOrDefault(x => Norm(x) == Norm(s));

    /// <summary>
    /// Decides whether <paramref name="stage"/> may move to <paramref name="toStatus"/> and which action that is.
    /// Pure: nothing is persisted here. <paramref name="force"/> skips the stage-order and single-active-stage rules
    /// (used when reconciling legacy edits).
    /// </summary>
    public static TransitionPlan PlanTransition(
        StageRecord stage,
        string toStatus,
        IReadOnlyList<StageRecord> stages,
        double? plannedEffort,
        EngineSettings settings,
        bool force = false)
    {
        var from = stage.Status;
        var errors = new List<string>();
        var estimate = plannedEffort ?? stage.StageEffortDays;
        var estimated = estimate > 0;
        var estimateChanged = plannedEffort is not null && plannedEffort != stage.StageEffortDays;
        // Estimating a not-yet-estimated stage makes it Not Started.
        if (from == StageStatus.NotYetEstimated && toStatus == from && plannedEffort > 0) toStatus = StageStatus.NotStarted;

        if (from == toStatus)
        {
            return new TransitionPlan(estimateChanged ? SlaAction.Estimate : SlaAction.Noop, toStatus, errors,
                estimateChanged ? estimate : stage.StageEffortDays);
        }
        if (!Allowed.TryGetValue(from, out var allowed) || !allowed.Contains(toStatus))
        {
            errors.Add($"Cannot change \"{stage.StageName}\" from {from} to {toStatus}.");
            return new TransitionPlan(null, toStatus, errors, estimate);
        }
        if ((toStatus == StageStatus.NotStarted || toStatus == StageStatus.InProgress) && !estimated)
        {
            errors.Add($"\"{stage.StageName}\" has no planned effort: send plannedEffort (man-days, above 0) before it can be {(toStatus == StageStatus.InProgress ? "started" : "marked Not Started")}.");
        }
        if (toStatus == StageStatus.NotYetEstimated && plannedEffort > 0)
        {
            errors.Add("A stage with a planned effort cannot be set to Not Yet Estimated.");
        }
        if (toStatus == StageStatus.InProgress && !force)
        {
            var active = stages.FirstOrDefault(s => s.Id != stage.Id && s.Status == StageStatus.InProgress);
            if (active is not null) errors.Add($"\"{active.StageName}\" is already In Progress: put it On Hold or complete it first.");
            if (settings.EnforceStageOrder && from != StageStatus.OnHold)
            {
                var unfinished = stages.Where(s => s.StageOrder < stage.StageOrder && s.Status is not (StageStatus.Completed or StageStatus.Cancelled)).ToList();
                if (unfinished.Count > 0)
                {
                    errors.Add($"Earlier stage(s) must be Completed or Cancelled first: {string.Join(", ", unfinished.Select(s => s.StageName))}.");
                }
            }
        }

        string action;
        if (toStatus == StageStatus.InProgress) action = from == StageStatus.OnHold ? SlaAction.Resume : from == StageStatus.Completed ? SlaAction.Reopen : SlaAction.Start;
        else if (toStatus == StageStatus.OnHold) action = SlaAction.Pause;
        else if (toStatus == StageStatus.Completed) action = SlaAction.Complete;
        else if (toStatus == StageStatus.Cancelled) action = SlaAction.Cancel;
        else if (toStatus == StageStatus.NotYetEstimated) action = SlaAction.Unestimate;
        else action = from == StageStatus.Cancelled ? SlaAction.Reinstate : SlaAction.Estimate;

        return new TransitionPlan(action, toStatus, errors, toStatus == StageStatus.NotYetEstimated ? null : estimate);
    }

    // ------------------------------------------------------------------ initial state for requests that pre-date the engine

    /// <summary>
    /// Interprets a legacy request status (free lookup name + its configured Active/Paused/Done state):
    /// <c>idle</c> | <c>active</c> | <c>hold</c> | <c>done</c> | <c>cancelled</c>.
    /// </summary>
    public static string ClassifyLegacyStatus(string? name, string? state)
    {
        var n = Norm(name);
        if (n is "cancelled" or "canceled") return "cancelled";
        if (state == "Done") return "done";
        if (state == "Paused" || n is "on hold" or "hold") return "hold";
        if (n is "not started" or "not yet estimated" or "not estimated" or "") return "idle";
        return "active";
    }

    /// <summary>
    /// Builds the initial request_stages for a request from its legacy stage/status. Stages before the current one are
    /// Completed (backfilled); the current stage takes the status the legacy status implies; later stages are
    /// Not Started (or Not Yet Estimated without a default effort).
    /// </summary>
    public static List<InitialStagePlan> PlanInitialStages(IEnumerable<SlaStageDefinition> pipeline, SlaStageDefinition? legacyStage, string intent)
    {
        var sorted = pipeline.OrderBy(s => s.StageOrder).ToList();
        var curOrder = legacyStage?.StageOrder ?? 0;
        var cur = sorted.FindIndex(s => s.StageOrder >= curOrder);
        if (cur == -1) cur = sorted.Count; // past the end of the pipeline, e.g. "Closed"

        var result = new List<InitialStagePlan>();
        for (var i = 0; i < sorted.Count; i++)
        {
            var s = sorted[i];
            double? planned = s.EffortDays > 0 ? s.EffortDays : null;
            InitialStagePlan Make(string status, bool backfilled) => new(s.Name, s.StageOrder, planned, status, backfilled);

            if (i < cur) result.Add(Make(StageStatus.Completed, true));
            else if (intent == "done") result.Add(Make(StageStatus.Completed, true));
            else if (intent == "cancelled") result.Add(Make(StageStatus.Cancelled, true));
            else if (planned is null) result.Add(Make(StageStatus.NotYetEstimated, false));
            else if (i == cur && intent == "active") result.Add(Make(StageStatus.InProgress, false));
            else if (i == cur && intent == "hold") result.Add(Make(StageStatus.OnHold, false));
            else result.Add(Make(StageStatus.NotStarted, false));
        }
        return result;
    }

    // ------------------------------------------------------------------ reconciling tracked stages with the configured pipeline

    /// <summary>
    /// visible: every stage still in the pipeline, plus removed stages that carry real history;
    /// hidden: removed stages that were never touched (ignored, never deleted).
    /// </summary>
    public static (List<StageRecord> Visible, List<StageRecord> Hidden) PartitionStages(IEnumerable<StageRecord> stages, IReadOnlySet<string> pipelineNames)
    {
        var visible = new List<StageRecord>();
        var hidden = new List<StageRecord>();
        foreach (var s in stages)
        {
            var inPipeline = pipelineNames.Contains(Norm(s.StageName));
            var untouched = s.IsBackfilled || s.Status == StageStatus.NotStarted || s.Status == StageStatus.NotYetEstimated;
            (inPipeline || !untouched ? visible : hidden).Add(s);
        }
        return (visible, hidden);
    }

    /// <summary>A removed stage that is still running (In Progress / On Hold) blocks the stage that replaced it.</summary>
    public static bool HasStaleActiveStage(IEnumerable<StageRecord> stages, IReadOnlySet<string> pipelineNames) =>
        stages.Any(s => !pipelineNames.Contains(Norm(s.StageName)) && s.Status is StageStatus.InProgress or StageStatus.OnHold);

    // ------------------------------------------------------------------ input validation for stage-status changes

    public static (List<string> Errors, CleanStageStatusInput Clean) ValidateStageStatusInput(StageStatusInput? body)
    {
        var errors = new List<string>();
        var b = body ?? new StageStatusInput();
        var stage = (b.Stage ?? string.Empty).Trim();
        if (stage.Length == 0) errors.Add("stage is required.");
        else if (stage.Length > 200) errors.Add("stage must be at most 200 characters.");

        var status = CanonicalStatus(b.Status);
        if (string.IsNullOrEmpty(b.Status)) errors.Add("status is required.");
        else if (status is null) errors.Add($"status must be one of: {string.Join(", ", StageStatus.All)}.");

        double? OptionalNumber(string? raw, string label, double max, bool positive)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            var v = JsMath.ToNumber(raw);
            if (!double.IsFinite(v) || v > max || (positive ? v <= 0 : v < 0))
            {
                errors.Add($"{label} must be a number {(positive ? "above" : "of at least")} 0 and at most {JsMath.ToJsString(max)}.");
                return null;
            }
            return JsMath.Round2(v);
        }
        var plannedEffort = OptionalNumber(b.PlannedEffort, "plannedEffort", 999, positive: true);
        var actualEffortDays = OptionalNumber(b.ActualEffortDays, "actualEffortDays", 9999, positive: false);
        if (actualEffortDays is not null && status != StageStatus.Completed) errors.Add("actualEffortDays can only be sent together with status Completed.");

        string? note = null;
        if (!string.IsNullOrEmpty(b.Note))
        {
            note = b.Note.Trim();
            if (note.Length > 500) errors.Add("note must be at most 500 characters.");
        }
        return (errors, new CleanStageStatusInput(stage, status, plannedEffort, actualEffortDays, note));
    }
}
