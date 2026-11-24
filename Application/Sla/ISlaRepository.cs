using ItrmTracker.Domain.Entities;

namespace ItrmTracker.Application.Sla;

/// <summary>Request header fields the SLA engine needs.</summary>
public sealed class SlaRequestInfo
{
    public int Id { get; set; }
    public string? BrNo { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Priority { get; set; }
    public string? Stage { get; set; }
    public string? Status { get; set; }
}

/// <summary>Append-only status-change audit row to insert.</summary>
public sealed record StatusHistoryEntry(
    int RequestStageId, int BusinessRequestId, string StageName, string? FromStatus, string ToStatus, string Action, string Source,
    double? PlannedEffortDays, double? ConsumedDays, double? RemainingDays, string? Note, string? ChangedBy, DateTime ChangedAt);

/// <summary>Stage-movement audit row to insert.</summary>
public sealed record StageHistoryEntry(
    int BusinessRequestId, string? FromStage, string ToStage, string Reason, string Source, string? ChangedBy, DateTime ChangedAt);

/// <summary>
/// Data access for the SLA engine. Every write performed inside <see cref="InTransactionAsync{T}"/> runs on the same
/// database transaction; nested calls run inline. Port of <c>lib/sla-repository.js</c>.
/// </summary>
public interface ISlaRepository
{
    Task<T> InTransactionAsync<T>(Func<Task<T>> work, CancellationToken ct = default);

    /// <summary><paramref name="lockRow"/> takes an UPDLOCK/HOLDLOCK on the request row so concurrent changes to one request serialize.</summary>
    Task<SlaRequestInfo?> GetRequestAsync(int id, bool lockRow, CancellationToken ct = default);
    Task<List<SlaRequestInfo>> ListTrackedRequestsAsync(CancellationToken ct = default);
    Task<List<int>> ListRequestIdsWithoutTrackingAsync(CancellationToken ct = default);
    Task<List<int>> ListRequestIdsWithStaleActiveStagesAsync(IReadOnlyCollection<string> pipelineNames, CancellationToken ct = default);
    Task<bool> HasUserActivityAsync(int requestId, CancellationToken ct = default);
    Task DeleteTrackingAsync(int requestId, CancellationToken ct = default);

    Task<List<StageRecord>> ListStagesAsync(int requestId, CancellationToken ct = default);
    Task<List<StageRecord>> ListAllStagesAsync(CancellationToken ct = default);
    Task<int> InsertStageAsync(StageRecord stage, CancellationToken ct = default);
    Task UpdateStageAsync(StageRecord stage, CancellationToken ct = default);

    Task<List<WorkSegment>> ListWorkAsync(int requestId, CancellationToken ct = default);
    Task<List<WorkSegment>> ListAllWorkAsync(CancellationToken ct = default);
    Task OpenWorkAsync(WorkSegment segment, CancellationToken ct = default);
    Task CloseWorkAsync(int id, DateTime endedAt, string reason, double workingDays, CancellationToken ct = default);

    Task<List<PauseRecord>> ListPausesAsync(int requestId, CancellationToken ct = default);
    Task<List<PauseRecord>> ListAllPausesAsync(CancellationToken ct = default);
    Task OpenPauseAsync(PauseRecord pause, CancellationToken ct = default);
    Task ClosePauseAsync(int id, DateTime resumedAt, double holdDays, string? resumedBy, CancellationToken ct = default);

    Task InsertStatusHistoryAsync(StatusHistoryEntry entry, CancellationToken ct = default);
    Task InsertStageHistoryAsync(StageHistoryEntry entry, CancellationToken ct = default);
    Task<List<RequestStageStatusHistory>> ListStatusHistoryAsync(int requestId, CancellationToken ct = default);
    Task<List<RequestStageHistory>> ListStageHistoryAsync(int requestId, CancellationToken ct = default);

    /// <summary>Mirrors the engine state into the legacy stage_id/status_id (names not in the lookups are ignored).</summary>
    Task SetLegacyFieldsAsync(int requestId, string? stageName, string? statusName, string? user, DateTime now, CancellationToken ct = default);
}
