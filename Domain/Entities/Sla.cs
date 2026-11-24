namespace ItrmTracker.Domain.Entities;

// ---------------------------------------------------------------------------
// SLA assumption tables. Keyed by lookup NAME (not id) so rows survive lookup
// renames; a lookup without a stored row falls back to built-in defaults.
// ---------------------------------------------------------------------------

/// <summary>dbo.sla_stages</summary>
public class SlaStageConfig
{
    public string Name { get; set; } = string.Empty;
    public int StageOrder { get; set; }
    public bool IsPipeline { get; set; } = true;
    public int BrDays { get; set; }
    public int IncidentDays { get; set; }
    public decimal EffortDays { get; set; }
}

/// <summary>dbo.sla_priorities</summary>
public class SlaPriorityConfig
{
    public string Name { get; set; } = string.Empty;
    public decimal Factor { get; set; }
    public decimal ResponseHours { get; set; } = 8;
    public int UpdateDays { get; set; } = 7;
}

/// <summary>dbo.sla_categories</summary>
public class SlaCategoryConfig
{
    public string Name { get; set; } = string.Empty;
    public decimal Factor { get; set; }
}

/// <summary>dbo.sla_statuses – maps a status lookup name to Active / Paused / Done.</summary>
public class SlaStatusConfig
{
    public string Name { get; set; } = string.Empty;
    public string State { get; set; } = "Active";
}

/// <summary>dbo.sla_settings – key/value engine settings (weekend_days, thresholds, ...).</summary>
public class SlaSetting
{
    public string SettingKey { get; set; } = string.Empty;
    public string SettingValue { get; set; } = string.Empty;
}

// ---------------------------------------------------------------------------
// Stage-driven, effort-driven SLA engine tables (migration 001).
// ---------------------------------------------------------------------------

/// <summary>dbo.request_stages – one row per (request, pipeline stage) with cached aggregates.</summary>
public class RequestStage
{
    public int Id { get; set; }
    public int BusinessRequestId { get; set; }
    public string StageName { get; set; } = string.Empty;
    public int StageOrder { get; set; }
    public string Status { get; set; } = "Not Started";
    public decimal? StageEffortDays { get; set; }
    public decimal ActualEffortDays { get; set; }
    public decimal? RemainingEffortDays { get; set; }
    public decimal SlaConsumedDays { get; set; }
    public decimal TotalHoldTime { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? PausedAt { get; set; }
    public DateTime? ResumeAt { get; set; }
    public int ReopenCount { get; set; }
    public bool IsBackfilled { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

/// <summary>dbo.sla_effort_tracking – a stretch of time a stage was In Progress.</summary>
public class SlaEffortTracking
{
    public int Id { get; set; }
    public int RequestStageId { get; set; }
    public int BusinessRequestId { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public string? EndReason { get; set; }
    public decimal? WorkingDays { get; set; }
    public string? CreatedBy { get; set; }
}

/// <summary>dbo.sla_pause_history – every hold period.</summary>
public class SlaPauseHistory
{
    public int Id { get; set; }
    public int RequestStageId { get; set; }
    public int BusinessRequestId { get; set; }
    public DateTime PausedAt { get; set; }
    public DateTime? ResumedAt { get; set; }
    public decimal? HoldDays { get; set; }
    public string? Reason { get; set; }
    public string? PausedBy { get; set; }
    public string? ResumedBy { get; set; }
}

/// <summary>dbo.request_stage_status_history – append-only audit of stage status changes.</summary>
public class RequestStageStatusHistory
{
    public int Id { get; set; }
    public int RequestStageId { get; set; }
    public int BusinessRequestId { get; set; }
    public string StageName { get; set; } = string.Empty;
    public string? FromStatus { get; set; }
    public string ToStatus { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Source { get; set; } = "api";
    public decimal? PlannedEffortDays { get; set; }
    public decimal? ConsumedDays { get; set; }
    public decimal? RemainingDays { get; set; }
    public string? Note { get; set; }
    public string? ChangedBy { get; set; }
    public DateTime ChangedAt { get; set; }
}

/// <summary>dbo.request_stage_history – movement of a request between stages.</summary>
public class RequestStageHistory
{
    public int Id { get; set; }
    public int BusinessRequestId { get; set; }
    public string? FromStage { get; set; }
    public string ToStage { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string Source { get; set; } = "api";
    public string? ChangedBy { get; set; }
    public DateTime ChangedAt { get; set; }
}
