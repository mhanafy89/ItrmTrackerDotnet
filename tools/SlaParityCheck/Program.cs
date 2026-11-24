using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ItrmTracker.Application.Common;
using ItrmTracker.Application.Import;
using ItrmTracker.Application.Query;
using ItrmTracker.Application.Sla;
using ItrmTracker.Application.Validation;
using ItrmTracker.Domain.Constants;
using ItrmTracker.Infrastructure.Identity;

// Usage: dotnet run -- <legacyAppRoot> [nodeExe] [casesPerKind] [seed]
var legacyRoot = args.Length > 0 ? args[0] : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));
var nodeExe = args.Length > 1 ? args[1] : "node";
var perKind = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 400;
var seed = args.Length > 3 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 20261124;

var gen = new CaseGenerator(new Random(seed));
var cases = gen.Generate(perKind);
var work = Path.Combine(Path.GetTempPath(), "itrm-parity-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(work);
var casesFile = Path.Combine(work, "cases.json");
var resultsFile = Path.Combine(work, "results.json");
File.WriteAllText(casesFile, new JsonArray(cases.Select(c => (JsonNode)new JsonObject { ["kind"] = c.Kind, ["input"] = c.Input.DeepClone() }).ToArray()).ToJsonString());

var oracle = Path.Combine(AppContext.BaseDirectory, "legacy-oracle.js");
var psi = new ProcessStartInfo(nodeExe, $"\"{oracle}\" \"{legacyRoot}\" \"{casesFile}\" \"{resultsFile}\"") { RedirectStandardOutput = true, RedirectStandardError = true };
using (var p = Process.Start(psi)!)
{
    Console.Write(p.StandardOutput.ReadToEnd());
    var err = p.StandardError.ReadToEnd();
    p.WaitForExit();
    if (p.ExitCode != 0) { Console.Error.WriteLine(err); return 2; }
}
var results = JsonNode.Parse(File.ReadAllText(resultsFile))!.AsArray();

var failures = 0;
var perKindCount = new Dictionary<string, (int Ok, int Fail)>();
for (var i = 0; i < cases.Count; i++)
{
    var c = cases[i];
    var expected = results[i]!;
    JsonNode? actual;
    string? actualError = null;
    try { actual = Port.Run(c.Kind, c.Input); }
    catch (Exception ex) { actual = null; actualError = ex.GetType().Name + ": " + ex.Message; }

    bool same;
    if (c.Kind == "scryptHash")
    {
        // Node created the hash; the port must accept the right password and reject a wrong one.
        var password = c.Input["password"]!.GetValue<string>();
        var hash = expected["value"]!.GetValue<string>();
        same = Scrypt.IsLegacyFormat(hash) && Scrypt.VerifyLegacy(password, hash) && !Scrypt.VerifyLegacy(password + "x", hash);
        actual = same ? "verified" : "NOT verified";
    }
    else if (expected["ok"]!.GetValue<bool>()) same = actualError is null && Json.DeepEqual(expected["value"], actual);
    else same = actualError is not null; // both failed
    var (ok, fail) = perKindCount.GetValueOrDefault(c.Kind);
    perKindCount[c.Kind] = same ? (ok + 1, fail) : (ok, fail + 1);
    if (!same)
    {
        failures++;
        if (failures <= 25)
        {
            Console.WriteLine($"--- MISMATCH #{failures} [{c.Kind}] case {i}");
            Console.WriteLine("input:    " + c.Input.ToJsonString());
            Console.WriteLine("legacy:   " + expected.ToJsonString());
            Console.WriteLine("ported:   " + (actualError ?? actual?.ToJsonString() ?? "null"));
        }
    }
}

Console.WriteLine();
Console.WriteLine($"{"Kind",-22} {"Match",7} {"Diff",6}");
foreach (var (kind, (ok, fail)) in perKindCount.OrderBy(k => k.Key)) Console.WriteLine($"{kind,-22} {ok,7} {fail,6}");
Console.WriteLine($"TOTAL {cases.Count} cases, {failures} mismatches (seed {seed}).");
var scenarioOutputs = results.Select((r, i) => (r, c: cases[i])).Where(x => x.c.Kind == "scenario" && x.r!["ok"]!.GetValue<bool>())
    .SelectMany(x => x.r!["value"]!["outputs"]!.AsArray()).ToList();
Console.WriteLine($"Scenario coverage: {scenarioOutputs.Count} steps, {scenarioOutputs.Count(o => o?["error"] is not null)} rule rejections, " +
    $"{scenarioOutputs.Count(o => o?["transition"]?["changed"]?.GetValue<bool>() == true)} effective stage transitions, " +
    $"{scenarioOutputs.Count(o => o?["initialized"] is not null)} legacy syncs.");
Directory.Delete(work, true);
return failures == 0 ? 0 : 1;

internal sealed record ParityCase(string Kind, JsonObject Input);

/// <summary>Runs the PORTED C# implementation for a case and returns JSON shaped like the legacy output.</summary>
internal static class Port
{
    private static double N(JsonNode? n) => double.Parse(n!.ToJsonString(), CultureInfo.InvariantCulture);
    private static DateTime? Dt(JsonNode? n) => n is null ? null : DateTimeOffset.FromUnixTimeMilliseconds(n.GetValue<long>()).UtcDateTime;
    private static long? Ms(DateTime? d) => d is null ? null : new DateTimeOffset(DateTime.SpecifyKind(d.Value, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
    private static double? Dbl(JsonNode? n) => n is null ? null : N(n);
    private static string? Str(JsonNode? n) => n?.GetValue<string>();
    private static SlaCalendar Cal(JsonNode? weekend) => new(weekend!.AsArray().Select(x => x!.GetValue<int>()), TimeZoneInfo.Local);
    private static Dictionary<string, string?> Raw(JsonNode? o) => o!.AsObject().ToDictionary(kv => kv.Key, kv => kv.Value is null ? null : kv.Value.GetValue<string>());

    private static StageRecord Stage(JsonNode s) => new()
    {
        Id = s["id"]!.GetValue<int>(),
        BusinessRequestId = 1,
        StageName = Str(s["stage_name"])!,
        StageOrder = s["stage_order"]!.GetValue<int>(),
        Status = Str(s["status"])!,
        StageEffortDays = Dbl(s["stage_effort_days"]),
        ActualEffortDays = Dbl(s["actual_effort_days"]) ?? 0,
        StartedAt = Dt(s["started_at"]),
        CompletedAt = Dt(s["completed_at"]),
        PausedAt = Dt(s["paused_at"]),
        ResumeAt = Dt(s["resume_at"]),
        ReopenCount = s["reopen_count"]?.GetValue<int>() ?? 0,
        IsBackfilled = s["is_backfilled"]?.GetValue<bool>() ?? false,
    };

    private static List<WorkSegment> Work(JsonNode? arr) => arr!.AsArray().Select(w => new WorkSegment { StartedAt = Dt(w!["started_at"])!.Value, EndedAt = Dt(w["ended_at"]) }).ToList();
    private static List<PauseRecord> Pauses(JsonNode? arr) => arr!.AsArray().Select(p => new PauseRecord { PausedAt = Dt(p!["paused_at"])!.Value, ResumedAt = Dt(p["resumed_at"]) }).ToList();
    private static JsonArray Strings(IEnumerable<string> s) => new(s.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray());

    private static JsonObject Metrics(StageMetrics m) => new()
    {
        ["stage"] = m.Stage, ["order"] = m.Order, ["status"] = m.Status, ["plannedEffort"] = m.PlannedEffort,
        ["expectedSlaDays"] = m.ExpectedSlaDays, ["consumedDays"] = m.ConsumedDays, ["actualEffort"] = m.ActualEffort,
        ["remaining"] = m.Remaining, ["remainingExpectedDays"] = m.RemainingExpectedDays, ["holdDays"] = m.HoldDays,
        ["varianceDays"] = m.VarianceDays, ["utilizationPct"] = m.UtilizationPct, ["slaState"] = m.SlaState,
        ["startedAt"] = Ms(m.StartedAt), ["completedAt"] = Ms(m.CompletedAt), ["pausedAt"] = Ms(m.PausedAt), ["resumedAt"] = Ms(m.ResumedAt),
        ["reopenCount"] = m.ReopenCount, ["isBackfilled"] = m.IsBackfilled, ["expectedCompletionDate"] = m.ExpectedCompletionDate,
    };

    private static JsonObject Settings(EngineSettings s) => new()
    {
        ["at_risk_threshold_pct"] = s.AtRiskThresholdPct, ["breach_threshold_pct"] = s.BreachThresholdPct,
        ["priority_factor_mode"] = s.PriorityFactorMode, ["enforce_stage_order"] = s.EnforceStageOrder, ["auto_start_next_stage"] = s.AutoStartNextStage,
    };

    public static JsonNode? Run(string kind, JsonObject c)
    {
        switch (kind)
        {
            case "workingDays":
                return SlaEngine.WorkingDaysBetween(Dt(c["from"]), Dt(c["to"]), Cal(c["weekend"]));
            case "addWorkingDays":
                return SlaEngine.AddWorkingDays(DateOnly.ParseExact(Str(c["start"])!, "yyyy-MM-dd", CultureInfo.InvariantCulture), N(c["days"]), Cal(c["weekend"]));
            case "stageSla":
                return Metrics(SlaEngine.CalculateStageSla(Stage(c["stage"]!), Work(c["work"]), Pauses(c["pauses"]), N(c["factor"]),
                    SlaEngine.ParseEngineSettings(Raw(c["settings"])), Cal(c["weekend"]), Dt(c["now"])!.Value));
            case "summarize":
            {
                var settings = SlaEngine.ParseEngineSettings(Raw(c["settings"]));
                var cal = Cal(c["weekend"]);
                var now = Dt(c["now"])!.Value;
                var stages = c["stages"]!.AsArray();
                var metrics = stages.Select(s => SlaEngine.CalculateStageSla(Stage(s!["stage"]!), Work(s["work"]), Pauses(s["pauses"]), N(c["factor"]), settings, cal, now)).ToList();
                var sum = SlaEngine.SummarizeRequest(metrics, settings, cal, now);
                var cur = SlaEngine.DeriveCurrentStage(stages.Select(s => Stage(s!["stage"]!)));
                return new JsonObject
                {
                    ["totalPlannedDays"] = sum.TotalPlannedDays, ["totalExpectedDays"] = sum.TotalExpectedDays, ["totalConsumedDays"] = sum.TotalConsumedDays,
                    ["totalRemainingDays"] = sum.TotalRemainingDays, ["totalHoldDays"] = sum.TotalHoldDays, ["varianceDays"] = sum.VarianceDays,
                    ["utilizationPct"] = sum.UtilizationPct, ["slaHealth"] = sum.SlaHealth, ["unestimatedStages"] = sum.UnestimatedStages,
                    ["isFinished"] = sum.IsFinished, ["expectedCompletionDate"] = sum.ExpectedCompletionDate, ["current"] = cur?.StageName,
                };
            }
            case "plan":
            {
                var stages = c["stages"]!.AsArray().Select(s => Stage(s!)).ToList();
                var stage = stages.First(s => s.Id == c["stage"]!["id"]!.GetValue<int>());
                var r = SlaEngine.PlanTransition(stage, Str(c["toStatus"])!, stages, Dbl(c["plannedEffort"]), SlaEngine.ParseEngineSettings(Raw(c["settings"])), c["force"]!.GetValue<bool>());
                return new JsonObject { ["action"] = r.Action, ["toStatus"] = r.ToStatus, ["errors"] = Strings(r.Errors), ["plannedEffort"] = r.PlannedEffort };
            }
            case "validateStageInput":
            {
                var (errors, clean) = SlaEngine.ValidateStageStatusInput(new StageStatusInput
                {
                    Stage = Str(c["stage"]), Status = Str(c["status"]), PlannedEffort = Str(c["plannedEffort"]), ActualEffortDays = Str(c["actualEffortDays"]), Note = Str(c["note"]),
                });
                return new JsonObject
                {
                    ["errors"] = Strings(errors), ["stage"] = clean.Stage, ["status"] = clean.Status, ["plannedEffort"] = clean.PlannedEffort,
                    ["actualEffortDays"] = clean.ActualEffortDays, ["note"] = clean.Note,
                };
            }
            case "engineSettings":
            {
                var raw = Raw(c["raw"]);
                var (errors, clean) = SlaEngine.ValidateEngineSettings(raw);
                var cleanObj = new JsonObject();
                foreach (var kv in clean) cleanObj[kv.Key] = kv.Value;
                return new JsonObject { ["parsed"] = Settings(SlaEngine.ParseEngineSettings(raw)), ["errors"] = Strings(errors), ["clean"] = cleanObj };
            }
            case "initialStages":
            {
                var pipeline = c["pipeline"]!.AsArray().Select(p => new SlaStageDefinition(Str(p!["name"])!, p["stage_order"]!.GetValue<int>(), true, 0, 0, N(p["effort_days"]))).ToList();
                var ls = c["legacyStage"];
                var legacy = ls is null ? null : new SlaStageDefinition(Str(ls["name"])!, ls["stage_order"]!.GetValue<int>(), false, 0, 0, 0);
                var intent = SlaEngine.ClassifyLegacyStatus(Str(c["statusName"]), Str(c["statusState"]));
                return new JsonArray(SlaEngine.PlanInitialStages(pipeline, legacy, intent).Select(p => (JsonNode)new JsonObject
                {
                    ["stage_name"] = p.StageName, ["stage_order"] = p.StageOrder, ["stage_effort_days"] = p.StageEffortDays,
                    ["is_backfilled"] = p.IsBackfilled, ["status"] = p.Status, ["intent"] = intent,
                }).ToArray());
            }
            case "legacySla":
            {
                var cfg = c["config"]!;
                var config = new SlaConfiguration(
                    cfg["stages"]!.AsArray().Select(s => new SlaStageDefinition(Str(s!["name"])!, s["stage_order"]!.GetValue<int>(), s["is_pipeline"]!.GetValue<int>() == 1,
                        N(s["br_days"]), N(s["incident_days"]), N(s["effort_days"]))).ToList(),
                    cfg["priorities"]!.AsArray().Select(p => new SlaPriorityDefinition(Str(p!["name"])!, N(p["factor"]), 8, 7)).ToList(),
                    cfg["categories"]!.AsArray().Select(p => new SlaCategoryDefinition(Str(p!["name"])!, N(p["factor"]))).ToList(),
                    cfg["statuses"]!.AsArray().Select(p => new SlaStatusDefinition(Str(p!["name"])!, Str(p["state"])!)).ToList(),
                    new SlaSettingsModel(cfg["settings"]!["weekend_days"]!.AsArray().Select(x => x!.GetValue<int>()).ToList(), EngineSettings.Default));
                var rec = c["record"]!;
                var subject = new Subject(Str(rec["status"]), Str(rec["priority"]), Str(rec["business_category"]), Str(rec["stage"]),
                    Str(rec["deployment_planned_end_date"]) is { } dp ? DateOnly.ParseExact(dp, "yyyy-MM-dd", CultureInfo.InvariantCulture) : null);
                var r = new LegacySlaCalculator(config).Compute(subject, Str(c["type"]) == "incidents" ? TrackerKind.Incident : TrackerKind.BusinessRequest,
                    DateOnly.ParseExact(Str(c["today"])!, "yyyy-MM-dd", CultureInfo.InvariantCulture));
                return new JsonObject
                {
                    ["sla_state"] = r.SlaState, ["sla_remaining_days"] = r.SlaRemainingDays, ["sla_effort_days"] = r.SlaEffortDays,
                    ["sla_target_date"] = r.SlaTargetDate, ["sla_gap_days"] = r.SlaGapDays, ["sla_flag"] = r.SlaFlag,
                    ["sla_breakdown"] = new JsonArray(r.SlaBreakdown.Select(b => (JsonNode)new JsonObject { ["stage"] = b.Stage, ["days"] = b.Days }).ToArray()),
                };
            }
            case "validateRequest":
            {
                var body = c["body"]!.AsObject().ToDictionary(kv => kv.Key, kv => kv.Value is null ? null : kv.Value.GetValue<string>());
                return Strings(c["incident"]!.GetValue<bool>() ? RequestValidation.ValidateIncident(body) : RequestValidation.ValidateBusinessRequest(body));
            }
            case "normalizeDate":
            {
                var (value, error) = ImportService.NormalizeDate(Str(c["value"]));
                var o = new JsonObject();
                if (error is not null) o["error"] = error; else o["value"] = value;
                return o;
            }
            case "parseBool":
                return ImportService.ParseBool(Str(c["value"]));
            case "normalizeHeader":
                return ImportService.NormalizeHeader(Str(c["value"]));
            case "suggestMapping":
            {
                var svc = new ImportService(null!, null!, null!);
                var m = svc.SuggestMapping(Str(c["target"])!, c["headers"]!.AsArray().Select(h => h!.GetValue<string>()).ToList());
                var o = new JsonObject();
                foreach (var kv in m) o[kv.Key] = kv.Value;
                return o;
            }
            case "checkReadOnly":
                return QueryConsoleService.CheckReadOnly(Str(c["sql"])!);
            case "csv":
                return new JsonArray(CsvParser.Parse(Str(c["text"])!).Select(r => (JsonNode)Strings(r)).ToArray());
            case "scryptHash":
                // The oracle returns Node's hash; the ported side returns "verified" markers that are compared specially.
                return null;
            case "slaConfigValidate":
            {
                var b = c["body"]!;
                var input = new SlaConfigInput
                {
                    Stages = b["stages"]!.AsArray().Select(r => new SlaStageInput { Name = Str(r!["name"]), StageOrder = Str(r["stage_order"]), IsPipeline = r["is_pipeline"]!.GetValue<bool>(), BrDays = Str(r["br_days"]), IncidentDays = Str(r["incident_days"]), EffortDays = Str(r["effort_days"]) }).ToList(),
                    Priorities = b["priorities"]!.AsArray().Select(r => new SlaPriorityInput { Name = Str(r!["name"]), Factor = Str(r["factor"]), ResponseHours = Str(r["response_hours"]), UpdateDays = Str(r["update_days"]) }).ToList(),
                    Categories = b["categories"]!.AsArray().Select(r => new SlaCategoryInput { Name = Str(r!["name"]), Factor = Str(r["factor"]) }).ToList(),
                    Statuses = b["statuses"]!.AsArray().Select(r => new SlaStatusInput { Name = Str(r!["name"]), State = Str(r["state"]) }).ToList(),
                    WeekendDays = b["settings"]!["weekend_days"]!.AsArray().Select(x => x!.GetValue<string>()).ToList(),
                    AtRiskThresholdPct = Str(b["settings"]!["at_risk_threshold_pct"]),
                    BreachThresholdPct = Str(b["settings"]!["breach_threshold_pct"]),
                    PriorityFactorMode = Str(b["settings"]!["priority_factor_mode"]),
                };
                var (errors, clean) = SlaConfigService.Validate(input);
                if (errors.Count > 0) return new JsonObject { ["errors"] = Strings(errors) };
                var engine = new JsonObject();
                foreach (var kv in clean.Settings.Where(k => k.Key != "weekend_days")) engine[kv.Key] = kv.Value;
                return new JsonObject
                {
                    ["errors"] = new JsonArray(),
                    ["clean"] = new JsonObject
                    {
                        ["stages"] = new JsonArray(clean.Stages.Select(s => (JsonNode)new JsonObject { ["name"] = s.Name, ["stage_order"] = s.StageOrder, ["is_pipeline"] = s.IsPipeline ? 1 : 0, ["br_days"] = s.BrDays, ["incident_days"] = s.IncidentDays, ["effort_days"] = s.EffortDays }).ToArray()),
                        ["priorities"] = new JsonArray(clean.Priorities.Select(s => (JsonNode)new JsonObject { ["name"] = s.Name, ["factor"] = s.Factor, ["response_hours"] = s.ResponseHours, ["update_days"] = s.UpdateDays }).ToArray()),
                        ["categories"] = new JsonArray(clean.Categories.Select(s => (JsonNode)new JsonObject { ["name"] = s.Name, ["factor"] = s.Factor }).ToArray()),
                        ["statuses"] = new JsonArray(clean.Statuses.Select(s => (JsonNode)new JsonObject { ["name"] = s.Name, ["state"] = s.State }).ToArray()),
                        ["weekend_days"] = new JsonArray(clean.Settings["weekend_days"].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => (JsonNode)JsonValue.Create(double.Parse(x, CultureInfo.InvariantCulture))!).ToArray()),
                        ["engine_settings"] = engine,
                    },
                };
            }
            case "scenario":
                return ScenarioRunner.Run(c);
            default:
                throw new NotSupportedException(kind);
        }
    }

    private sealed record Subject(string? Status, string? Priority, string? BusinessCategory, string? Stage, DateOnly? DeploymentPlannedEndDate) : ILegacySlaSubject;
}

internal static class Json
{
    private static double N(JsonNode? n) => double.Parse(n!.ToJsonString(), CultureInfo.InvariantCulture);

    public static bool DeepEqual(JsonNode? a, JsonNode? b)
    {
        if (a is null || b is null) return a is null && b is null;
        switch (a)
        {
            case JsonObject oa when b is JsonObject ob:
                foreach (var key in oa.Select(k => k.Key).Union(ob.Select(k => k.Key)))
                {
                    if (!DeepEqual(oa[key], ob[key])) return false;
                }
                return true;
            case JsonArray aa when b is JsonArray ab:
                return aa.Count == ab.Count && aa.Zip(ab).All(p => DeepEqual(p.First, p.Second));
            case JsonValue va when b is JsonValue vb:
                var ka = va.GetValueKind();
                var kb = vb.GetValueKind();
                if (ka == JsonValueKind.Number && kb == JsonValueKind.Number)
                {
                    var x = N(va);
                    var y = N(vb);
                    return x == y || Math.Abs(x - y) <= 1e-9 * Math.Max(1, Math.Abs(x));
                }
                return va.ToJsonString() == vb.ToJsonString();
            default:
                return false;
        }
    }
}


