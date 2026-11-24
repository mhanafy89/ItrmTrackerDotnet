using ItrmTracker.Application.Import;
using ItrmTracker.Application.Query;
using ItrmTracker.Application.Sla;
using ItrmTracker.Application.Tracker;
using ItrmTracker.Application.Validation;
using ItrmTracker.Domain.Constants;
using ItrmTracker.Infrastructure.Identity;

namespace ItrmTracker.Tests;

public class ScryptTests
{
    [Fact]
    public void Matches_the_RFC_7914_test_vector()
    {
        var key = Scrypt.DeriveKey("password"u8.ToArray(), "NaCl"u8.ToArray(), 1024, 8, 16, 64);
        Assert.Equal("fdbabe1c9d3472007856e7190d01e9fe7c6ad7cbc8237830e77376634b3731622eaf30d92e22a3886ff109279d9830dac727afb94a83ee6d8360cbdfa2cc0640",
            Convert.ToHexString(key).ToLowerInvariant());
    }

    [Fact]
    public void Verifies_a_hash_created_by_the_legacy_Nodejs_app()
    {
        // crypto.scryptSync("admin123", "00112233445566778899aabbccddeeff", 64) – format "saltHex$hashHex".
        const string legacy = "00112233445566778899aabbccddeeff$f33984f56f35ec026923f926b3110174c78bc32521bcb7811c08d90a582281af7ce63906919cb55dba8d2d6e1eb4334e2a25220419c921115c5240ea57caad28";
        Assert.True(Scrypt.IsLegacyFormat(legacy));
        Assert.True(Scrypt.VerifyLegacy("admin123", legacy));
        Assert.False(Scrypt.VerifyLegacy("admin124", legacy));
    }

    [Fact]
    public void Legacy_format_round_trips()
    {
        var hash = Scrypt.HashLegacy("S3cure!pass");
        Assert.True(Scrypt.VerifyLegacy("S3cure!pass", hash));
        Assert.False(Scrypt.VerifyLegacy("wrong", hash));
    }
}

public class RequestValidationTests
{
    private static Dictionary<string, string?> Body(params (string Key, string? Value)[] values) => values.ToDictionary(v => v.Key, v => v.Value);

    [Fact]
    public void Title_is_required()
    {
        Assert.Contains("Title is required.", RequestValidation.ValidateBusinessRequest(Body(("title", "   "))));
        Assert.Empty(RequestValidation.ValidateBusinessRequest(Body(("title", "Ok"))));
    }

    [Fact]
    public void Dates_must_be_valid_and_within_range()
    {
        var errors = RequestValidation.ValidateBusinessRequest(Body(("title", "t"), ("submission_date", "2024-13-01"), ("closed_date", "2019-12-31")));
        Assert.Contains("Submission Date must be a valid date (YYYY-MM-DD).", errors);
        Assert.Contains("Closed Date must be between 2020-01-01 and 2035-12-31.", errors);
    }

    [Fact]
    public void Pipeline_dates_are_checked_against_both_neighbours_skipping_blanks()
    {
        var errors = RequestValidation.ValidateBusinessRequest(Body(("title", "t"), ("dev_planned_start_date", "2025-03-10"), ("dev_planned_end_date", "2025-03-01"), ("cab_date", "2025-04-01")));
        Assert.Contains("Dev Planned End Date must be on or after Dev Planned Start Date.", errors);
        Assert.Contains("Dev Planned Start Date must be on or before Dev Planned End Date.", errors);
        Assert.Equal(2, errors.Count);
    }

    [Fact]
    public void Incidents_ignore_analysis_dates()
    {
        Assert.Empty(RequestValidation.ValidateIncident(Body(("title", "t"), ("analysis_planned_start_date", "garbage"))));
    }
}

public class QueryConsoleGuardTests
{
    [Theory]
    [InlineData("SELECT * FROM business_requests", null)]
    [InlineData("WITH x AS (SELECT 1 AS a) SELECT * FROM x", null)]
    [InlineData("SELECT '--not a comment' AS a", null)]
    [InlineData("DELETE FROM users", "Only SELECT queries are allowed.")]
    [InlineData("SELECT 1; SELECT 2", "Multiple statements are not allowed.")]
    [InlineData("SELECT 1 -- x", "Comments are not allowed in queries.")]
    [InlineData("SELECT * INTO t FROM x", "Only read-only SELECT queries are allowed.")]
    [InlineData("SELECT updated_at FROM business_requests", null)]
    public void Matches_the_original_rules(string sql, string? expected) => Assert.Equal(expected, QueryConsoleService.CheckReadOnly(sql));

    [Theory]
    [InlineData("SELECT password_hash FROM users")]
    [InlineData("SELECT * FROM dbo.[users]")]
    [InlineData("select token from sessions")]
    public void Blocks_sensitive_tables(string sql) => Assert.NotNull(QueryConsoleService.CheckReadOnly(sql, new[] { "users", "sessions" }));

    [Fact]
    public void Does_not_block_similarly_named_columns() =>
        Assert.Null(QueryConsoleService.CheckReadOnly("SELECT users_count, created_by FROM business_requests", new[] { "users", "sessions" }));
}

public class ImportParsingTests
{
    [Theory]
    [InlineData("2024-05-06", "2024-05-06")]
    [InlineData("45000", "2023-03-15")]
    [InlineData("05/11/2024", "2024-11-05")]
    [InlineData("13/05/2024", "2024-05-13")]
    [InlineData("5-13-2024", "2024-05-13")]
    [InlineData("2024/5/6", "2024-05-06")]
    [InlineData("", null)]
    public void NormalizeDate_accepts_the_original_formats(string input, string? expected) => Assert.Equal(expected, ImportService.NormalizeDate(input).Value);

    [Fact]
    public void NormalizeDate_reports_unrecognizable_values() => Assert.Equal("\"abc\" is not a recognizable date", ImportService.NormalizeDate("abc").Error);

    [Theory]
    [InlineData("Yes", 1)]
    [InlineData(" TRUE ", 1)]
    [InlineData("\u2713", 1)]
    [InlineData("x", 1)]
    [InlineData("no", 0)]
    [InlineData("", 0)]
    public void ParseBool_matches_the_original_truthy_list(string input, int expected) => Assert.Equal(expected, ImportService.ParseBool(input));

    [Fact]
    public void Csv_handles_quotes_commas_newlines_and_bom()
    {
        var rows = CsvParser.Parse("\uFEFFa,\"b, c\",\"multi\nline\",\"esc \"\"q\"\"\"\r\n1,2,3,4\n\n");
        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { "a", "b, c", "multi\nline", "esc \"q\"" }, rows[0]);
    }

    [Fact]
    public void SuggestMapping_uses_labels_and_aliases()
    {
        var svc = new ImportService(null!, null!, null!);
        var mapping = svc.SuggestMapping("business_requests", new[] { "BR No.", "Subject", "Go Live", "Dept" });
        Assert.Equal("BR No.", mapping["br_no"]);
        Assert.Equal("Subject", mapping["title"]);
        Assert.Equal("Go Live", mapping["deployment_planned_end_date"]);
        Assert.Equal("Dept", mapping["business_unit"]);
        Assert.Null(mapping["cr"]);
    }
}

public class LegacySlaCalculatorTests
{
    private static SlaConfiguration Config() => new(
        new[]
        {
            new SlaStageDefinition("Analysis", 1, true, 5, 2, 2), new SlaStageDefinition("Development", 3, true, 15, 5, 8),
            new SlaStageDefinition("QA", 4, true, 7, 3, 4), new SlaStageDefinition("Deployment", 7, true, 3, 2, 1),
        },
        new[] { new SlaPriorityDefinition("High", 0.7, 4, 2), new SlaPriorityDefinition("Medium", 1, 8, 7) },
        new[] { new SlaCategoryDefinition("Regulatory", 0.8) },
        new[] { new SlaStatusDefinition("In Progress", "Active"), new SlaStatusDefinition("On Hold", "Paused"), new SlaStatusDefinition("Completed", "Done") },
        new SlaSettingsModel(new[] { 5, 6 }, EngineSettings.Default));

    private sealed record Subject(string? Status, string? Priority, string? BusinessCategory, string? Stage, DateOnly? DeploymentPlannedEndDate) : ILegacySlaSubject;

    [Fact]
    public void Counts_remaining_pipeline_days_with_priority_and_category_factors()
    {
        var r = new LegacySlaCalculator(Config()).Compute(new Subject("In Progress", "High", "Regulatory", "Development", new DateOnly(2026, 12, 1)), TrackerKind.BusinessRequest, new DateOnly(2026, 11, 22));
        // 15×0.56 → 9, 7×0.56 → 4, 3×0.56 → 2 (ceil, min 1)
        Assert.Equal(new[] { 9d, 4d, 2d }, r.SlaBreakdown.Select(b => b.Days));
        Assert.Equal(15, r.SlaRemainingDays);
        Assert.Equal(13, r.SlaEffortDays);
        Assert.Equal(SlaFlags.AtRisk, r.SlaFlag);
    }

    [Fact]
    public void Done_and_paused_states_short_circuit()
    {
        var calc = new LegacySlaCalculator(Config());
        Assert.Equal(SlaFlags.Done, calc.Compute(new Subject("Completed", null, null, "QA", null), TrackerKind.Incident, new DateOnly(2026, 11, 22)).SlaFlag);
        Assert.Equal(SlaFlags.Paused, calc.Compute(new Subject("On Hold", null, null, "QA", new DateOnly(2027, 1, 1)), TrackerKind.Incident, new DateOnly(2026, 11, 22)).SlaFlag);
        Assert.Equal(SlaFlags.NoPlan, calc.Compute(new Subject("In Progress", null, null, "QA", null), TrackerKind.Incident, new DateOnly(2026, 11, 22)).SlaFlag);
    }
}

public class HistoryAndFilterTests
{
    [Fact]
    public void History_diff_lists_changed_columns_between_consecutive_snapshots()
    {
        var rows = new List<HistoryRow>
        {
            new(2, DateTime.UtcNow, "Bob", "update", "{\"title\":\"New\",\"estimated\":1,\"updated_at\":\"2026-01-02 00:00:00\"}"),
            new(1, DateTime.UtcNow, "Alice", "create", "{\"title\":\"Old\",\"estimated\":0,\"updated_at\":\"2026-01-01 00:00:00\"}"),
        };
        var view = HistoryDiffer.Build(rows, TrackerColumns.BusinessRequestTable, s => s);
        Assert.Equal("Updated", view[0].ChangeLabel);
        Assert.Contains(view[0].Changes, c => c.Label == "Title" && c.Before == "Old" && c.After == "New");
        Assert.Contains(view[0].Changes, c => c.Label == "Estimated" && c.Before == "No" && c.After == "Yes");
        Assert.DoesNotContain(view[0].Changes, c => c.Label == "Last Updated");
        Assert.Empty(view[1].Changes);
    }

    [Fact]
    public void Filters_default_to_open_records_and_sort_empty_values_last()
    {
        var rows = new List<TrackerRecord>
        {
            new() { Id = 1, Title = "b", Status = "Completed" },
            new() { Id = 2, Title = "a", Status = "In Progress", Cr = null },
            new() { Id = 3, Title = "c", Status = "In Progress", Cr = "CR-1" },
        };
        var open = TrackerFilter.Apply(rows, new TrackerQuery());
        Assert.Equal(new[] { 2, 3 }, open.Select(r => r.Id));
        Assert.Equal(new[] { 3, 2 }, TrackerFilter.Sort(open, "cr", "asc", TrackerColumns.BusinessRequestTable).Select(r => r.Id));
        Assert.Equal(new[] { 3, 2 }, TrackerFilter.Sort(open, "cr", "desc", TrackerColumns.BusinessRequestTable).Select(r => r.Id));
        Assert.Equal(new[] { 1 }, TrackerFilter.Apply(rows, new TrackerQuery { View = "closed" }).Select(r => r.Id));
    }

    [Fact]
    public void Closed_statuses_include_the_legacy_spelling() => Assert.True(TrackerRules.IsDoneStatus(" Compeleted "));
}
