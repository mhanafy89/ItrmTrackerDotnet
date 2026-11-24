using System.Globalization;
using System.Text.Json.Nodes;
using ItrmTracker.Application.Abstractions;
using ItrmTracker.Application.Common;
using ItrmTracker.Application.Sla;
using ItrmTracker.Domain.Constants;
using ItrmTracker.Domain.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

/// <summary>
/// C# equivalent of the original test/memory-repository.js (same ordering, id sequence and rollback semantics),
/// so the ported <see cref="SlaService"/> can be compared step by step with the original JS service.
/// </summary>
internal sealed class MemorySlaRepository : ISlaRepository
{
    internal sealed class State
    {
        public List<SlaRequestInfo> Requests = new();
        public List<StageRecord> Stages = new();
        public List<WorkSegment> Work = new();
        public List<PauseRecord> Pauses = new();
        public List<RequestStageStatusHistory> StatusHistory = new();
        public List<RequestStageHistory> StageHistory = new();
        public int Seq;

        public State Clone() => new()
        {
            Requests = Requests.Select(Copy).ToList(),
            Stages = Stages.Select(Copy).ToList(),
            Work = Work.Select(Copy).ToList(),
            Pauses = Pauses.Select(Copy).ToList(),
            StatusHistory = StatusHistory.Select(Copy).ToList(),
            StageHistory = StageHistory.Select(Copy).ToList(),
            Seq = Seq,
        };
    }

    public State S = new();
    private int _depth;

    internal static SlaRequestInfo Copy(SlaRequestInfo r) => new() { Id = r.Id, BrNo = r.BrNo, Title = r.Title, Priority = r.Priority, Stage = r.Stage, Status = r.Status };
    internal static StageRecord Copy(StageRecord s) => (StageRecord)typeof(object).GetMethod("MemberwiseClone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(s, null)!;
    internal static WorkSegment Copy(WorkSegment s) => (WorkSegment)typeof(object).GetMethod("MemberwiseClone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(s, null)!;
    internal static PauseRecord Copy(PauseRecord s) => (PauseRecord)typeof(object).GetMethod("MemberwiseClone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(s, null)!;
    internal static RequestStageStatusHistory Copy(RequestStageStatusHistory s) => (RequestStageStatusHistory)typeof(object).GetMethod("MemberwiseClone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(s, null)!;
    internal static RequestStageHistory Copy(RequestStageHistory s) => (RequestStageHistory)typeof(object).GetMethod("MemberwiseClone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(s, null)!;

    private int Next() => ++S.Seq;

    public async Task<T> InTransactionAsync<T>(Func<Task<T>> work, CancellationToken ct = default)
    {
        if (_depth > 0) return await work();
        var snapshot = S.Clone();
        _depth++;
        try
        {
            return await work();
        }
        catch
        {
            S = snapshot;
            throw;
        }
        finally
        {
            _depth--;
        }
    }

    public Task<SlaRequestInfo?> GetRequestAsync(int id, bool lockRow, CancellationToken ct = default) =>
        Task.FromResult(S.Requests.FirstOrDefault(r => r.Id == id) is { } r ? Copy(r) : null);

    public Task<List<SlaRequestInfo>> ListTrackedRequestsAsync(CancellationToken ct = default) =>
        Task.FromResult(S.Requests.Where(r => S.Stages.Any(s => s.BusinessRequestId == r.Id)).Select(Copy).ToList());

    public Task<List<int>> ListRequestIdsWithoutTrackingAsync(CancellationToken ct = default) =>
        Task.FromResult(S.Requests.Select(r => r.Id).Where(id => !S.Stages.Any(s => s.BusinessRequestId == id)).ToList());

    public Task<List<int>> ListRequestIdsWithStaleActiveStagesAsync(IReadOnlyCollection<string> pipelineNames, CancellationToken ct = default)
    {
        var names = new HashSet<string>(pipelineNames.Select(n => n.ToLowerInvariant()));
        return Task.FromResult(S.Stages.Where(s => s.Status is StageStatus.InProgress or StageStatus.OnHold && !names.Contains(s.StageName.ToLowerInvariant()))
            .Select(s => s.BusinessRequestId).Distinct().ToList());
    }

    public Task<bool> HasUserActivityAsync(int requestId, CancellationToken ct = default) =>
        Task.FromResult(S.StatusHistory.Any(h => h.BusinessRequestId == requestId && h.Source != "migration")
            || S.StageHistory.Any(h => h.BusinessRequestId == requestId && h.Source != "migration"));

    public Task DeleteTrackingAsync(int requestId, CancellationToken ct = default)
    {
        var ids = S.Stages.Where(s => s.BusinessRequestId == requestId).Select(s => s.Id).ToHashSet();
        S.Stages.RemoveAll(s => ids.Contains(s.Id));
        S.Work.RemoveAll(w => ids.Contains(w.RequestStageId));
        S.Pauses.RemoveAll(p => ids.Contains(p.RequestStageId));
        S.StatusHistory.RemoveAll(h => ids.Contains(h.RequestStageId));
        S.StageHistory.RemoveAll(h => h.BusinessRequestId == requestId && h.Source == "migration");
        return Task.CompletedTask;
    }

    public Task<List<StageRecord>> ListStagesAsync(int requestId, CancellationToken ct = default) =>
        Task.FromResult(S.Stages.Where(s => s.BusinessRequestId == requestId).OrderBy(s => s.StageOrder).ThenBy(s => s.Id).Select(Copy).ToList());

    public Task<List<StageRecord>> ListAllStagesAsync(CancellationToken ct = default) => Task.FromResult(S.Stages.Select(Copy).ToList());

    public Task<int> InsertStageAsync(StageRecord stage, CancellationToken ct = default)
    {
        var copy = Copy(stage);
        copy.Id = Next();
        S.Stages.Add(copy);
        return Task.FromResult(copy.Id);
    }

    public Task UpdateStageAsync(StageRecord stage, CancellationToken ct = default)
    {
        var i = S.Stages.FindIndex(s => s.Id == stage.Id);
        S.Stages[i] = Copy(stage);
        return Task.CompletedTask;
    }

    public Task<List<WorkSegment>> ListWorkAsync(int requestId, CancellationToken ct = default) => Task.FromResult(S.Work.Where(w => w.BusinessRequestId == requestId).Select(Copy).ToList());
    public Task<List<WorkSegment>> ListAllWorkAsync(CancellationToken ct = default) => Task.FromResult(S.Work.Select(Copy).ToList());

    public Task OpenWorkAsync(WorkSegment segment, CancellationToken ct = default)
    {
        S.Work.Add(new WorkSegment { Id = Next(), RequestStageId = segment.RequestStageId, BusinessRequestId = segment.BusinessRequestId, StartedAt = segment.StartedAt, CreatedBy = segment.CreatedBy });
        return Task.CompletedTask;
    }

    public Task CloseWorkAsync(int id, DateTime endedAt, string reason, double workingDays, CancellationToken ct = default)
    {
        var w = S.Work.First(x => x.Id == id);
        w.EndedAt = endedAt;
        w.EndReason = reason;
        w.WorkingDays = workingDays;
        return Task.CompletedTask;
    }

    public Task<List<PauseRecord>> ListPausesAsync(int requestId, CancellationToken ct = default) => Task.FromResult(S.Pauses.Where(p => p.BusinessRequestId == requestId).Select(Copy).ToList());
    public Task<List<PauseRecord>> ListAllPausesAsync(CancellationToken ct = default) => Task.FromResult(S.Pauses.Select(Copy).ToList());

    public Task OpenPauseAsync(PauseRecord pause, CancellationToken ct = default)
    {
        S.Pauses.Add(new PauseRecord { Id = Next(), RequestStageId = pause.RequestStageId, BusinessRequestId = pause.BusinessRequestId, PausedAt = pause.PausedAt, Reason = pause.Reason, PausedBy = pause.PausedBy });
        return Task.CompletedTask;
    }

    public Task ClosePauseAsync(int id, DateTime resumedAt, double holdDays, string? resumedBy, CancellationToken ct = default)
    {
        var p = S.Pauses.First(x => x.Id == id);
        p.ResumedAt = resumedAt;
        p.HoldDays = holdDays;
        p.ResumedBy = resumedBy;
        return Task.CompletedTask;
    }

    public Task InsertStatusHistoryAsync(StatusHistoryEntry h, CancellationToken ct = default)
    {
        S.StatusHistory.Add(new RequestStageStatusHistory
        {
            Id = Next(), RequestStageId = h.RequestStageId, BusinessRequestId = h.BusinessRequestId, StageName = h.StageName, FromStatus = h.FromStatus,
            ToStatus = h.ToStatus, Action = h.Action, Source = h.Source, PlannedEffortDays = (decimal?)h.PlannedEffortDays, ConsumedDays = (decimal?)h.ConsumedDays,
            RemainingDays = (decimal?)h.RemainingDays, Note = h.Note, ChangedBy = h.ChangedBy, ChangedAt = h.ChangedAt,
        });
        return Task.CompletedTask;
    }

    public Task InsertStageHistoryAsync(StageHistoryEntry h, CancellationToken ct = default)
    {
        S.StageHistory.Add(new RequestStageHistory
        {
            Id = Next(), BusinessRequestId = h.BusinessRequestId, FromStage = h.FromStage, ToStage = h.ToStage, Reason = h.Reason, Source = h.Source,
            ChangedBy = h.ChangedBy, ChangedAt = h.ChangedAt,
        });
        return Task.CompletedTask;
    }

    public Task<List<RequestStageStatusHistory>> ListStatusHistoryAsync(int requestId, CancellationToken ct = default) =>
        Task.FromResult(S.StatusHistory.Where(h => h.BusinessRequestId == requestId).Select(Copy).Reverse().ToList());

    public Task<List<RequestStageHistory>> ListStageHistoryAsync(int requestId, CancellationToken ct = default) =>
        Task.FromResult(S.StageHistory.Where(h => h.BusinessRequestId == requestId).Select(Copy).Reverse().ToList());

    public Task SetLegacyFieldsAsync(int requestId, string? stageName, string? statusName, string? user, DateTime now, CancellationToken ct = default)
    {
        var r = S.Requests.First(x => x.Id == requestId);
        if (!string.IsNullOrEmpty(stageName)) r.Stage = stageName;
        if (!string.IsNullOrEmpty(statusName)) r.Status = statusName;
        return Task.CompletedTask;
    }
}

/// <summary>Runs one randomized scenario through the PORTED SlaService and returns JSON shaped like the oracle's output.</summary>
internal static class ScenarioRunner
{
    private sealed class FakeClock : IClock
    {
        public DateTime Now;
        public DateTime UtcNow => Now;
        public TimeZoneInfo BusinessTimeZone => TimeZoneInfo.Local;
        public DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(Now, TimeZoneInfo.Local));
        public DateOnly ToBusinessDate(DateTime utc) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.Local));
    }

    private sealed class FakeConfig : ISlaConfigService
    {
        public SlaConfiguration Config = null!;
        public Task<SlaConfiguration> GetAsync(CancellationToken ct = default) => Task.FromResult(Config);
        public Task<SlaConfiguration> LoadAsync(CancellationToken ct = default) => Task.FromResult(Config);
        public Task<SlaConfiguration> SaveAsync(SlaConfigInput input, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SlaConfiguration> ResetAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public void Invalidate() { }
    }

    private sealed class NullAudit : IAuditLogger
    {
        public void Log(string action, string entity, object? entityId, string? details = null) { }
    }

    private static double N(JsonNode? n) => double.Parse(n!.ToJsonString(), CultureInfo.InvariantCulture);
    private static string? Str(JsonNode? n) => n?.GetValue<string>();
    private static long? Ms(DateTime? d) => d is null ? null : new DateTimeOffset(DateTime.SpecifyKind(d.Value, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
    private static DateTime FromMs(JsonNode n) => DateTimeOffset.FromUnixTimeMilliseconds(n.GetValue<long>()).UtcDateTime;

    internal static SlaConfiguration BuildConfig(JsonNode cfg)
    {
        var settings = cfg["settings"]!.AsObject();
        var raw = settings.Where(kv => kv.Key != "weekend_days").ToDictionary(kv => kv.Key, kv => kv.Value is null ? null : kv.Value.ToString());
        return new SlaConfiguration(
            cfg["stages"]!.AsArray().Select(s => new SlaStageDefinition(Str(s!["name"])!, s["stage_order"]!.GetValue<int>(), s["is_pipeline"]!.GetValue<int>() == 1, 0, 0, N(s["effort_days"]))).ToList(),
            cfg["priorities"]!.AsArray().Select(p => new SlaPriorityDefinition(Str(p!["name"])!, N(p["factor"]), 8, 7)).ToList(),
            new List<SlaCategoryDefinition>(),
            cfg["statuses"]!.AsArray().Select(s => new SlaStatusDefinition(Str(s!["name"])!, Str(s["state"])!)).ToList(),
            new SlaSettingsModel(settings["weekend_days"]!.AsArray().Select(x => x!.GetValue<int>()).ToList(), SlaEngine.ParseEngineSettings(raw)));
    }

    private static JsonObject Metrics(StageMetrics m) => new()
    {
        ["stage"] = m.Stage, ["order"] = m.Order, ["status"] = m.Status, ["plannedEffort"] = m.PlannedEffort, ["expectedSlaDays"] = m.ExpectedSlaDays,
        ["consumedDays"] = m.ConsumedDays, ["actualEffort"] = m.ActualEffort, ["remaining"] = m.Remaining, ["remainingExpectedDays"] = m.RemainingExpectedDays,
        ["holdDays"] = m.HoldDays, ["varianceDays"] = m.VarianceDays, ["utilizationPct"] = m.UtilizationPct, ["slaState"] = m.SlaState,
        ["startedAt"] = Ms(m.StartedAt), ["completedAt"] = Ms(m.CompletedAt), ["pausedAt"] = Ms(m.PausedAt), ["resumedAt"] = Ms(m.ResumedAt),
        ["reopenCount"] = m.ReopenCount, ["isBackfilled"] = m.IsBackfilled, ["expectedCompletionDate"] = m.ExpectedCompletionDate,
    };

    private static JsonObject Summary(RequestSlaSummary s) => new()
    {
        ["totalPlannedDays"] = s.TotalPlannedDays, ["totalExpectedDays"] = s.TotalExpectedDays, ["totalConsumedDays"] = s.TotalConsumedDays,
        ["totalRemainingDays"] = s.TotalRemainingDays, ["totalHoldDays"] = s.TotalHoldDays, ["varianceDays"] = s.VarianceDays,
        ["utilizationPct"] = s.UtilizationPct, ["slaHealth"] = s.SlaHealth, ["unestimatedStages"] = s.UnestimatedStages,
        ["isFinished"] = s.IsFinished, ["expectedCompletionDate"] = s.ExpectedCompletionDate,
    };

    private static JsonObject View(RequestSlaView v) => new()
    {
        ["requestId"] = v.RequestId, ["priority"] = v.Priority, ["priorityFactor"] = v.PriorityFactor, ["priorityFactorMode"] = v.PriorityFactorMode,
        ["currentStage"] = v.CurrentStage, ["status"] = v.Status, ["plannedEffort"] = v.PlannedEffort, ["actualConsumed"] = v.ActualConsumed,
        ["remaining"] = v.Remaining, ["holdDays"] = v.HoldDays, ["expectedCompletionDate"] = v.ExpectedCompletionDate, ["slaState"] = v.SlaState,
        ["expectedSlaDays"] = v.ExpectedSlaDays, ["utilizationPct"] = v.UtilizationPct, ["request"] = Summary(v.Request),
        ["stages"] = new JsonArray(v.Stages.Select(m => (JsonNode)Metrics(m)).ToArray()),
    };

    private static JsonObject StatusRow(RequestStageStatusHistory h) => new()
    {
        ["request_stage_id"] = h.RequestStageId, ["business_request_id"] = h.BusinessRequestId, ["stage_name"] = h.StageName, ["from_status"] = h.FromStatus,
        ["to_status"] = h.ToStatus, ["action"] = h.Action, ["source"] = h.Source, ["planned_effort_days"] = (double?)h.PlannedEffortDays,
        ["consumed_days"] = (double?)h.ConsumedDays, ["remaining_days"] = (double?)h.RemainingDays, ["note"] = h.Note, ["changed_by"] = h.ChangedBy, ["changed_at"] = Ms(h.ChangedAt),
    };

    private static JsonObject StageMove(RequestStageHistory h) => new()
    {
        ["business_request_id"] = h.BusinessRequestId, ["from_stage"] = h.FromStage, ["to_stage"] = h.ToStage, ["reason"] = h.Reason,
        ["source"] = h.Source, ["changed_by"] = h.ChangedBy, ["changed_at"] = Ms(h.ChangedAt),
    };

    private static JsonObject Pause(PauseRecord p) => new()
    {
        ["request_stage_id"] = p.RequestStageId, ["business_request_id"] = p.BusinessRequestId, ["paused_at"] = Ms(p.PausedAt), ["resumed_at"] = Ms(p.ResumedAt),
        ["hold_days"] = p.HoldDays, ["reason"] = p.Reason, ["paused_by"] = p.PausedBy, ["resumed_by"] = p.ResumedBy,
    };

    private static JsonObject Stage(StageRecord s) => new()
    {
        ["id"] = s.Id, ["business_request_id"] = s.BusinessRequestId, ["stage_name"] = s.StageName, ["stage_order"] = s.StageOrder, ["status"] = s.Status,
        ["stage_effort_days"] = s.StageEffortDays, ["actual_effort_days"] = s.ActualEffortDays, ["remaining_effort_days"] = s.RemainingEffortDays,
        ["sla_consumed_days"] = s.SlaConsumedDays, ["total_hold_time"] = s.TotalHoldTime, ["started_at"] = Ms(s.StartedAt), ["completed_at"] = Ms(s.CompletedAt),
        ["paused_at"] = Ms(s.PausedAt), ["resume_at"] = Ms(s.ResumeAt), ["reopen_count"] = s.ReopenCount, ["is_backfilled"] = s.IsBackfilled,
        ["updated_by"] = s.UpdatedBy, ["updated_at"] = Ms(s.UpdatedAt),
    };

    private static JsonObject Work(WorkSegment w) => new()
    {
        ["id"] = w.Id, ["request_stage_id"] = w.RequestStageId, ["business_request_id"] = w.BusinessRequestId, ["started_at"] = Ms(w.StartedAt),
        ["ended_at"] = Ms(w.EndedAt), ["end_reason"] = w.EndReason, ["working_days"] = w.WorkingDays, ["created_by"] = w.CreatedBy,
    };

    public static JsonNode Run(JsonObject scenario)
    {
        var repo = new MemorySlaRepository();
        var clock = new FakeClock();
        var config = new FakeConfig { Config = BuildConfig(scenario["config"]!) };
        var service = new SlaService(repo, config, clock, new NullAudit(), NullLogger<SlaService>.Instance, Options.Create(new SlaOptions()));
        foreach (var r in scenario["requests"]!.AsArray())
        {
            repo.S.Requests.Add(new SlaRequestInfo { Id = r!["id"]!.GetValue<int>(), Title = "Test request", Priority = Str(r["priority"]), Stage = Str(r["stage"]), Status = Str(r["status"]) });
        }

        var outputs = new JsonArray();
        foreach (var stepNode in scenario["steps"]!.AsArray())
        {
            var step = stepNode!.AsObject();
            clock.Now = FromMs(step["at"]!);
            JsonNode? result;
            try
            {
                result = RunStep(step, service, repo, config).GetAwaiter().GetResult();
            }
            catch (ServiceException ex)
            {
                result = new JsonObject { ["error"] = ex.StatusCode, ["errors"] = new JsonArray(ex.Errors.Select(e => (JsonNode)JsonValue.Create(e)!).ToArray()) };
            }
            outputs.Add(result);
        }

        return new JsonObject
        {
            ["outputs"] = outputs,
            ["state"] = new JsonObject
            {
                ["requests"] = new JsonArray(repo.S.Requests.Select(r => (JsonNode)new JsonObject { ["id"] = r.Id, ["stage"] = r.Stage, ["status"] = r.Status }).ToArray()),
                ["stages"] = new JsonArray(repo.S.Stages.Select(s => (JsonNode)Stage(s)).ToArray()),
                ["work"] = new JsonArray(repo.S.Work.Select(w => (JsonNode)Work(w)).ToArray()),
                ["pauses"] = new JsonArray(repo.S.Pauses.Select(p => (JsonNode)Pause(p)).ToArray()),
                ["statusHistory"] = new JsonArray(repo.S.StatusHistory.Select(h => (JsonNode)StatusRow(h)).ToArray()),
                ["stageHistory"] = new JsonArray(repo.S.StageHistory.Select(h => (JsonNode)StageMove(h)).ToArray()),
            },
        };
    }

    private static async Task<JsonNode?> RunStep(JsonObject step, SlaService service, MemorySlaRepository repo, FakeConfig config)
    {
        var id = step["id"]?.GetValue<int>() ?? 0;
        switch (Str(step["op"]))
        {
            case "get":
                return View(await service.GetRequestSlaAsync(id));
            case "update":
            {
                var input = step["input"]!;
                var res = await service.UpdateStageStatusAsync(id, new StageStatusInput
                {
                    Stage = Str(input["stage"]), Status = Str(input["status"]), PlannedEffort = Str(input["plannedEffort"]),
                    ActualEffortDays = Str(input["actualEffortDays"]), Note = Str(input["note"]),
                }, Str(step["user"]));
                var o = View(res.Sla);
                o["transition"] = new JsonObject { ["stage"] = res.Transition.Stage, ["from"] = res.Transition.From, ["to"] = res.Transition.To, ["action"] = res.Transition.Action, ["changed"] = res.Transition.Changed };
                return o;
            }
            case "legacy":
            {
                var r = repo.S.Requests.First(x => x.Id == id);
                r.Stage = Str(step["stage"]);
                r.Status = Str(step["status"]);
                var initialized = await service.SyncFromLegacyAsync(id, Str(step["user"]));
                return new JsonObject { ["initialized"] = initialized };
            }
            case "backfill":
            {
                var (total, created) = await service.BackfillMissingAsync("sla-migration");
                return new JsonObject { ["total"] = total, ["created"] = created };
            }
            case "dashboard":
            {
                var d = await service.GetDashboardAsync(Str(step["priority"]), Str(step["stage"]), Str(step["health"]));
                return new JsonObject
                {
                    ["thresholds"] = new JsonObject { ["atRiskPct"] = d.Thresholds.AtRiskPct, ["breachPct"] = d.Thresholds.BreachPct },
                    ["totals"] = new JsonObject
                    {
                        ["requests"] = d.Totals.Requests, ["plannedDays"] = d.Totals.PlannedDays, ["consumedDays"] = d.Totals.ConsumedDays, ["holdDays"] = d.Totals.HoldDays,
                        ["health"] = new JsonObject(d.Totals.Health.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
                    },
                    ["stages"] = new JsonArray(d.Stages.Select(s => (JsonNode)new JsonObject
                    {
                        ["stage"] = s.Stage, ["requests"] = s.Requests, ["plannedEffort"] = s.PlannedEffort, ["actualEffort"] = s.ActualEffort,
                        ["varianceDays"] = s.VarianceDays, ["holdDays"] = s.HoldDays, ["utilizationPct"] = s.UtilizationPct, ["slaState"] = s.SlaState,
                    }).ToArray()),
                    ["requests"] = new JsonArray(d.Requests.Select(r => (JsonNode)new JsonObject
                    {
                        ["requestId"] = r.RequestId, ["brNo"] = r.BrNo, ["title"] = r.Title, ["priority"] = r.Priority, ["currentStage"] = r.CurrentStage, ["status"] = r.Status,
                        ["totalPlannedDays"] = r.TotalPlannedDays, ["totalExpectedDays"] = r.TotalExpectedDays, ["totalConsumedDays"] = r.TotalConsumedDays,
                        ["totalRemainingDays"] = r.TotalRemainingDays, ["totalHoldDays"] = r.TotalHoldDays, ["varianceDays"] = r.VarianceDays,
                        ["utilizationPct"] = r.UtilizationPct, ["slaHealth"] = r.SlaHealth, ["unestimatedStages"] = r.UnestimatedStages,
                        ["isFinished"] = r.IsFinished, ["expectedCompletionDate"] = r.ExpectedCompletionDate,
                    }).ToArray()),
                };
            }
            case "history":
            {
                var h = await service.GetHistoryAsync(id);
                return new JsonObject
                {
                    ["statusHistory"] = new JsonArray(h.StatusHistory.Select(x => (JsonNode)StatusRow(x)).ToArray()),
                    ["stageHistory"] = new JsonArray(h.StageHistory.Select(x => (JsonNode)StageMove(x)).ToArray()),
                    ["pauses"] = new JsonArray(h.Pauses.Select(x => (JsonNode)Pause(x)).ToArray()),
                };
            }
            case "config":
                config.Config = BuildConfig(step["config"]!);
                return new JsonObject { ["ok"] = true };
            default:
                throw new NotSupportedException(Str(step["op"]));
        }
    }
}
