namespace ItrmTracker.Domain.Constants;

/// <summary>Privilege levels stored in dbo.users.role.</summary>
public static class AppRoles
{
    public const string ReadOnly = "read_only";
    public const string ReadWrite = "read_write";
    public static readonly IReadOnlyList<string> All = new[] { ReadOnly, ReadWrite };
    public static bool IsValid(string? role) => role == ReadOnly || role == ReadWrite;
}

/// <summary>Authorization policy names.</summary>
public static class Policies
{
    /// <summary>Requires the read_write role (every mutating operation except the documented exemptions).</summary>
    public const string ReadWrite = "ReadWrite";
}

/// <summary>Stage statuses of the stage-driven SLA engine (dbo.request_stages.status).</summary>
public static class StageStatus
{
    public const string NotYetEstimated = "Not Yet Estimated";
    public const string NotStarted = "Not Started";
    public const string InProgress = "In Progress";
    public const string OnHold = "On Hold";
    public const string Completed = "Completed";
    public const string Cancelled = "Cancelled";

    public static readonly IReadOnlyList<string> All = new[] { NotYetEstimated, NotStarted, InProgress, OnHold, Completed, Cancelled };
}

/// <summary>SLA health classification.</summary>
public static class SlaHealth
{
    public const string OnTrack = "On Track";
    public const string AtRisk = "At Risk";
    public const string Breached = "Breached";

    public static int Rank(string? health) => health switch
    {
        OnTrack => 0,
        AtRisk => 1,
        Breached => 2,
        _ => -1,
    };
}

/// <summary>Transition actions recorded in request_stage_status_history.action.</summary>
public static class SlaAction
{
    public const string Noop = "noop";
    public const string Init = "init";
    public const string Estimate = "estimate";
    public const string Unestimate = "unestimate";
    public const string Start = "start";
    public const string Pause = "pause";
    public const string Resume = "resume";
    public const string Complete = "complete";
    public const string Cancel = "cancel";
    public const string Reopen = "reopen";
    public const string Reinstate = "reinstate";
}

/// <summary>Legacy SLA status flags shown in the "SLA Status" column (lib/sla.js FLAGS).</summary>
public static class SlaFlags
{
    public const string Done = "Closed / done";
    public const string Paused = "Paused - clock stopped";
    public const string NoPlan = "No plan - use SLA date";
    public const string Passed = "Planned date passed - re-baseline";
    public const string AtRisk = "Plan earlier than SLA - at risk";
    public const string Ok = "Plan within SLA";

    /// <summary>Display order used by the SLA filter (same as the original UI).</summary>
    public static readonly IReadOnlyList<string> All = new[] { AtRisk, Passed, NoPlan, Ok, Paused, Done };
}

/// <summary>Business-level constants shared by several screens.</summary>
public static class TrackerRules
{
    /// <summary>Open records with no update for this many days are "stale".</summary>
    public const int StaleThresholdDays = 14;

    /// <summary>Follow-up screen: due within this many days.</summary>
    public const int FollowUpThresholdDays = 5;

    /// <summary>Earliest/latest accepted dates (same as the Excel data validation).</summary>
    public static readonly DateOnly MinDate = new(2020, 1, 1);
    public static readonly DateOnly MaxDate = new(2035, 12, 31);

    /// <summary>
    /// Statuses the UI treats as closed (case-insensitive). Note: the server-side legacy SLA calculation
    /// uses its own list (it also includes "closed"); both are preserved exactly as in the original app.
    /// </summary>
    public static readonly IReadOnlySet<string> ClosedStatuses =
        new HashSet<string>(StringComparer.Ordinal) { "compeleted", "completed", "cancelled", "canceled" };

    public static bool IsDoneStatus(string? status) => ClosedStatuses.Contains((status ?? string.Empty).Trim().ToLowerInvariant());

    public const int CommentMaxLength = 2000;
}
