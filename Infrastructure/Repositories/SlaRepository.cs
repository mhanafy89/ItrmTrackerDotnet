using ItrmTracker.Application.Sla;
using ItrmTracker.Domain.Constants;
using ItrmTracker.Domain.Entities;
using ItrmTracker.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ItrmTracker.Infrastructure.Repositories;

/// <summary>EF Core implementation of <see cref="ISlaRepository"/> (port of lib/sla-repository.js).</summary>
public sealed class SlaRepository : ISlaRepository
{
    private readonly ItrmDbContext _db;

    public SlaRepository(ItrmDbContext db) => _db = db;

    public Task<T> InTransactionAsync<T>(Func<Task<T>> work, CancellationToken ct = default) => DbHelpers.InTransactionAsync(_db, work, ct);

    private static decimal? Dec(double? v) => v is null ? null : Math.Round((decimal)v.Value, 2, MidpointRounding.AwayFromZero);
    private static decimal Dec(double v) => Math.Round((decimal)v, 2, MidpointRounding.AwayFromZero);
    private static double? Dbl(decimal? v) => v is null ? null : (double)v.Value;

    internal sealed class RequestRow
    {
        public int Id { get; set; }
        public string? BrNo { get; set; }
        public string Title { get; set; } = string.Empty;
        public string? Priority { get; set; }
        public string? Stage { get; set; }
        public string? Status { get; set; }
    }

    public async Task<SlaRequestInfo?> GetRequestAsync(int id, bool lockRow, CancellationToken ct = default)
    {
        if (lockRow)
        {
            // Update lock on the request row so concurrent changes to one request serialize (same hint as the original).
            var row = await _db.Database.SqlQuery<RequestRow>($@"
SELECT br.id AS Id, br.br_no AS BrNo, br.title AS Title, p.name AS Priority, s.name AS Stage, st.name AS Status
  FROM dbo.business_requests br WITH (UPDLOCK, HOLDLOCK)
  LEFT JOIN dbo.priorities p ON p.id = br.priority_id
  LEFT JOIN dbo.stages s     ON s.id = br.stage_id
  LEFT JOIN dbo.statuses st  ON st.id = br.status_id
 WHERE br.id = {id}").ToListAsync(ct);
            return row.Select(Map).FirstOrDefault();
        }
        return await _db.BusinessRequests.AsNoTracking().Where(b => b.Id == id).Select(b => new SlaRequestInfo
        {
            Id = b.Id,
            BrNo = b.BrNo,
            Title = b.Title,
            Priority = b.Priority != null ? b.Priority.Name : null,
            Stage = b.Stage != null ? b.Stage.Name : null,
            Status = b.Status != null ? b.Status.Name : null,
        }).FirstOrDefaultAsync(ct);
    }

    private static SlaRequestInfo Map(RequestRow r) => new() { Id = r.Id, BrNo = r.BrNo, Title = r.Title, Priority = r.Priority, Stage = r.Stage, Status = r.Status };

    public Task<List<SlaRequestInfo>> ListTrackedRequestsAsync(CancellationToken ct = default) =>
        _db.BusinessRequests.AsNoTracking()
            .Where(b => _db.RequestStages.Any(rs => rs.BusinessRequestId == b.Id))
            .OrderBy(b => b.Id)
            .Select(b => new SlaRequestInfo { Id = b.Id, BrNo = b.BrNo, Title = b.Title, Priority = b.Priority != null ? b.Priority.Name : null })
            .ToListAsync(ct);

    public Task<List<int>> ListRequestIdsWithoutTrackingAsync(CancellationToken ct = default) =>
        _db.BusinessRequests.AsNoTracking().Where(b => !_db.RequestStages.Any(rs => rs.BusinessRequestId == b.Id)).OrderBy(b => b.Id).Select(b => b.Id).ToListAsync(ct);

    public Task<List<int>> ListRequestIdsWithStaleActiveStagesAsync(IReadOnlyCollection<string> pipelineNames, CancellationToken ct = default)
    {
        var names = pipelineNames.ToList();
        return _db.RequestStages.AsNoTracking()
            .Where(s => (s.Status == StageStatus.InProgress || s.Status == StageStatus.OnHold) && !names.Contains(s.StageName))
            .Select(s => s.BusinessRequestId).Distinct().OrderBy(x => x).ToListAsync(ct);
    }

    public async Task<bool> HasUserActivityAsync(int requestId, CancellationToken ct = default)
    {
        var statusChanges = await _db.RequestStageStatusHistory.CountAsync(h => h.BusinessRequestId == requestId && h.Source != "migration", ct);
        var stageMoves = await _db.RequestStageHistory.CountAsync(h => h.BusinessRequestId == requestId && h.Source != "migration", ct);
        return statusChanges + stageMoves > 0;
    }

    public async Task DeleteTrackingAsync(int requestId, CancellationToken ct = default)
    {
        await _db.RequestStageHistory.Where(h => h.BusinessRequestId == requestId && h.Source == "migration").ExecuteDeleteAsync(ct);
        // Stages cascade to segments, holds and status history.
        await _db.RequestStages.Where(s => s.BusinessRequestId == requestId).ExecuteDeleteAsync(ct);
    }

    private static StageRecord ToRecord(RequestStage s) => new()
    {
        Id = s.Id,
        BusinessRequestId = s.BusinessRequestId,
        StageName = s.StageName,
        StageOrder = s.StageOrder,
        Status = s.Status,
        StageEffortDays = Dbl(s.StageEffortDays),
        ActualEffortDays = (double)s.ActualEffortDays,
        RemainingEffortDays = Dbl(s.RemainingEffortDays),
        SlaConsumedDays = (double)s.SlaConsumedDays,
        TotalHoldTime = (double)s.TotalHoldTime,
        StartedAt = s.StartedAt,
        CompletedAt = s.CompletedAt,
        PausedAt = s.PausedAt,
        ResumeAt = s.ResumeAt,
        ReopenCount = s.ReopenCount,
        IsBackfilled = s.IsBackfilled,
        UpdatedAt = s.UpdatedAt,
        UpdatedBy = s.UpdatedBy,
    };

    public async Task<List<StageRecord>> ListStagesAsync(int requestId, CancellationToken ct = default) =>
        (await _db.RequestStages.AsNoTracking().Where(s => s.BusinessRequestId == requestId).OrderBy(s => s.StageOrder).ThenBy(s => s.Id).ToListAsync(ct))
        .Select(ToRecord).ToList();

    public async Task<List<StageRecord>> ListAllStagesAsync(CancellationToken ct = default) =>
        (await _db.RequestStages.AsNoTracking().OrderBy(s => s.BusinessRequestId).ThenBy(s => s.StageOrder).ThenBy(s => s.Id).ToListAsync(ct))
        .Select(ToRecord).ToList();

    public async Task<int> InsertStageAsync(StageRecord stage, CancellationToken ct = default)
    {
        var e = new RequestStage
        {
            BusinessRequestId = stage.BusinessRequestId,
            StageName = stage.StageName,
            StageOrder = stage.StageOrder,
            CreatedAt = stage.UpdatedAt,
        };
        Copy(stage, e);
        _db.RequestStages.Add(e);
        await DbHelpers.SaveAsync(_db, ct);
        _db.Entry(e).State = EntityState.Detached;
        return e.Id;
    }

    private static void Copy(StageRecord s, RequestStage e)
    {
        e.Status = s.Status;
        e.StageEffortDays = Dec(s.StageEffortDays);
        e.ActualEffortDays = Dec(s.ActualEffortDays);
        e.RemainingEffortDays = Dec(s.RemainingEffortDays);
        e.SlaConsumedDays = Dec(s.SlaConsumedDays);
        e.TotalHoldTime = Dec(s.TotalHoldTime);
        e.StartedAt = s.StartedAt;
        e.CompletedAt = s.CompletedAt;
        e.PausedAt = s.PausedAt;
        e.ResumeAt = s.ResumeAt;
        e.ReopenCount = s.ReopenCount;
        e.IsBackfilled = s.IsBackfilled;
        e.UpdatedAt = s.UpdatedAt;
        e.UpdatedBy = s.UpdatedBy;
    }

    public async Task UpdateStageAsync(StageRecord stage, CancellationToken ct = default)
    {
        var e = await _db.RequestStages.FirstAsync(s => s.Id == stage.Id, ct);
        Copy(stage, e);
        await DbHelpers.SaveAsync(_db, ct);
        _db.Entry(e).State = EntityState.Detached;
    }

    private static WorkSegment ToWork(SlaEffortTracking w) => new()
    {
        Id = w.Id, RequestStageId = w.RequestStageId, BusinessRequestId = w.BusinessRequestId, StartedAt = w.StartedAt, EndedAt = w.EndedAt,
        EndReason = w.EndReason, WorkingDays = Dbl(w.WorkingDays), CreatedBy = w.CreatedBy,
    };

    public async Task<List<WorkSegment>> ListWorkAsync(int requestId, CancellationToken ct = default) =>
        (await _db.SlaEffortTracking.AsNoTracking().Where(w => w.BusinessRequestId == requestId).OrderBy(w => w.StartedAt).ThenBy(w => w.Id).ToListAsync(ct)).Select(ToWork).ToList();

    public async Task<List<WorkSegment>> ListAllWorkAsync(CancellationToken ct = default) =>
        (await _db.SlaEffortTracking.AsNoTracking().ToListAsync(ct)).Select(ToWork).ToList();

    public async Task OpenWorkAsync(WorkSegment segment, CancellationToken ct = default)
    {
        var e = new SlaEffortTracking { RequestStageId = segment.RequestStageId, BusinessRequestId = segment.BusinessRequestId, StartedAt = segment.StartedAt, CreatedBy = segment.CreatedBy };
        _db.SlaEffortTracking.Add(e);
        await DbHelpers.SaveAsync(_db, ct);
        _db.Entry(e).State = EntityState.Detached;
    }

    public Task CloseWorkAsync(int id, DateTime endedAt, string reason, double workingDays, CancellationToken ct = default) =>
        _db.SlaEffortTracking.Where(w => w.Id == id && w.EndedAt == null).ExecuteUpdateAsync(s => s
            .SetProperty(w => w.EndedAt, endedAt)
            .SetProperty(w => w.EndReason, reason)
            .SetProperty(w => w.WorkingDays, Dec(workingDays)), ct);

    private static PauseRecord ToPause(SlaPauseHistory p) => new()
    {
        Id = p.Id, RequestStageId = p.RequestStageId, BusinessRequestId = p.BusinessRequestId, PausedAt = p.PausedAt, ResumedAt = p.ResumedAt,
        HoldDays = Dbl(p.HoldDays), Reason = p.Reason, PausedBy = p.PausedBy, ResumedBy = p.ResumedBy,
    };

    public async Task<List<PauseRecord>> ListPausesAsync(int requestId, CancellationToken ct = default) =>
        (await _db.SlaPauseHistory.AsNoTracking().Where(p => p.BusinessRequestId == requestId).OrderBy(p => p.PausedAt).ThenBy(p => p.Id).ToListAsync(ct)).Select(ToPause).ToList();

    public async Task<List<PauseRecord>> ListAllPausesAsync(CancellationToken ct = default) =>
        (await _db.SlaPauseHistory.AsNoTracking().ToListAsync(ct)).Select(ToPause).ToList();

    public async Task OpenPauseAsync(PauseRecord pause, CancellationToken ct = default)
    {
        var e = new SlaPauseHistory
        {
            RequestStageId = pause.RequestStageId, BusinessRequestId = pause.BusinessRequestId, PausedAt = pause.PausedAt,
            Reason = string.IsNullOrEmpty(pause.Reason) ? null : pause.Reason, PausedBy = pause.PausedBy,
        };
        _db.SlaPauseHistory.Add(e);
        await DbHelpers.SaveAsync(_db, ct);
        _db.Entry(e).State = EntityState.Detached;
    }

    public Task ClosePauseAsync(int id, DateTime resumedAt, double holdDays, string? resumedBy, CancellationToken ct = default) =>
        _db.SlaPauseHistory.Where(p => p.Id == id && p.ResumedAt == null).ExecuteUpdateAsync(s => s
            .SetProperty(p => p.ResumedAt, resumedAt)
            .SetProperty(p => p.HoldDays, Dec(holdDays))
            .SetProperty(p => p.ResumedBy, resumedBy), ct);

    public async Task InsertStatusHistoryAsync(StatusHistoryEntry h, CancellationToken ct = default)
    {
        var e = new RequestStageStatusHistory
        {
            RequestStageId = h.RequestStageId, BusinessRequestId = h.BusinessRequestId, StageName = h.StageName, FromStatus = h.FromStatus,
            ToStatus = h.ToStatus, Action = h.Action, Source = h.Source, PlannedEffortDays = Dec(h.PlannedEffortDays), ConsumedDays = Dec(h.ConsumedDays),
            RemainingDays = Dec(h.RemainingDays), Note = h.Note, ChangedBy = h.ChangedBy, ChangedAt = h.ChangedAt,
        };
        _db.RequestStageStatusHistory.Add(e);
        await DbHelpers.SaveAsync(_db, ct);
        _db.Entry(e).State = EntityState.Detached;
    }

    public async Task InsertStageHistoryAsync(StageHistoryEntry h, CancellationToken ct = default)
    {
        var e = new RequestStageHistory
        {
            BusinessRequestId = h.BusinessRequestId, FromStage = h.FromStage, ToStage = h.ToStage, Reason = h.Reason, Source = h.Source,
            ChangedBy = h.ChangedBy, ChangedAt = h.ChangedAt,
        };
        _db.RequestStageHistory.Add(e);
        await DbHelpers.SaveAsync(_db, ct);
        _db.Entry(e).State = EntityState.Detached;
    }

    public Task<List<RequestStageStatusHistory>> ListStatusHistoryAsync(int requestId, CancellationToken ct = default) =>
        _db.RequestStageStatusHistory.AsNoTracking().Where(h => h.BusinessRequestId == requestId).OrderByDescending(h => h.ChangedAt).ThenByDescending(h => h.Id).ToListAsync(ct);

    public Task<List<RequestStageHistory>> ListStageHistoryAsync(int requestId, CancellationToken ct = default) =>
        _db.RequestStageHistory.AsNoTracking().Where(h => h.BusinessRequestId == requestId).OrderByDescending(h => h.ChangedAt).ThenByDescending(h => h.Id).ToListAsync(ct);

    public Task SetLegacyFieldsAsync(int requestId, string? stageName, string? statusName, string? user, DateTime now, CancellationToken ct = default) =>
        _db.Database.ExecuteSqlInterpolatedAsync($@"
UPDATE dbo.business_requests
   SET stage_id   = COALESCE((SELECT id FROM dbo.stages   WHERE name = {stageName}),  stage_id),
       status_id  = COALESCE((SELECT id FROM dbo.statuses WHERE name = {statusName}), status_id),
       updated_at = {now},
       updated_by = {user}
 WHERE id = {requestId}", ct);
}
