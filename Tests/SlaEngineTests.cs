using ItrmTracker.Application.Sla;
using ItrmTracker.Domain.Constants;

namespace ItrmTracker.Tests;

/// <summary>Port of test/sla-engine.test.js (same scenarios, same expected values).</summary>
public class SlaEngineTests
{
    // Local-time dates. 2026-11-23 is a Monday.
    private static DateTime At(int day, int hour = 9) => new DateTime(2026, 11, day, hour, 0, 0, DateTimeKind.Local).ToUniversalTime();
    private static DateTime Local(int y, int m, int d, int h = 0) => new DateTime(y, m, d, h, 0, 0, DateTimeKind.Local).ToUniversalTime();
    private static readonly SlaCalendar FriSat = new(new[] { 5, 6 }, TimeZoneInfo.Local);
    private static readonly SlaCalendar SatSun = new(new[] { 0, 6 }, TimeZoneInfo.Local);
    private static EngineSettings Settings(string? mode = null, string? atRisk = null, string? breach = null, string? enforce = null) =>
        SlaEngine.ParseEngineSettings(new Dictionary<string, string?>
        {
            ["priority_factor_mode"] = mode, ["at_risk_threshold_pct"] = atRisk, ["breach_threshold_pct"] = breach, ["enforce_stage_order"] = enforce,
        });

    private static StageRecord StageRow(Action<StageRecord>? over = null)
    {
        var s = new StageRecord { Id = 1, StageName = "Development", StageOrder = 3, Status = StageStatus.InProgress, StageEffortDays = 10, ActualEffortDays = 0, StartedAt = At(23) };
        over?.Invoke(s);
        return s;
    }

    [Fact]
    public void WorkingDaysBetween_excludes_the_weekend_and_the_start_day()
    {
        Assert.Equal(3, SlaEngine.WorkingDaysBetween(At(23), At(27), FriSat));
        Assert.Equal(4, SlaEngine.WorkingDaysBetween(At(23), At(27), SatSun));
        Assert.Equal(5, SlaEngine.WorkingDaysBetween(At(23), At(30), SatSun));
        Assert.Equal(0, SlaEngine.WorkingDaysBetween(At(23, 9), At(23, 17), SatSun));
        Assert.Equal(0, SlaEngine.WorkingDaysBetween(At(27), At(23), SatSun));
    }

    [Fact]
    public void AddWorkingDays_skips_weekends_and_rounds_fractions_up()
    {
        var start = new DateOnly(2026, 11, 23);
        Assert.Equal("2026-11-30", SlaEngine.AddWorkingDays(start, 5, SatSun));
        Assert.Equal("2026-11-30", SlaEngine.AddWorkingDays(start, 5, FriSat));
        Assert.Equal("2026-12-02", SlaEngine.AddWorkingDays(start, 6.67, SatSun));
        Assert.Equal("2026-11-23", SlaEngine.AddWorkingDays(start, 0, SatSun));
    }

    [Fact]
    public void A_weekend_covering_every_day_is_treated_as_no_weekend()
    {
        Assert.Equal("2026-11-25", SlaEngine.AddWorkingDays(new DateOnly(2026, 11, 23), 2, new SlaCalendar(new[] { 0, 1, 2, 3, 4, 5, 6 }, TimeZoneInfo.Local)));
    }

    [Fact]
    public void Priority_adjusts_the_target_only()
    {
        Assert.Equal(10, Round(SlaEngine.CalculateExpectedSlaDays(10, 1, "divisor")));
        Assert.Equal(6.67, Round(SlaEngine.CalculateExpectedSlaDays(10, 1.5, "divisor")));
        Assert.Equal(13.33, Round(SlaEngine.CalculateExpectedSlaDays(10, 0.75, "divisor")));
        Assert.Equal(4, Round(SlaEngine.CalculateExpectedSlaDays(10, 0.4, "multiplier")));
        Assert.Equal(0, SlaEngine.CalculateExpectedSlaDays(null, 1.5, "divisor"));
        Assert.Equal(10, SlaEngine.CalculateExpectedSlaDays(10, 0, "divisor")); // invalid factor ignored
        static double Round(double v) => Application.Common.JsMath.Round2(v);
    }

    [Fact]
    public void Consumed_effort_sums_work_segments_and_ignores_holds()
    {
        var work = new List<WorkSegment>
        {
            new() { StartedAt = At(23), EndedAt = At(25) },
            new() { StartedAt = At(30), EndedAt = Local(2026, 12, 2, 9) },
            new() { StartedAt = Local(2026, 12, 2, 9), EndedAt = null },
        };
        Assert.Equal(5, SlaEngine.CalculateConsumedEffort(work, Local(2026, 12, 3, 10), SatSun));
    }

    [Fact]
    public void Hold_days_accumulate_across_cycles_including_an_open_hold()
    {
        var pauses = new List<PauseRecord>
        {
            new() { PausedAt = At(24), ResumedAt = At(26) },
            new() { PausedAt = Local(2026, 12, 1), ResumedAt = null },
        };
        Assert.Equal(4, SlaEngine.CalculateHoldDays(pauses, Local(2026, 12, 3, 10), SatSun));
    }

    [Fact]
    public void Remaining_effort_is_unknown_until_estimated_and_zero_once_finished()
    {
        Assert.Null(SlaEngine.CalculateRemainingEffort(StageStatus.NotYetEstimated, null, 0));
        Assert.Equal(15, SlaEngine.CalculateRemainingEffort(StageStatus.NotStarted, 15, 0));
        Assert.Equal(11, SlaEngine.CalculateRemainingEffort(StageStatus.InProgress, 15, 4));
        Assert.Equal(0, SlaEngine.CalculateRemainingEffort(StageStatus.InProgress, 5, 8));
        Assert.Equal(0, SlaEngine.CalculateRemainingEffort(StageStatus.Completed, 15, 4));
        Assert.Equal(0, SlaEngine.CalculateRemainingEffort(StageStatus.Cancelled, 15, 4));
    }

    [Fact]
    public void Health_thresholds_are_configurable()
    {
        var s = EngineSettings.Default;
        Assert.Equal(SlaHealth.OnTrack, SlaEngine.ClassifyHealth(0, s));
        Assert.Equal(SlaHealth.OnTrack, SlaEngine.ClassifyHealth(80, s));
        Assert.Equal(SlaHealth.AtRisk, SlaEngine.ClassifyHealth(80.01, s));
        Assert.Equal(SlaHealth.AtRisk, SlaEngine.ClassifyHealth(100, s));
        Assert.Equal(SlaHealth.Breached, SlaEngine.ClassifyHealth(100.01, s));
        Assert.Null(SlaEngine.ClassifyHealth(null, s));
        var strict = Settings(atRisk: "60", breach: "90");
        Assert.Equal(SlaHealth.AtRisk, SlaEngine.ClassifyHealth(70, strict));
        Assert.Equal(SlaHealth.Breached, SlaEngine.ClassifyHealth(95, strict));
    }

    [Fact]
    public void Engine_settings_parse_leniently_and_validate_strictly()
    {
        Assert.Equal(EngineSettings.Default, SlaEngine.ParseEngineSettings(new Dictionary<string, string?>()));
        Assert.Equal("divisor", Settings(mode: "DIVISOR").PriorityFactorMode);
        Assert.False(Settings(enforce: "false").EnforceStageOrder);
        Assert.Equal(80, Settings(atRisk: "abc").AtRiskThresholdPct);

        Assert.Empty(SlaEngine.ValidateEngineSettings(new Dictionary<string, string?>()).Errors);
        var ok = SlaEngine.ValidateEngineSettings(new Dictionary<string, string?> { ["at_risk_threshold_pct"] = "70", ["breach_threshold_pct"] = "95" });
        Assert.Equal("70", ok.Clean["at_risk_threshold_pct"]);
        Assert.Equal("95", ok.Clean["breach_threshold_pct"]);
        Assert.Single(SlaEngine.ValidateEngineSettings(new Dictionary<string, string?> { ["at_risk_threshold_pct"] = "120", ["breach_threshold_pct"] = "100" }).Errors);
        Assert.Single(SlaEngine.ValidateEngineSettings(new Dictionary<string, string?> { ["priority_factor_mode"] = "x" }).Errors);
        Assert.Single(SlaEngine.ValidateEngineSettings(new Dictionary<string, string?> { ["enforce_stage_order"] = "maybe" }).Errors);
    }

    [Fact]
    public void Stage_sla_excludes_hold_time_and_priority_never_changes_consumption()
    {
        var now = Local(2026, 11, 30, 12);
        var work = new List<WorkSegment> { new() { StartedAt = At(23), EndedAt = At(25) }, new() { StartedAt = At(29), EndedAt = null } };
        var pauses = new List<PauseRecord> { new() { PausedAt = At(25), ResumedAt = At(29) } };
        var divisor = Settings(mode: "divisor");
        var medium = SlaEngine.CalculateStageSla(StageRow(), work, pauses, 1, divisor, SatSun, now);
        var critical = SlaEngine.CalculateStageSla(StageRow(), work, pauses, 1.5, divisor, SatSun, now);

        Assert.Equal(3, medium.ConsumedDays);
        Assert.Equal(3, critical.ConsumedDays);
        Assert.Equal(2, medium.HoldDays);
        Assert.Equal(10, medium.ExpectedSlaDays);
        Assert.Equal(6.67, critical.ExpectedSlaDays);
        Assert.Equal(7, medium.Remaining);
        Assert.Equal(7, critical.Remaining);
        Assert.Equal(30, medium.UtilizationPct);
        Assert.Equal(45, critical.UtilizationPct);
        Assert.Equal(SlaHealth.OnTrack, medium.SlaState);
        Assert.Equal(-7, medium.VarianceDays);
        Assert.Equal("2026-12-09", medium.ExpectedCompletionDate);
        Assert.Equal("2026-12-04", critical.ExpectedCompletionDate);
    }

    [Fact]
    public void Not_started_stages_never_consume_and_completed_stages_keep_the_stored_actual()
    {
        var none = new List<WorkSegment>();
        var notStarted = SlaEngine.CalculateStageSla(StageRow(s => { s.Status = StageStatus.NotStarted; s.StartedAt = null; }), none, new List<PauseRecord>(), 1, EngineSettings.Default, SatSun, At(30));
        Assert.Equal(0, notStarted.ConsumedDays);
        Assert.Equal(10, notStarted.Remaining);
        Assert.Null(notStarted.VarianceDays);

        var completed = SlaEngine.CalculateStageSla(StageRow(s => { s.Status = StageStatus.Completed; s.ActualEffortDays = 12; s.CompletedAt = At(27); }),
            new List<WorkSegment> { new() { StartedAt = At(23), EndedAt = At(27) } }, new List<PauseRecord>(), 1, EngineSettings.Default, SatSun, At(30));
        Assert.Equal(12, completed.ActualEffort);
        Assert.Equal(0, completed.Remaining);
        Assert.Equal(2, completed.VarianceDays);
        Assert.Equal("2026-11-27", completed.ExpectedCompletionDate);

        var nye = SlaEngine.CalculateStageSla(StageRow(s => { s.Status = StageStatus.NotYetEstimated; s.StageEffortDays = null; }), none, new List<PauseRecord>(), 1, EngineSettings.Default, SatSun, At(30));
        Assert.Null(nye.Remaining);
        Assert.Null(nye.UtilizationPct);
        Assert.Null(nye.SlaState);
    }

    [Fact]
    public void DeriveCurrentStage_picks_the_earliest_active_then_first_unfinished_then_null()
    {
        StageRecord S(int id, int order, string status) => new() { Id = id, StageName = $"S{order}", StageOrder = order, Status = status };
        Assert.Equal("S3", SlaEngine.DeriveCurrentStage(new[] { S(1, 1, StageStatus.Completed), S(2, 2, StageStatus.NotStarted), S(3, 3, StageStatus.OnHold) })!.StageName);
        Assert.Equal("S2", SlaEngine.DeriveCurrentStage(new[] { S(1, 1, StageStatus.Completed), S(2, 2, StageStatus.NotStarted), S(3, 3, StageStatus.NotStarted) })!.StageName);
        Assert.Null(SlaEngine.DeriveCurrentStage(new[] { S(1, 1, StageStatus.Completed), S(2, 2, StageStatus.Cancelled) }));
    }

    [Fact]
    public void PlanTransition_maps_allowed_moves_to_actions()
    {
        StageRecord St(string status, double? effort = 5) => new() { Id = 1, StageName = "QA", StageOrder = 1, Status = status, StageEffortDays = effort };
        string? A(string from, string to, double? planned = null) => SlaEngine.PlanTransition(St(from), to, new[] { St(from) }, planned, EngineSettings.Default).Action;
        Assert.Equal(SlaAction.Start, A(StageStatus.NotStarted, StageStatus.InProgress));
        Assert.Equal(SlaAction.Pause, A(StageStatus.InProgress, StageStatus.OnHold));
        Assert.Equal(SlaAction.Resume, A(StageStatus.OnHold, StageStatus.InProgress));
        Assert.Equal(SlaAction.Complete, A(StageStatus.InProgress, StageStatus.Completed));
        Assert.Equal(SlaAction.Cancel, A(StageStatus.InProgress, StageStatus.Cancelled));
        Assert.Equal(SlaAction.Reopen, A(StageStatus.Completed, StageStatus.InProgress));
        Assert.Equal(SlaAction.Reinstate, A(StageStatus.Cancelled, StageStatus.NotStarted));
        Assert.Equal(SlaAction.Estimate, A(StageStatus.NotStarted, StageStatus.NotStarted, 8));
        Assert.Equal(SlaAction.Noop, A(StageStatus.NotStarted, StageStatus.NotStarted));
    }

    [Fact]
    public void PlanTransition_rejects_illegal_moves_and_unestimated_starts()
    {
        var done = new StageRecord { Id = 1, StageName = "QA", StageOrder = 1, Status = StageStatus.Completed, StageEffortDays = 5 };
        Assert.Contains("Cannot change \"QA\" from Completed to On Hold.", SlaEngine.PlanTransition(done, StageStatus.OnHold, new[] { done }, null, EngineSettings.Default).Errors);
        var nye = new StageRecord { Id = 1, StageName = "QA", StageOrder = 1, Status = StageStatus.NotYetEstimated };
        Assert.Single(SlaEngine.PlanTransition(nye, StageStatus.InProgress, new[] { nye }, null, EngineSettings.Default).Errors);
        var estimated = SlaEngine.PlanTransition(nye, StageStatus.NotYetEstimated, new[] { nye }, 4, EngineSettings.Default);
        Assert.Equal(StageStatus.NotStarted, estimated.ToStatus);
        Assert.Empty(estimated.Errors);
    }

    [Fact]
    public void PlanTransition_enforces_stage_order_and_single_active_stage_unless_forced()
    {
        var a = new StageRecord { Id = 1, StageName = "BA", StageOrder = 1, Status = StageStatus.InProgress, StageEffortDays = 3 };
        var b = new StageRecord { Id = 2, StageName = "Dev", StageOrder = 2, Status = StageStatus.NotStarted, StageEffortDays = 5 };
        var stages = new[] { a, b };
        Assert.Equal(2, SlaEngine.PlanTransition(b, StageStatus.InProgress, stages, null, EngineSettings.Default).Errors.Count);
        Assert.Single(SlaEngine.PlanTransition(b, StageStatus.InProgress, stages, null, Settings(enforce: "false")).Errors);
        Assert.Empty(SlaEngine.PlanTransition(b, StageStatus.InProgress, stages, null, EngineSettings.Default, force: true).Errors);
    }

    [Fact]
    public void Legacy_status_classification_and_initial_stage_planning()
    {
        Assert.Equal("cancelled", SlaEngine.ClassifyLegacyStatus("Canceled", "Done"));
        Assert.Equal("done", SlaEngine.ClassifyLegacyStatus("Completed", "Done"));
        Assert.Equal("hold", SlaEngine.ClassifyLegacyStatus("On Hold", "Active"));
        Assert.Equal("idle", SlaEngine.ClassifyLegacyStatus("Not Started", "Active"));
        Assert.Equal("active", SlaEngine.ClassifyLegacyStatus("Pending for Approval", "Active"));

        var pipeline = new[]
        {
            new SlaStageDefinition("BA", 1, true, 0, 0, 5), new SlaStageDefinition("Development", 2, true, 0, 0, 15),
            new SlaStageDefinition("QA", 3, true, 0, 0, 5), new SlaStageDefinition("Deployment", 4, true, 0, 0, 0),
        };
        var plan = SlaEngine.PlanInitialStages(pipeline, pipeline[1], "active");
        Assert.Equal(new[] { StageStatus.Completed, StageStatus.InProgress, StageStatus.NotStarted, StageStatus.NotYetEstimated }, plan.Select(p => p.Status));
        Assert.True(plan[0].IsBackfilled);
        Assert.All(SlaEngine.PlanInitialStages(pipeline, null, "done"), p => Assert.Equal(StageStatus.Completed, p.Status));
    }

    [Fact]
    public void ValidateStageStatusInput_reports_every_problem_with_the_original_messages()
    {
        var (errors, _) = SlaEngine.ValidateStageStatusInput(new StageStatusInput { Stage = "", Status = "done", PlannedEffort = "-1", ActualEffortDays = "3", Note = new string('n', 501) });
        Assert.Contains("stage is required.", errors);
        Assert.Contains("status must be one of: Not Yet Estimated, Not Started, In Progress, On Hold, Completed, Cancelled.", errors);
        Assert.Contains("plannedEffort must be a number above 0 and at most 999.", errors);
        Assert.Contains("actualEffortDays can only be sent together with status Completed.", errors);
        Assert.Contains("note must be at most 500 characters.", errors);

        var (ok, clean) = SlaEngine.ValidateStageStatusInput(new StageStatusInput { Stage = " QA ", Status = "in progress", PlannedEffort = "2.345" });
        Assert.Empty(ok);
        Assert.Equal("QA", clean.Stage);
        Assert.Equal(StageStatus.InProgress, clean.Status);
        Assert.Equal(2.35, clean.PlannedEffort);
    }
}
