using System.Globalization;
using ItrmTracker.Application.Common;
using ItrmTracker.Domain.Constants;

namespace ItrmTracker.Application.Validation;

/// <summary>A pipeline date field and its user-facing label.</summary>
public sealed record DateFieldDefinition(string Key, string Label);

/// <summary>
/// Server-side validation mirroring the rules built into the original Excel tracker (port of routes/validation.js).
/// This is the authoritative check – client-side validation is only a convenience. Error messages are identical.
/// </summary>
public static class RequestValidation
{
    public static readonly IReadOnlyList<DateFieldDefinition> PipelineFields = new[]
    {
        new DateFieldDefinition("analysis_planned_start_date", "Analysis Planned Start Date"),
        new DateFieldDefinition("analysis_planned_end_date", "Analysis Planned End Date"),
        new DateFieldDefinition("dev_planned_start_date", "Dev Planned Start Date"),
        new DateFieldDefinition("dev_planned_end_date", "Dev Planned End Date"),
        new DateFieldDefinition("qa_planned_start_date", "QA Planned Start Date"),
        new DateFieldDefinition("qa_planned_end_date", "QA Planned End Date"),
        new DateFieldDefinition("uat_planned_start_date", "UAT Planned Start Date"),
        new DateFieldDefinition("uat_planned_date", "UAT Planned End Date"),
        new DateFieldDefinition("cab_date", "CAB Date"),
        new DateFieldDefinition("deployment_planned_end_date", "Deployment Planned End Date"),
    };

    public static readonly IReadOnlyList<DateFieldDefinition> IncidentPipelineFields =
        PipelineFields.Where(f => f.Key is not ("analysis_planned_start_date" or "analysis_planned_end_date")).ToList();

    private static readonly string MinDate = TrackerRules.MinDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static readonly string MaxDate = TrackerRules.MaxDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static bool IsValidDateString(string? s) => LegacyFormat.TryParseIsoDate(s, out _);

    private static string? Get(IReadOnlyDictionary<string, string?> body, string key) =>
        body.TryGetValue(key, out var v) ? v : null;

    /// <summary>
    /// Validates the pipeline date fields against BOTH neighbours (the stage before and after), the same two-sided rule
    /// as the spreadsheet's data validation. A blank neighbour is skipped, so fields can be filled out of order.
    /// </summary>
    public static List<string> ValidatePipelineDates(IReadOnlyDictionary<string, string?> body, IReadOnlyList<DateFieldDefinition>? fields = null)
    {
        fields ??= PipelineFields;
        var errors = new List<string>();

        foreach (var (key, label) in fields)
        {
            var value = Get(body, key);
            if (string.IsNullOrEmpty(value)) continue;
            if (!IsValidDateString(value))
            {
                errors.Add($"{label} must be a valid date (YYYY-MM-DD).");
                continue;
            }
            if (string.CompareOrdinal(value, MinDate) < 0 || string.CompareOrdinal(value, MaxDate) > 0)
            {
                errors.Add($"{label} must be between {MinDate} and {MaxDate}.");
            }
        }

        for (var i = 0; i < fields.Count; i++)
        {
            var (key, label) = fields[i];
            var value = Get(body, key);
            if (!IsValidDateString(value)) continue;

            if (i > 0)
            {
                var prev = fields[i - 1];
                var prevValue = Get(body, prev.Key);
                if (IsValidDateString(prevValue) && string.CompareOrdinal(value, prevValue) < 0)
                {
                    errors.Add($"{label} must be on or after {prev.Label}.");
                }
            }
            if (i < fields.Count - 1)
            {
                var next = fields[i + 1];
                var nextValue = Get(body, next.Key);
                if (IsValidDateString(nextValue) && string.CompareOrdinal(value, nextValue) > 0)
                {
                    errors.Add($"{label} must be on or before {next.Label}.");
                }
            }
        }
        return errors;
    }

    public static List<string> ValidateSimpleDate(IReadOnlyDictionary<string, string?> body, string key, string label)
    {
        var value = Get(body, key);
        if (string.IsNullOrEmpty(value)) return new List<string>();
        if (!IsValidDateString(value)) return new List<string> { $"{label} must be a valid date (YYYY-MM-DD)." };
        if (string.CompareOrdinal(value, MinDate) < 0 || string.CompareOrdinal(value, MaxDate) > 0)
        {
            return new List<string> { $"{label} must be between {MinDate} and {MaxDate}." };
        }
        return new List<string>();
    }

    public static List<string> ValidateBusinessRequest(IReadOnlyDictionary<string, string?> body)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Get(body, "title"))) errors.Add("Title is required.");
        errors.AddRange(ValidateSimpleDate(body, "submission_date", "Submission Date"));
        errors.AddRange(ValidateSimpleDate(body, "regulatory_deadline", "Regulatory Deadline"));
        errors.AddRange(ValidateSimpleDate(body, "closed_date", "Closed Date"));
        errors.AddRange(ValidatePipelineDates(body));
        return errors;
    }

    public static List<string> ValidateIncident(IReadOnlyDictionary<string, string?> body)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Get(body, "title"))) errors.Add("Title is required.");
        errors.AddRange(ValidateSimpleDate(body, "closed_date", "Closed Date"));
        errors.AddRange(ValidatePipelineDates(body, IncidentPipelineFields));
        return errors;
    }
}
