using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using ItrmTracker.Domain.Constants;

/// <summary>Randomized (seeded, reproducible) inputs for every ported function.</summary>
internal sealed class CaseGenerator
{
    private readonly Random _r;
    private readonly long _base = new DateTimeOffset(new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Local)).ToUnixTimeMilliseconds();

    public CaseGenerator(Random r) => _r = r;

    private T Pick<T>(params T[] items) => items[_r.Next(items.Length)];
    private bool Chance(double p) => _r.NextDouble() < p;
    private long Instant(int maxDays = 60) => _base + (long)_r.Next(0, maxDays) * 86400000 + (long)_r.Next(0, 24) * 3600000 + (long)_r.Next(0, 60) * 60000;
    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private DateOnly Day(int from = 2024, int to = 2027) => new(_r.Next(from, to + 1), _r.Next(1, 13), _r.Next(1, 29));
    private double Effort() => Math.Round(_r.NextDouble() * 20, Pick(0, 1, 2));

    private JsonArray Weekend()
    {
        int[] w = _r.Next(10) switch
        {
            < 3 => new[] { 5, 6 },
            < 6 => new[] { 0, 6 },
            < 8 => Enumerable.Range(0, 7).OrderBy(_ => _r.Next()).Take(_r.Next(0, 4)).ToArray(),
            8 => Enumerable.Range(0, 7).ToArray(),
            _ => Array.Empty<int>(),
        };
        return new JsonArray(w.Select(x => (JsonNode)x).ToArray());
    }

    /// <summary>Weekend for the legacy calculation: never all seven days (rejected by the settings validation; the legacy code crashes on it).</summary>
    private JsonArray LegacyWeekend()
    {
        var w = Weekend();
        return w.Count >= 7 ? new JsonArray(5, 6) : w;
    }

    private JsonObject EngineRaw()
    {
        var o = new JsonObject();
        if (Chance(0.6)) o["at_risk_threshold_pct"] = Pick("80", "60", "95", "abc", "0", "-5", "120", " 70 ", "1e2");
        if (Chance(0.6)) o["breach_threshold_pct"] = Pick("100", "90", "150", "x", "0", "75", "1000", "1001");
        if (Chance(0.7)) o["priority_factor_mode"] = Pick("multiplier", "divisor", "DIVISOR", " Multiplier ", "x", "");
        if (Chance(0.5)) o["enforce_stage_order"] = Pick("true", "false", "TRUE", "1", "yes", "no", "maybe", "");
        if (Chance(0.5)) o["auto_start_next_stage"] = Pick("true", "false", "0", "on", "");
        return o;
    }

    private JsonObject StageRow(int id, string name, int order, string? status = null)
    {
        status ??= Pick(StageStatus.All.ToArray());
        var started = Chance(0.7) ? Instant(30) : (long?)null;
        var o = new JsonObject
        {
            ["id"] = id,
            ["stage_name"] = name,
            ["stage_order"] = order,
            ["status"] = status,
            ["stage_effort_days"] = status == StageStatus.NotYetEstimated || Chance(0.1) ? null : Chance(0.1) ? 0 : Math.Max(0.5, Effort()),
            ["actual_effort_days"] = Effort(),
            ["started_at"] = started,
            ["completed_at"] = status == StageStatus.Completed && Chance(0.85) ? Instant(40) : null,
            ["paused_at"] = status == StageStatus.OnHold ? Instant(40) : null,
            ["resume_at"] = Chance(0.2) ? Instant(40) : null,
            ["reopen_count"] = _r.Next(0, 3),
            ["is_backfilled"] = Chance(0.15),
        };
        return o;
    }

    private JsonArray Segments(bool pauses)
    {
        var arr = new JsonArray();
        var t = Instant(20);
        var n = _r.Next(0, 4);
        for (var i = 0; i < n; i++)
        {
            var start = t + (long)_r.Next(0, 5) * 86400000 + _r.Next(0, 86400000);
            var open = i == n - 1 && Chance(0.4);
            var end = open ? (long?)null : start + (long)_r.Next(0, 8) * 86400000 + _r.Next(0, 86400000);
            arr.Add(pauses ? new JsonObject { ["paused_at"] = start, ["resumed_at"] = end } : new JsonObject { ["started_at"] = start, ["ended_at"] = end });
            t = end ?? start;
        }
        return arr;
    }

    public List<ParityCase> Generate(int n)
    {
        var list = new List<ParityCase>();
        void Add(string kind, JsonObject input) => list.Add(new ParityCase(kind, input));
        var stageNames = new[] { "Analysis", "BA", "Development", "QA", "UAT", "CAB", "Deployment" };

        for (var i = 0; i < n; i++)
        {
            Add("workingDays", new JsonObject { ["from"] = Chance(0.05) ? null : Instant(), ["to"] = Chance(0.05) ? null : Instant(), ["weekend"] = Weekend() });
            Add("addWorkingDays", new JsonObject { ["start"] = Iso(Day()), ["days"] = Pick(0, 1, 5, 6.67, 0.01, 3.5, 12, _r.Next(0, 40) + Math.Round(_r.NextDouble(), 2)), ["weekend"] = Weekend() });

            var now = Instant(70) + 40L * 86400000;
            Add("stageSla", new JsonObject
            {
                ["stage"] = StageRow(1, Pick(stageNames), _r.Next(1, 8)),
                ["work"] = Segments(false),
                ["pauses"] = Segments(true),
                ["factor"] = Pick(0, 0.4, 0.7, 1, 1.5, 2, 2.5),
                ["settings"] = EngineRaw(),
                ["weekend"] = Weekend(),
                ["now"] = now,
            });

            var count = _r.Next(1, 7);
            var stages = new JsonArray();
            for (var s = 0; s < count; s++)
            {
                stages.Add(new JsonObject { ["stage"] = StageRow(s + 1, stageNames[s], s + 1), ["work"] = Segments(false), ["pauses"] = Segments(true) });
            }
            Add("summarize", new JsonObject { ["stages"] = stages, ["factor"] = Pick(0.4, 1, 1.5), ["settings"] = EngineRaw(), ["weekend"] = Weekend(), ["now"] = now });

            var planStages = new JsonArray();
            for (var s = 0; s < count; s++) planStages.Add(StageRow(s + 1, stageNames[s], s + 1));
            var target = planStages[_r.Next(count)]!.DeepClone();
            Add("plan", new JsonObject
            {
                ["stage"] = target,
                ["stages"] = planStages,
                ["toStatus"] = Pick(StageStatus.All.ToArray()),
                ["plannedEffort"] = Chance(0.5) ? null : Pick(0, 2, 5, 7.5, 10),
                ["settings"] = EngineRaw(),
                ["force"] = Chance(0.3),
            });

            Add("validateStageInput", new JsonObject
            {
                ["stage"] = Chance(0.1) ? null : Pick("", "Development", " QA ", new string('x', 201), "UAT"),
                ["status"] = Chance(0.1) ? null : Pick("", "in progress", "ON HOLD", "Completed", "done", "Not Yet Estimated", " cancelled "),
                ["plannedEffort"] = Chance(0.4) ? null : Pick("", "5", "0", "-1", "abc", "1000", " 2.345 ", "999", "0.004"),
                ["actualEffortDays"] = Chance(0.5) ? null : Pick("", "3", "0", "-2", "10000", "9999", "1.005"),
                ["note"] = Chance(0.5) ? null : Pick("", "  ", "waiting for vendor", new string('n', 501), " trimmed "),
            });

            Add("engineSettings", new JsonObject { ["raw"] = EngineRaw() });

            var pipeline = new JsonArray();
            var pc = _r.Next(1, 7);
            for (var s = 0; s < pc; s++) pipeline.Add(new JsonObject { ["name"] = stageNames[s], ["stage_order"] = (s + 1) * Pick(1, 1, 2), ["effort_days"] = Pick(0, 0, 2, 5, 0.5) });
            Add("initialStages", new JsonObject
            {
                ["pipeline"] = pipeline,
                ["legacyStage"] = Chance(0.2) ? null : new JsonObject { ["name"] = Pick("Development", "Closed", "Draft", "QA"), ["stage_order"] = _r.Next(0, 10) },
                ["statusName"] = Pick("In Progress", "On Hold", "hold", "Completed", "Cancelled", "canceled", "Not Started", "", "Pending for Approval", "Not Yet Estimated"),
                ["statusState"] = Chance(0.2) ? null : Pick("Active", "Paused", "Done"),
            });

            Add("legacySla", LegacySlaCase());
            Add("validateRequest", ValidateRequestCase());
            Add("normalizeDate", new JsonObject { ["value"] = DateInput() });
            Add("parseBool", new JsonObject { ["value"] = Chance(0.05) ? null : Pick("1", "0", "y", "Yes", " TRUE ", "t", "x", "\u2713", "checked", "on", "off", "no", "", "2") });
            Add("normalizeHeader", new JsonObject { ["value"] = Pick("BR No.", "Deployment Planned End", "  Tech-Owner ", "Prioirty", "CR#", "Go Live", "Incident ID", "status", "N\u00e9wgen", "") });
            Add("suggestMapping", SuggestCase());
            Add("checkReadOnly", new JsonObject { ["sql"] = SqlCase() });
            Add("csv", new JsonObject { ["text"] = CsvCase() });
            Add("slaConfigValidate", SlaConfigCase());
            if (i % 40 == 0) Add("scryptHash", new JsonObject { ["password"] = Pick("admin123", "S3cure!pass", "p\u00e4ssw\u00f6rd", "x", new string('a', 70)) + i });
            if (i % 2 == 0) Add("scenario", ScenarioCase());
        }
        return list;
    }

    private static readonly string[] ScenarioStatuses = { "Not Started", "Not Yet Estimated", "In Progress", "Pending for Approval", "On Hold", "Completed", "Cancelled" };

    private JsonObject ScenarioConfig()
    {
        var names = new[] { "Planning", "BA", "Development", "QA", "UAT", "Deployment" };
        var stages = new JsonArray();
        for (var s = 0; s < names.Length; s++)
        {
            if (s > 0 && Chance(0.15)) continue;
            stages.Add(new JsonObject { ["name"] = names[s], ["stage_order"] = s + 1, ["is_pipeline"] = Chance(0.9) ? 1 : 0, ["effort_days"] = Pick(0, 2, 3, 5, 8, 15, 0.5) });
        }
        stages.Add(new JsonObject { ["name"] = "Closed", ["stage_order"] = 9, ["is_pipeline"] = 0, ["effort_days"] = 0 });
        var priorities = new JsonArray(
            new JsonObject { ["name"] = "Critical", ["factor"] = Pick(1.5, 0.4) },
            new JsonObject { ["name"] = "High", ["factor"] = Pick(1.25, 0.7) },
            new JsonObject { ["name"] = "Medium", ["factor"] = 1 },
            new JsonObject { ["name"] = "Low", ["factor"] = Pick(0.75, 1.5) });
        var statuses = new JsonArray(
            new JsonObject { ["name"] = "Not Started", ["state"] = "Active" }, new JsonObject { ["name"] = "Not Yet Estimated", ["state"] = "Active" },
            new JsonObject { ["name"] = "In Progress", ["state"] = "Active" }, new JsonObject { ["name"] = "Pending for Approval", ["state"] = "Active" },
            new JsonObject { ["name"] = "On Hold", ["state"] = "Paused" }, new JsonObject { ["name"] = "Completed", ["state"] = "Done" },
            new JsonObject { ["name"] = "Cancelled", ["state"] = "Done" }, new JsonObject { ["name"] = "Reject", ["state"] = "Active" });
        var settings = new JsonObject
        {
            ["weekend_days"] = Pick(new JsonArray(0, 6), new JsonArray(5, 6)),
            ["priority_factor_mode"] = Pick("divisor", "multiplier"),
            ["enforce_stage_order"] = Pick("true", "false"),
            ["auto_start_next_stage"] = Pick("true", "false", "false"),
        };
        if (Chance(0.3)) settings["at_risk_threshold_pct"] = Pick("60", "80", "90");
        return new JsonObject { ["stages"] = stages, ["priorities"] = priorities, ["statuses"] = statuses, ["settings"] = settings };
    }

    private JsonObject ScenarioCase()
    {
        var config = ScenarioConfig();
        var initialConfig = config.DeepClone();
        var stageNames = config["stages"]!.AsArray().Select(s => s!["name"]!.GetValue<string>()).ToArray();
        var requestCount = _r.Next(1, 4);
        var requests = new JsonArray();
        for (var id = 1; id <= requestCount; id++)
        {
            requests.Add(new JsonObject
            {
                ["id"] = id,
                ["priority"] = Pick<string?>("Critical", "High", "Medium", "Low", null, "Urgent"),
                ["stage"] = Pick<string?>(stageNames.Append("Draft").Append(null).ToArray()),
                ["status"] = Pick<string?>(ScenarioStatuses.Append("Reject").Append(null).ToArray()),
            });
        }

        var steps = new JsonArray();
        var t = _base + 22L * 86400000 + 9L * 3600000;
        var count = _r.Next(10, 30);
        for (var i = 0; i < count; i++)
        {
            t += (long)_r.Next(0, 4) * 86400000 + (long)_r.Next(0, 8) * 3600000;
            var id = _r.Next(1, requestCount + 1);
            var roll = _r.Next(100);
            JsonObject step;
            if (roll < 15) step = new JsonObject { ["op"] = "get", ["id"] = id };
            else if (roll < 65)
            {
                var status = Pick(StageStatus.All.ToArray());
                step = new JsonObject
                {
                    ["op"] = "update", ["id"] = Chance(0.03) ? 99 : id, ["user"] = Pick("Alice", "Bob"),
                    ["input"] = new JsonObject
                    {
                        ["stage"] = Chance(0.05) ? "Nope" : Pick(stageNames),
                        ["status"] = Chance(0.1) ? status.ToUpperInvariant() : status,
                        ["plannedEffort"] = Chance(0.3) ? Pick("2", "5", "7.5", "12", "0", "-1") : null,
                        ["actualEffortDays"] = status == StageStatus.Completed && Chance(0.3) ? Pick("3", "0", "4.5") : Chance(0.03) ? "2" : null,
                        ["note"] = Chance(0.3) ? Pick("waiting for vendor", " trimmed ", "") : null,
                    },
                };
            }
            else if (roll < 80) step = new JsonObject { ["op"] = "legacy", ["id"] = id, ["user"] = "Form User", ["stage"] = Pick<string?>(stageNames.Append("Draft").ToArray()), ["status"] = Pick<string?>(ScenarioStatuses.Append("Reject").ToArray()) };
            else if (roll < 85) step = new JsonObject { ["op"] = "backfill" };
            else if (roll < 91) step = new JsonObject { ["op"] = "dashboard", ["priority"] = Chance(0.2) ? "Medium" : null, ["stage"] = Chance(0.2) ? Pick(stageNames) : null, ["health"] = Chance(0.2) ? Pick(SlaHealth.OnTrack, SlaHealth.AtRisk, SlaHealth.Breached) : null };
            else if (roll < 96) step = new JsonObject { ["op"] = "history", ["id"] = id };
            else
            {
                // Pipeline change mid-flight: a stage leaves or joins the pipeline, effort or settings change.
                var newConfig = config.DeepClone().AsObject();
                var st = newConfig["stages"]!.AsArray();
                var target = st[_r.Next(st.Count - 1)]!;
                if (Chance(0.5)) target["is_pipeline"] = target["is_pipeline"]!.GetValue<int>() == 1 ? 0 : 1;
                else target["effort_days"] = Pick(0, 4, 6);
                newConfig["settings"]!["auto_start_next_stage"] = Pick("true", "false");
                config = newConfig;
                step = new JsonObject { ["op"] = "config", ["config"] = newConfig.DeepClone() };
            }
            step["at"] = t;
            steps.Add(step);
        }
        return new JsonObject { ["config"] = initialConfig, ["requests"] = requests, ["steps"] = steps };
    }

    private JsonObject LegacySlaCase()
    {
        var pool = new[] { "Analysis", "BA", "Development", "QA", "UAT", "CAB", "Deployment", "Newgen", "DMO", "Closed", "Draft", "Demand" };
        var stages = new JsonArray();
        foreach (var name in pool.Where(_ => Chance(0.8)))
        {
            stages.Add(new JsonObject
            {
                ["name"] = name, ["stage_order"] = _r.Next(0, 9), ["is_pipeline"] = Chance(0.7) ? 1 : 0,
                ["br_days"] = _r.Next(0, 16), ["incident_days"] = _r.Next(0, 6), ["effort_days"] = Pick(0, 0.5, 1, 2, 8),
            });
        }
        var priorities = new JsonArray();
        foreach (var p in new[] { "Critical", "High", "Medium", "Low", "Very Low" }) if (Chance(0.8)) priorities.Add(new JsonObject { ["name"] = p, ["factor"] = Pick(0.4, 0.7, 1, 1.5, 2, 0.33) });
        var categories = new JsonArray();
        foreach (var c in new[] { "Normal", "Regulatory", "Fast Track", "Strategic Projects" }) if (Chance(0.8)) categories.Add(new JsonObject { ["name"] = c, ["factor"] = Pick(1, 0.8, 0.6, 1.5, 1.25) });
        var statuses = new JsonArray();
        foreach (var s in new[] { "Not Started", "In Progress", "On Hold", "Completed", "Cancelled", "Pending for Approval" }) statuses.Add(new JsonObject { ["name"] = s, ["state"] = Pick("Active", "Paused", "Done") });
        return new JsonObject
        {
            ["config"] = new JsonObject
            {
                ["stages"] = stages, ["priorities"] = priorities, ["categories"] = categories, ["statuses"] = statuses,
                ["settings"] = new JsonObject { ["weekend_days"] = LegacyWeekend() },
            },
            ["record"] = new JsonObject
            {
                ["status"] = Chance(0.1) ? null : Pick("Not Started", "In Progress", "On Hold", "Completed", "Cancelled", "Pending for Approval", "closed", "Unknown"),
                ["priority"] = Chance(0.1) ? null : Pick("Critical", "critcal", "High", "Medium", "Low", "Very Low", "Urgent"),
                ["business_category"] = Chance(0.2) ? null : Pick("Normal", "Regulatory", "Fast Track", "Strategic Projects", "Other"),
                ["stage"] = Chance(0.1) ? null : Pick(pool.Concat(new[] { "development", "Unknown stage" }).ToArray()),
                ["deployment_planned_end_date"] = Chance(0.3) ? null : Iso(Day(2026, 2027)),
            },
            ["type"] = Pick("requests", "incidents"),
            ["today"] = Iso(new DateOnly(2026, 11, 1).AddDays(_r.Next(0, 60))),
        };
    }

    private string? DateValue() => _r.Next(12) switch
    {
        0 => null,
        1 => "",
        2 => Pick("abc", "2024-13-01", "2024-1-5", "20240105", "2024-00-10"),
        3 => Pick("2019-12-31", "2036-01-01", "2020-01-01", "2035-12-31"),
        _ => Iso(Day(2021, 2030)),
    };

    private JsonObject ValidateRequestCase()
    {
        var body = new JsonObject
        {
            ["title"] = Pick("Title", "", "   ", null, "x"),
            ["submission_date"] = DateValue(),
            ["regulatory_deadline"] = DateValue(),
            ["closed_date"] = DateValue(),
        };
        foreach (var f in new[] { "analysis_planned_start_date", "analysis_planned_end_date", "dev_planned_start_date", "dev_planned_end_date", "qa_planned_start_date",
                     "qa_planned_end_date", "uat_planned_start_date", "uat_planned_date", "cab_date", "deployment_planned_end_date" })
        {
            if (Chance(0.6)) body[f] = DateValue();
        }
        return new JsonObject { ["body"] = body, ["incident"] = Chance(0.4) };
    }

    private string? DateInput() => _r.Next(10) switch
    {
        0 => Pick<string?>(null, "", "   "),
        1 => Pick("abc", "2024-13-01", "31/31/2024", "99/99/9999"),
        2 => _r.Next(30000, 60000).ToString(CultureInfo.InvariantCulture) + (Chance(0.5) ? "" : "." + _r.Next(0, 99)),
        3 => $"{_r.Next(1, 29):00}/{_r.Next(1, 13):00}/{_r.Next(2020, 2031)}",
        4 => $"{_r.Next(1, 13)}-{_r.Next(13, 29)}-{_r.Next(2020, 2031)}",
        5 => $"{_r.Next(13, 29)}.{_r.Next(1, 13)}.{_r.Next(2020, 2031)}",
        6 => $"{_r.Next(2020, 2031)}/{_r.Next(1, 13)}/{_r.Next(1, 29)}",
        7 => $"{_r.Next(1, 10)}/{_r.Next(1, 10)}/{_r.Next(1, 99)}",
        _ => " " + Iso(Day()) + " ",
    };

    private JsonObject SuggestCase()
    {
        var pool = new[] { "BR No", "BR", "CR No", "Title", "Subject", "Desc", "Business Unit", "Dept", "Category", "Requested By", "Priority", "Phase", "Status",
            "Owner", "Developer", "RM", "Regulator", "Newgen", "Estimated", "BA", "BRD", "Regulatory", "Reg Deadline", "Analysis Start", "Analysis",
            "Dev Start", "Development", "QA Start", "Testing", "UAT Start", "UAT", "CAB", "Go Live", "Closed On", "Incident No", "ID", "Name", "Random", "Submitted" };
        var headers = pool.OrderBy(_ => _r.Next()).Take(_r.Next(3, 20)).ToArray();
        return new JsonObject { ["target"] = Pick("business_requests", "incidents"), ["headers"] = new JsonArray(headers.Select(h => (JsonNode)h).ToArray()) };
    }

    private string SqlCase() => Pick(
        "SELECT * FROM business_requests",
        "select id, title from incidents where title = 'drop table'",
        "WITH x AS (SELECT 1 AS a) SELECT * FROM x",
        "SELECT 1; SELECT 2",
        "SELECT 1;",
        "SELECT 1 -- comment",
        "SELECT /* c */ 1",
        "SELECT '--not a comment' AS a",
        "DELETE FROM users",
        "SELECT * INTO t FROM users",
        "SELECT [update] FROM t",
        "SELECT \"delete\" FROM t",
        "update t set a=1",
        "SELECT name FROM sys.tables; EXEC xp_cmdshell 'dir'",
        "  select   count(*) from dbo.statuses  ",
        "SELECT * FROM OPENROWSET('x')",
        "SELECT sp_help",
        "SELECT updated_at FROM business_requests",
        "SELECT * FROM t WHERE a = 'it''s'",
        "SELECT waitfor FROM t",
        "SELECTX 1",
        "EXPLAIN SELECT 1",
        "select set_point from t",
        "SELECT * FROM t WHERE x = 'a;b'");

    private string CsvCase()
    {
        var sb = new StringBuilder();
        if (Chance(0.2)) sb.Append('\uFEFF');
        var rows = _r.Next(0, 5);
        for (var i = 0; i < rows; i++)
        {
            var cols = _r.Next(1, 5);
            for (var j = 0; j < cols; j++)
            {
                if (j > 0) sb.Append(',');
                sb.Append(Pick("a", "", "\"quoted, comma\"", "\"multi\nline\"", "\"esc \"\"q\"\"\"", "x y", "1.5", "\"\""));
            }
            sb.Append(Pick("\n", "\r\n", "\r", i == rows - 1 ? "" : "\n"));
        }
        if (Chance(0.2)) sb.Append(",,\n\n");
        return sb.ToString();
    }

    private JsonObject SlaConfigCase()
    {
        JsonNode? Num(params string?[] values) => Pick(values) is { } s ? JsonValue.Create(s) : null;
        var stages = new JsonArray();
        for (var i = 0; i < _r.Next(0, 5); i++)
        {
            stages.Add(new JsonObject
            {
                ["name"] = Pick("Analysis", "QA", "qa ", "", "Development", new string('s', 201)),
                ["stage_order"] = Num("1", "0", "99", "100", "1.5", "", "x"),
                ["is_pipeline"] = Chance(0.7),
                ["br_days"] = Num("5", "0", "365", "366", "-1", "2.5"),
                ["incident_days"] = Num("2", "0", "400"),
                ["effort_days"] = Num("2", "0.5", "999", "1000", "", "abc"),
            });
        }
        var priorities = new JsonArray();
        for (var i = 0; i < _r.Next(0, 4); i++)
        {
            priorities.Add(new JsonObject
            {
                ["name"] = Pick("Critical", "High", "", "high"),
                ["factor"] = Num("0.4", "0", "10", "10.5", "1"),
                ["response_hours"] = Num("8", "999", "1000", "-1", "4.5"),
                ["update_days"] = Num("7", "0", "365", "1.5"),
            });
        }
        var categories = new JsonArray();
        for (var i = 0; i < _r.Next(0, 3); i++) categories.Add(new JsonObject { ["name"] = Pick("Normal", "Regulatory", ""), ["factor"] = Num("1", "0", "0.8", "11") });
        var statuses = new JsonArray();
        for (var i = 0; i < _r.Next(0, 3); i++) statuses.Add(new JsonObject { ["name"] = Pick("Completed", "On Hold", "x"), ["state"] = Pick("Active", "Paused", "Done", "done", "") });
        var weekend = new JsonArray(Enumerable.Range(0, _r.Next(0, 4)).Select(_ => (JsonNode)Pick("5", "6", "0", "7", "1.5", "x", "")).ToArray());
        return new JsonObject
        {
            ["body"] = new JsonObject
            {
                ["stages"] = stages, ["priorities"] = priorities, ["categories"] = categories, ["statuses"] = statuses,
                ["settings"] = new JsonObject
                {
                    ["weekend_days"] = weekend,
                    ["at_risk_threshold_pct"] = Chance(0.5) ? null : Pick("70", "120", "abc", ""),
                    ["breach_threshold_pct"] = Chance(0.5) ? null : Pick("100", "60", "2000"),
                    ["priority_factor_mode"] = Chance(0.5) ? null : Pick("divisor", "x", "Multiplier"),
                },
            },
        };
    }
}
