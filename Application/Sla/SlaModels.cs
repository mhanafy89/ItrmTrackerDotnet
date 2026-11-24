using ItrmTracker.Domain.Constants;

namespace ItrmTracker.Application.Sla;

/// <summary>Engine settings stored in dbo.sla_settings (parsed leniently, see <see cref="SlaEngine.ParseEngineSettings"/>).</summary>
public sealed record EngineSettings(
    double AtRiskThresholdPct,
    double BreachThresholdPct,
    string PriorityFactorMode,
    bool EnforceStageOrder,
    bool AutoStartNextStage)
{
    public static readonly EngineSettings Default = new(80, 100, "multiplier", true, false);
}

/// <summary>Weekend days (0 = Sunday … 6 = Saturday) and the time zone that defines a calendar day.</summary>
public sealed class SlaCalendar
{
    public SlaCalendar(IEnumerable<int>? weekendDays, TimeZoneInfo zone)
    {
        var set = new HashSet<int>(weekendDays ?? SlaEngine.DefaultWeekend);
        // A weekend covering all seven days would make every day non-working; treat it as "no weekend".
        Weekend = set.Count >= 7 ? new HashSet<int>() : set;
        Zone = zone;
    }

    public IReadOnlySet<int> Weekend { get; }
    public TimeZoneInfo Zone { get; }

    public bool IsWeekend(DateOnly day) => Weekend.Contains((int)day.DayOfWeek);

    /// <summary>Calendar day of a UTC instant in the business time zone (the engine's <c>dayKey</c>).</summary>
    public DateOnly DayKey(DateTime utc) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone));
}

/// <summary>Mutable in-memory view of a dbo.request_stages row used by the engine and the service.</summary>
public sealed class StageRecord
{
    public int Id { get; set; }
    public int BusinessRequestId { get; set; }
    public string StageName { get; set; } = string.Empty;
    public int StageOrder { get; set; }
    public string Status { get; set; } = StageStatus.NotStarted;
    public double? StageEffortDays { get; set; }
    public double ActualEffortDays { get; set; }
    public double? RemainingEffortDays { get; set; }
    public double SlaConsumedDays { get; set; }
    public double TotalHoldTime { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? PausedAt { get; set; }
    public DateTime? ResumeAt { get; set; }
    public int ReopenCount { get; set; }
    public bool IsBackfilled { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

/// <summary>A stretch of time a stage was In Progress (dbo.sla_effort_tracking).</summary>
public sealed class WorkSegment
{
    public int Id { get; set; }
    public int RequestStageId { get; set; }
    public int BusinessRequestId { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public string? EndReason { get; set; }
    public double? WorkingDays { get; set; }
    public string? CreatedBy { get; set; }
}

/// <summary>A hold period (dbo.sla_pause_history).</summary>
public sealed class PauseRecord
{
    public int Id { get; set; }
    public int RequestStageId { get; set; }
    public int BusinessRequestId { get; set; }
    public DateTime PausedAt { get; set; }
    public DateTime? ResumedAt { get; set; }
    public double? HoldDays { get; set; }
    public string? Reason { get; set; }
    public string? PausedBy { get; set; }
    public string? ResumedBy { get; set; }
}

/// <summary>Every metric of one stage (output of <see cref="SlaEngine.CalculateStageSla"/>).</summary>
public sealed class StageMetrics
{
    public string Stage { get; init; } = string.Empty;
    public int Order { get; init; }
    public string Status { get; init; } = string.Empty;
    public double? PlannedEffort { get; init; }
    public double ExpectedSlaDays { get; init; }
    public double ConsumedDays { get; init; }
    public double ActualEffort { get; init; }
    public double? Remaining { get; init; }
    public double? RemainingExpectedDays { get; init; }
    public double HoldDays { get; init; }
    public double? VarianceDays { get; init; }
    public double? UtilizationPct { get; init; }
    public string? SlaState { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public DateTime? PausedAt { get; init; }
    public DateTime? ResumedAt { get; init; }
    public int ReopenCount { get; init; }
    public bool IsBackfilled { get; init; }
    public string? ExpectedCompletionDate { get; init; }
}

/// <summary>Request-level roll-up (output of <see cref="SlaEngine.SummarizeRequest"/>).</summary>
public sealed class RequestSlaSummary
{
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

/// <summary>Decision returned by <see cref="SlaEngine.PlanTransition"/>; non-empty <see cref="Errors"/> means rejected.</summary>
public sealed record TransitionPlan(string? Action, string ToStatus, IReadOnlyList<string> Errors, double? PlannedEffort);

/// <summary>Initial stage planned for a request that pre-dates the engine.</summary>
public sealed record InitialStagePlan(string StageName, int StageOrder, double? StageEffortDays, string Status, bool IsBackfilled);

/// <summary>Raw (string) input of a stage status change, validated by <see cref="SlaEngine.ValidateStageStatusInput"/>.</summary>
public sealed class StageStatusInput
{
    public string? Stage { get; set; }
    public string? Status { get; set; }
    public string? PlannedEffort { get; set; }
    public string? ActualEffortDays { get; set; }
    public string? Note { get; set; }
}

/// <summary>Validated stage status change.</summary>
public sealed record CleanStageStatusInput(string Stage, string? Status, double? PlannedEffort, double? ActualEffortDays, string? Note);
