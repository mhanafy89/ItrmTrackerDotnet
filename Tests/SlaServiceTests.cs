using ItrmTracker.Application.Abstractions;
using ItrmTracker.Application.Sla;
using ItrmTracker.Domain.Constants;
using ItrmTracker.Domain.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ItrmTracker.Tests;

/// <summary>In-memory <see cref="ISlaRepository"/> with transaction rollback (port of test/memory-repository.js).</summary>
internal sealed class InMemorySlaRepository : ISlaRepository
{
    private sealed class State
    {
        public List<SlaRequestInfo> Requests = new();
        public List<StageRecord> Stages = new();
        public List<WorkSegment> Work = new();
        public List<PauseRecord> Pauses = new();
        public List<RequestStageStatusHistory> StatusHistory = new();
        public List<RequestStageHistory> StageHistory = new();
        public int Seq;
    }

    private State _s = new();
    private int _depth;

    /// <summary>Name of a method that throws once (rollback tests).</summary>
    public string? FailOn { get; set; }

    private static T Clone<T>(T o) where T : class =>
        (T)typeof(object).GetMethod("MemberwiseClone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(o, null)!;

    private State Snapshot() => new()
    {
        Requests = _s.Requests.Select(Clone).ToList(), Stages = _s.Stages.Select(Clone).ToList(), Work = _s.Work.Select(Clone).ToList(),
        Pauses = _s.Pauses.Select(Clone).ToList(), StatusHistory = _s.StatusHistory.Select(Clone).ToList(), StageHistory = _s.StageHistory.Select(Clone).ToList(), Seq = _s.Seq,
    };

    private void MaybeFail(string name)
    {
        if (FailOn != name) return;
        FailOn = null;
        throw new InvalidOperationException($"forced failure in {name}");
    }

    public void AddRequest(int id, string? stage, string? status, string? priority = "Medium") =>
        _s.Requests.Add(new SlaRequestInfo { Id = id, Title = "Test request", Stage = stage, Status = status, Priority = priority });

    public SlaRequestInfo Request(int id) => _s.Requests.First(r => r.Id == id);
    public int StageCount(int id) => _s.Stages.Count(s => s.BusinessRequestId == id);
    public int StatusHistoryCount => _s.StatusHistory.Count;

    public async Task<T> InTransactionAsync<T>(Func<Task<T>> work, CancellationToken ct = default)
    {
        if (_depth > 0) return await work();
        var snapshot = Snapshot();
        _depth++;
        try { return await work(); }
        catch { _s = snapshot; throw; }
        finally { _depth--; }
    }

    public Task<SlaRequestInfo?> GetRequestAsync(int id, bool lockRow, CancellationToken ct = default) => Task.FromResult(_s.Requests.FirstOrDefault(r => r.Id == id) is { } r ? Clone(r) : null);
    public Task<List<SlaRequestInfo>> ListTrackedRequestsAsync(CancellationToken ct = default) => Task.FromResult(_s.Requests.Where(r => _s.Stages.Any(s => s.BusinessRequestId == r.Id)).Select(Clone).ToList());
    public Task<List<int>> ListRequestIdsWithoutTrackingAsync(CancellationToken ct = default) => Task.FromResult(_s.Requests.Select(r => r.Id).Where(id => !_s.Stages.Any(s => s.BusinessRequestId == id)).ToList());
    public Task<List<int>> ListRequestIdsWithStaleActiveStagesAsync(IReadOnlyCollection<string> pipelineNames, CancellationToken ct = default)
    {
        var names = pipelineNames.Select(n => n.ToLowerInvariant()).ToHashSet();
        return Task.FromResult(_s.Stages.Where(s => s.Status is StageStatus.InProgress or StageStatus.OnHold && !names.Contains(s.StageName.ToLowerInvariant())).Select(s => s.BusinessRequestId).Distinct().ToList());
    }
    public Task<bool> HasUserActivityAsync(int requestId, CancellationToken ct = default) =>
        Task.FromResult(_s.StatusHistory.Any(h => h.BusinessRequestId == requestId && h.Source != "migration") || _s.StageHistory.Any(h => h.BusinessRequestId == requestId && h.Source != "migration"));
    public Task DeleteTrackingAsync(int requestId, CancellationToken ct = default)
    {
        var ids = _s.Stages.Where(s => s.BusinessRequestId == requestId).Select(s => s.Id).ToHashSet();
        _s.Stages.RemoveAll(s => ids.Contains(s.Id));
        _s.Work.RemoveAll(w => ids.Contains(w.RequestStageId));
        _s.Pauses.RemoveAll(p => ids.Contains(p.RequestStageId));
        _s.StatusHistory.RemoveAll(h => ids.Contains(h.RequestStageId));
        _s.StageHistory.RemoveAll(h => h.BusinessRequestId == requestId && h.Source == "migration");
        return Task.CompletedTask;
    }
    public Task<List<StageRecord>> ListStagesAsync(int requestId, CancellationToken ct = default) =>
        Task.FromResult(_s.Stages.Where(s => s.BusinessRequestId == requestId).OrderBy(s => s.StageOrder).ThenBy(s => s.Id).Select(Clone).ToList());
    public Task<List<StageRecord>> ListAllStagesAsync(CancellationToken ct = default) => Task.FromResult(_s.Stages.Select(Clone).ToList());
    public Task<int> InsertStageAsync(StageRecord stage, CancellationToken ct = default) { var c = Clone(stage); c.Id = ++_s.Seq; _s.Stages.Add(c); return Task.FromResult(c.Id); }
    public Task UpdateStageAsync(StageRecord stage, CancellationToken ct = default) { MaybeFail(nameof(UpdateStageAsync)); _s.Stages[_s.Stages.FindIndex(s => s.Id == stage.Id)] = Clone(stage); return Task.CompletedTask; }
    public Task<List<WorkSegment>> ListWorkAsync(int requestId, CancellationToken ct = default) => Task.FromResult(_s.Work.Where(w => w.BusinessRequestId == requestId).Select(Clone).ToList());
    public Task<List<WorkSegment>> ListAllWorkAsync(CancellationToken ct = default) => Task.FromResult(_s.Work.Select(Clone).ToList());
    public Task OpenWorkAsync(WorkSegment w, CancellationToken ct = default) { var c = Clone(w); c.Id = ++_s.Seq; _s.Work.Add(c); return Task.CompletedTask; }
    public Task CloseWorkAsync(int id, DateTime endedAt, string reason, double workingDays, CancellationToken ct = default)
    { var w = _s.Work.First(x => x.Id == id); w.EndedAt = endedAt; w.EndReason = reason; w.WorkingDays = workingDays; return Task.CompletedTask; }
    public Task<List<PauseRecord>> ListPausesAsync(int requestId, CancellationToken ct = default) => Task.FromResult(_s.Pauses.Where(p => p.BusinessRequestId == requestId).Select(Clone).ToList());
    public Task<List<PauseRecord>> ListAllPausesAsync(CancellationToken ct = default) => Task.FromResult(_s.Pauses.Select(Clone).ToList());
    public Task OpenPauseAsync(PauseRecord p, CancellationToken ct = default) { var c = Clone(p); c.Id = ++_s.Seq; _s.Pauses.Add(c); return Task.CompletedTask; }
    public Task ClosePauseAsync(int id, DateTime resumedAt, double holdDays, string? resumedBy, CancellationToken ct = default)
    { var p = _s.Pauses.First(x => x.Id == id); p.ResumedAt = resumedAt; p.HoldDays = holdDays; p.ResumedBy = resumedBy; return Task.CompletedTask; }
    public Task InsertStatusHistoryAsync(StatusHistoryEntry h, CancellationToken ct = default)
    {
        MaybeFail(nameof(InsertStatusHistoryAsync));
        _s.StatusHistory.Add(new RequestStageStatusHistory
        {
            Id = ++_s.Seq, RequestStageId = h.RequestStageId, BusinessRequestId = h.BusinessRequestId, StageName = h.StageName, FromStatus = h.FromStatus, ToStatus = h.ToStatus,
            Action = h.Action, Source = h.Source, PlannedEffortDays = (decimal?)h.PlannedEffortDays, ConsumedDays = (decimal?)h.ConsumedDays, RemainingDays = (decimal?)h.RemainingDays,
            Note = h.Note, ChangedBy = h.ChangedBy, ChangedAt = h.ChangedAt,
        });
        return Task.CompletedTask;
    }
    public Task InsertStageHistoryAsync(StageHistoryEntry h, CancellationToken ct = default)
    {
        _s.StageHistory.Add(new RequestStageHistory { Id = ++_s.Seq, BusinessRequestId = h.BusinessRequestId, FromStage = h.FromStage, ToStage = h.ToStage, Reason = h.Reason, Source = h.Source, ChangedBy = h.ChangedBy, ChangedAt = h.ChangedAt });
        return Task.CompletedTask;
    }
    public Task<List<RequestStageStatusHistory>> ListStatusHistoryAsync(int requestId, CancellationToken ct = default) => Task.FromResult(_s.StatusHistory.Where(h => h.BusinessRequestId == requestId).Reverse().ToList());
    public Task<List<RequestStageHistory>> ListStageHistoryAsync(int requestId, CancellationToken ct = default) => Task.FromResult(_s.StageHistory.Where(h => h.BusinessRequestId == requestId).Reverse().ToList());
    public Task SetLegacyFieldsAsync(int requestId, string? stageName, string? statusName, string? user, DateTime now, CancellationToken ct = default)
    {
        var r = _s.Requests.First(x => x.Id == requestId);
        if (!string.IsNullOrEmpty(stageName)) r.Stage = stageName;
        if (!string.IsNullOrEmpty(statusName)) r.Status = statusName;
        return Task.CompletedTask;
    }
}

/// <summary>Service-level tests ported from test/sla-service.integration.test.js.</summary>
public class SlaServiceTests
{
    private sealed class Clock : IClock
    {
        public DateTime Now { get; set; }
        public DateTime UtcNow => Now;
        public TimeZoneInfo BusinessTimeZone => TimeZoneInfo.Local;
        public DateOnly Today => ToBusinessDate(Now);
        public DateOnly ToBusinessDate(DateTime utc) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.Local));
    }

    private sealed class Config : ISlaConfigService
    {
        public SlaConfiguration Value { get; set; } = null!;
        public Task<SlaConfiguration> GetAsync(CancellationToken ct = default) => Task.FromResult(Value);
        public Task<SlaConfiguration> LoadAsync(CancellationToken ct = default) => Task.FromResult(Value);
        public Task<SlaConfiguration> SaveAsync(SlaConfigInput input, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SlaConfiguration> ResetAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public void Invalidate() { }
    }

    private sealed class NoAudit : IAuditLogger
    {
        public void Log(string action, string entity, object? entityId, string? details = null) { }
    }

    /// <summary>Local-time instant in November/December 2026 (Mon 23 Nov 2026 is a Monday).</summary>
    private static DateTime At(int month, int day, int hour = 9) => new DateTime(2026, month, day, hour, 0, 0, DateTimeKind.Local).ToUniversalTime();

    private static SlaConfiguration MakeConfig() => new(
        new[]
        {
            new SlaStageDefinition("Planning", 1, true, 0, 0, 3), new SlaStageDefinition("BA", 2, true, 0, 0, 5),
            new SlaStageDefinition("Development", 3, true, 0, 0, 15), new SlaStageDefinition("QA", 4, true, 0, 0, 5),
            new SlaStageDefinition("UAT", 5, true, 0, 0, 3), new SlaStageDefinition("Deployment", 6, true, 0, 0, 0),
            new SlaStageDefinition("Closed", 7, false, 0, 0, 0),
        },
        new[] { new SlaPriorityDefinition("Critical", 1.5, 1, 1), new SlaPriorityDefinition("Medium", 1, 8, 7) },
        Array.Empty<SlaCategoryDefinition>(),
        new[]
        {
            new SlaStatusDefinition("Not Started", "Active"), new SlaStatusDefinition("In Progress", "Active"), new SlaStatusDefinition("On Hold", "Paused"),
            new SlaStatusDefinition("Completed", "Done"), new SlaStatusDefinition("Cancelled", "Done"),
        },
        new SlaSettingsModel(new[] { 0, 6 }, SlaEngine.ParseEngineSettings(new Dictionary<string, string?> { ["priority_factor_mode"] = "divisor" })));

    private static (SlaService Service, InMemorySlaRepository Repo, Clock Clock) Setup()
    {
        var repo = new InMemorySlaRepository();
        var clock = new Clock { Now = At(11, 23) };
        var service = new SlaService(repo, new Config { Value = MakeConfig() }, clock, new NoAudit(), NullLogger<SlaService>.Instance, Options.Create(new SlaOptions()));
        return (service, repo, clock);
    }

    private static StageStatusInput Input(string stage, string status, string? planned = null, string? note = null) => new() { Stage = stage, Status = status, PlannedEffort = planned, Note = note };

    [Fact]
    public async Task Reading_the_sla_initializes_tracking_from_the_legacy_stage_and_status()
    {
        var (service, repo, _) = Setup();
        repo.AddRequest(1, "Development", "In Progress");

        var sla = await service.GetRequestSlaAsync(1);

        Assert.Equal("Development", sla.CurrentStage);
        Assert.Equal(StageStatus.InProgress, sla.Status);
        Assert.Equal(15, sla.PlannedEffort);
        Assert.Equal(0, sla.ActualConsumed);
        Assert.Equal(15, sla.Remaining);
        Assert.Equal(SlaHealth.OnTrack, sla.SlaState);
        Assert.Equal("2026-12-14", sla.ExpectedCompletionDate); // 15 working days from Mon 23 Nov
        Assert.True(sla.Stages.Single(s => s.Stage == "BA").IsBackfilled);
        Assert.Equal(StageStatus.NotYetEstimated, sla.Stages.Single(s => s.Stage == "Deployment").Status);
        Assert.Equal(15 + 5 + 3, sla.Request.TotalPlannedDays);
        Assert.Equal(1, sla.Request.UnestimatedStages);

        await service.GetRequestSlaAsync(1); // idempotent
        Assert.Equal(6, repo.StageCount(1));
    }

    [Fact]
    public async Task Lifecycle_hold_time_is_not_consumed_and_legacy_fields_follow_the_engine()
    {
        var (service, repo, clock) = Setup();
        repo.AddRequest(1, "Planning", "Not Started");

        await service.UpdateStageStatusAsync(1, Input("Planning", StageStatus.InProgress), "Alice");
        Assert.Equal("In Progress", repo.Request(1).Status);

        clock.Now = At(11, 25);
        await service.UpdateStageStatusAsync(1, Input("Planning", StageStatus.OnHold, note: "waiting"), "Alice");
        clock.Now = At(11, 30);
        await service.UpdateStageStatusAsync(1, Input("Planning", StageStatus.InProgress), "Alice");
        clock.Now = At(12, 2);
        var done = await service.UpdateStageStatusAsync(1, Input("Planning", StageStatus.Completed), "Alice");

        var planning = done.Sla.Stages.Single(s => s.Stage == "Planning");
        Assert.Equal(4, planning.ActualEffort);   // Tue, Wed + Tue, Wed
        Assert.Equal(3, planning.HoldDays);       // Thu, Fri, Mon
        Assert.Equal("BA", done.Sla.CurrentStage);
        Assert.Equal("BA", repo.Request(1).Stage);
        Assert.Equal("Not Started", repo.Request(1).Status);
    }

    [Fact]
    public async Task Only_one_stage_may_be_in_progress_and_stage_order_is_enforced()
    {
        var (service, repo, _) = Setup();
        repo.AddRequest(1, "Planning", "In Progress");
        var ex = await Assert.ThrowsAsync<SlaException>(() => service.UpdateStageStatusAsync(1, Input("BA", StageStatus.InProgress), "Alice"));
        Assert.Equal(409, ex.StatusCode);
        Assert.Contains("\"Planning\" is already In Progress: put it On Hold or complete it first.", ex.Errors);
        Assert.Contains("Earlier stage(s) must be Completed or Cancelled first: Planning.", ex.Errors);
    }

    [Fact]
    public async Task A_failure_mid_transition_rolls_everything_back()
    {
        var (service, repo, _) = Setup();
        repo.AddRequest(1, "Planning", "Not Started");
        await service.GetRequestSlaAsync(1);
        var before = repo.StatusHistoryCount;

        repo.FailOn = nameof(InMemorySlaRepository.InsertStatusHistoryAsync);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateStageStatusAsync(1, Input("Planning", StageStatus.InProgress), "Alice"));

        var sla = await service.GetRequestSlaAsync(1);
        Assert.Equal(StageStatus.NotStarted, sla.Stages.Single(s => s.Stage == "Planning").Status);
        Assert.Equal(before, repo.StatusHistoryCount);
    }

    [Fact]
    public async Task Legacy_form_edits_are_reconciled()
    {
        var (service, repo, _) = Setup();
        repo.AddRequest(1, "Planning", "In Progress");
        await service.GetRequestSlaAsync(1);

        var r = repo.Request(1);
        r.Stage = "Development";
        r.Status = "In Progress";
        await service.SyncFromLegacyAsync(1, "Form User");

        var sla = await service.GetRequestSlaAsync(1);
        Assert.Equal(StageStatus.Completed, sla.Stages.Single(s => s.Stage == "Planning").Status); // was active → completed
        Assert.Equal(StageStatus.Cancelled, sla.Stages.Single(s => s.Stage == "BA").Status);       // skipped → cancelled
        Assert.Equal(StageStatus.InProgress, sla.Stages.Single(s => s.Stage == "Development").Status);

        r.Status = "On Hold";
        await service.SyncFromLegacyAsync(1, "Form User");
        Assert.Equal(StageStatus.OnHold, (await service.GetRequestSlaAsync(1)).Stages.Single(s => s.Stage == "Development").Status);

        r.Status = "Completed";
        await service.SyncFromLegacyAsync(1, "Form User");
        Assert.True((await service.GetRequestSlaAsync(1)).Request.IsFinished);
    }
}
