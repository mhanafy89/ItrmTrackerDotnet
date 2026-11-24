using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ItrmTracker.Application.Abstractions;
using ItrmTracker.Application.Common;
using ItrmTracker.Application.Sla;
using ItrmTracker.Application.Tracker;
using ItrmTracker.Application.Validation;
using ItrmTracker.Domain.Entities;

namespace ItrmTracker.Application.Import;

/// <summary>Result of parsing an uploaded file.</summary>
public sealed class ParsedFile
{
    public string FileType { get; init; } = string.Empty;
    public string Filename { get; init; } = string.Empty;
    public List<string> Headers { get; init; } = new();
    public List<Dictionary<string, string>> Rows { get; init; } = new();
    public int TotalRows => Rows.Count;
    public Dictionary<string, string?>? SuggestedMapping { get; init; }
}

public sealed record NewLookupValue(string Field, string Label, string Table, string IdField, string Name);

public sealed record FieldDiff(string Field, string Label, string Current, string Uploaded);

/// <summary>Classification of one uploaded row: new / update / duplicate / invalid.</summary>
public sealed class ClassifiedRecord
{
    public int RowIndex { get; init; }
    public string Key { get; init; } = string.Empty;
    public string? KeyField { get; init; }
    public string Title { get; init; } = string.Empty;
    public Dictionary<string, string> Uploaded { get; init; } = new();
    public List<NewLookupValue> NewLookups { get; init; } = new();
    public List<string> MappedFields { get; init; } = new();
    public string Outcome { get; init; } = string.Empty;
    public string SuggestedAction { get; init; } = "skip";
    public int? EntityId { get; init; }
    public string? MatchedBy { get; init; }
    public List<FieldDiff> Diffs { get; init; } = new();
    public List<string> Errors { get; init; } = new();
    public Dictionary<string, string> Current { get; init; } = new();
}

public sealed record AnalysisSummary(int Total, int New, int Update, int Duplicate, int Invalid);

public sealed record AnalysisResult(string Target, List<ClassifiedRecord> Records, AnalysisSummary Summary);

public sealed record CommitSummary(int Total, int New, int Updated, int Duplicate, int Skipped, int Invalid, int Error);

public sealed record CommitError(int RowIndex, string Key, string Message);

public sealed record CommitResult(int BatchId, CommitSummary Summary, List<CommitError> Errors);

/// <summary>Persistence used by the import wizard.</summary>
public interface IImportRepository
{
    Task<Dictionary<string, List<LookupRow>>> LoadLookupsAsync(IEnumerable<string> tables, CancellationToken ct = default);
    Task<int> CreateLookupAsync(string table, string name, CancellationToken ct = default);

    /// <summary>SELECT TOP 1 * … WHERE {field} = @value ORDER BY id, as a raw snake_case row.</summary>
    Task<Dictionary<string, object?>?> FindByKeyAsync(TrackerKind kind, string field, string value, CancellationToken ct = default);
    Task<bool> KeyInUseAsync(TrackerKind kind, string field, string value, int? excludeId, CancellationToken ct = default);

    /// <summary>Inserts the entity columns, syncs the tech-owner junction and writes a "create" history row.</summary>
    Task<int> InsertEntityAsync(TrackerKind kind, IReadOnlyDictionary<string, object?> body, string? changedBy, CancellationToken ct = default);

    /// <summary>Updates the entity columns, syncs the tech-owner junction and writes an "update" history row.</summary>
    Task UpdateEntityAsync(TrackerKind kind, int id, IReadOnlyDictionary<string, object?> body, string? changedBy, CancellationToken ct = default);

    Task<int> CreateBatchAsync(ImportBatch batch, CancellationToken ct = default);
    Task AddBatchRowAsync(ImportBatchRow row, CancellationToken ct = default);
    Task CompleteBatchAsync(int batchId, CommitSummary summary, string summaryJson, CancellationToken ct = default);
    Task<List<ImportBatch>> ListBatchesAsync(int top, CancellationToken ct = default);
    Task<ImportBatch?> GetBatchAsync(int id, CancellationToken ct = default);
    Task<List<ImportBatchRow>> ListBatchRowsAsync(int batchId, CancellationToken ct = default);
}

public sealed record LookupRow(int Id, string Name);

public interface IImportService
{
    ParsedFile Parse(string? filename, byte[] content, string? target);
    Task<AnalysisResult> AnalyzeAsync(string target, IReadOnlyDictionary<string, string?> mapping, IReadOnlyList<Dictionary<string, string>> rows, CancellationToken ct = default);
    Task<CommitResult> CommitAsync(string target, string? filename, string? fileType, IReadOnlyDictionary<string, string?> mapping,
        IReadOnlyList<Dictionary<string, string>> rows, IReadOnlyDictionary<int, string> decisions, CancellationToken ct = default);
    Task<List<ImportBatch>> HistoryAsync(CancellationToken ct = default);
    Task<(ImportBatch Batch, List<ImportBatchRow> Rows)> HistoryDetailAsync(int id, CancellationToken ct = default);
    Dictionary<string, string?> SuggestMapping(string target, IReadOnlyList<string> headers);
    Dictionary<string, string> MapRow(string target, IReadOnlyDictionary<string, string?> mapping, IReadOnlyDictionary<string, string> row);
}

/// <summary>
/// Import &amp; merge wizard backend (port of routes/import.js): parse (csv/xlsx → rows) → analyze (map + classify each
/// row as new / update / duplicate / invalid, with field-level diffs) → commit (insert / update / skip per the user's
/// decisions, auto-creating missing lookup values, writing entity history and the import batch history).
/// Everything is re-validated on commit; the analysis shown to the user is never trusted for the writes.
/// </summary>
public sealed class ImportService : IImportService
{
    private static readonly HashSet<string> TrueValues = new() { "1", "y", "yes", "true", "t", "x", "\u2713", "checked", "on" };
    private static readonly Regex IsoDate = new(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.Compiled);
    private static readonly Regex Serial = new(@"^\d+(\.\d+)?$", RegexOptions.Compiled);
    private static readonly Regex Dmy = new(@"^(\d{1,4})[/\-.](\d{1,2})[/\-.](\d{1,4})$", RegexOptions.Compiled);

    private readonly IImportRepository _repo;
    private readonly ICurrentUser _user;
    private readonly IAuditLogger _audit;

    public ImportService(IImportRepository repo, ICurrentUser user, IAuditLogger audit)
    {
        _repo = repo;
        _user = user;
        _audit = audit;
    }

    private static ImportTarget TargetOf(string? key) =>
        key is not null && ImportDefinitions.Targets.TryGetValue(key, out var t) ? t : throw ServiceException.BadRequest("Unknown import target.");

    // ------------------------------------------------------------------ small utilities

    public static string NormalizeHeader(string? h) => Regex.Replace((h ?? string.Empty).ToLowerInvariant(), "[^a-z0-9]+", string.Empty);

    public static int ParseBool(string? v) => v is null ? 0 : TrueValues.Contains(v.Trim().ToLowerInvariant()) ? 1 : 0;

    /// <summary>Accepts ISO dates, Excel serials, D/M/Y (or M/D/Y when unambiguous), Y/M/D and other recognizable dates.</summary>
    public static (string? Value, string? Error) NormalizeDate(string? v)
    {
        if (v is null) return (null, null);
        var s = v.Trim();
        if (s.Length == 0) return (null, null);
        if (IsoDate.IsMatch(s)) return RequestValidation.IsValidDateString(s) ? (s, null) : (null, $"\"{s}\" is not a valid date");
        if (Serial.IsMatch(s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial))
        {
            var iso = XlsxReader.ExcelSerialToIso(serial);
            if (RequestValidation.IsValidDateString(iso)) return (iso, null);
        }
        var m = Dmy.Match(s);
        if (m.Success)
        {
            string a = m.Groups[1].Value, b = m.Groups[2].Value, c = m.Groups[3].Value;
            string yyyy, mm, dd;
            if (a.Length == 4)
            {
                (yyyy, mm, dd) = (a, b, c);
            }
            else
            {
                // Ambiguous: assume DD/MM/YYYY, but swap if the first part can't be a day.
                (yyyy, dd, mm) = (c, a, b);
                if (int.Parse(a, CultureInfo.InvariantCulture) > 12 && int.Parse(b, CultureInfo.InvariantCulture) <= 12) { dd = a; mm = b; }
                else if (int.Parse(b, CultureInfo.InvariantCulture) > 12) { mm = a; dd = b; }
            }
            var iso = $"{yyyy.PadLeft(4, '0')}-{mm.PadLeft(2, '0')}-{dd.PadLeft(2, '0')}";
            if (RequestValidation.IsValidDateString(iso)) return (iso, null);
        }
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed))
        {
            return (parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), null);
        }
        return (null, $"\"{s}\" is not a recognizable date");
    }

    // ------------------------------------------------------------------ parsing

    public ParsedFile Parse(string? filename, byte[] content, string? target)
    {
        var ext = (filename ?? string.Empty).Split('.').Last().ToLowerInvariant();
        List<List<string>> matrix;
        string fileType;
        try
        {
            if (ext is "csv" or "txt")
            {
                matrix = CsvParser.Parse(Encoding.UTF8.GetString(content));
                fileType = "csv";
            }
            else if (ext == "xlsx")
            {
                matrix = XlsxReader.Parse(content);
                fileType = "xlsx";
            }
            else
            {
                throw ServiceException.BadRequest("Unsupported file type. Please upload a .xlsx or .csv file.");
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException)
        {
            throw ServiceException.BadRequest(ex.Message);
        }

        if (matrix.Count == 0) return new ParsedFile { FileType = fileType, Filename = filename ?? string.Empty };

        var headers = matrix[0].Select((h, i) => string.IsNullOrWhiteSpace(h) ? $"Column {i + 1}" : h.Trim()).ToList();
        var rows = new List<Dictionary<string, string>>();
        for (var i = 1; i < matrix.Count && rows.Count < ImportDefinitions.MaxRows; i++)
        {
            var arr = matrix[i];
            if (arr.All(c => c.Trim().Length == 0)) continue;
            var obj = new Dictionary<string, string>();
            for (var j = 0; j < headers.Count; j++) obj[headers[j]] = j < arr.Count ? arr[j] : string.Empty;
            rows.Add(obj);
        }
        var suggestion = target is not null && ImportDefinitions.Targets.ContainsKey(target) ? SuggestMapping(target, headers) : null;
        return new ParsedFile { FileType = fileType, Filename = filename ?? string.Empty, Headers = headers, Rows = rows, SuggestedMapping = suggestion };
    }

    public Dictionary<string, string?> SuggestMapping(string target, IReadOnlyList<string> headers)
    {
        var def = TargetOf(target);
        var normHeaders = headers.Select(h => (Header: h, Norm: NormalizeHeader(h))).ToList();
        var used = new HashSet<string>();
        var mapping = new Dictionary<string, string?>();
        foreach (var f in def.Fields)
        {
            var candidates = new List<string> { NormalizeHeader(f.Field), NormalizeHeader(f.Label) };
            if (ImportDefinitions.FieldAliases.TryGetValue(f.Field, out var aliases)) candidates.AddRange(aliases);
            var hit = normHeaders.FirstOrDefault(h => !used.Contains(h.Header) && candidates.Contains(h.Norm));
            if (hit.Header is not null)
            {
                mapping[f.Field] = hit.Header;
                used.Add(hit.Header);
            }
            else
            {
                mapping[f.Field] = null;
            }
        }
        return mapping;
    }

    // ------------------------------------------------------------------ mapping + classification

    public Dictionary<string, string> MapRow(string target, IReadOnlyDictionary<string, string?> mapping, IReadOnlyDictionary<string, string> row)
    {
        var def = TargetOf(target);
        var mapped = new Dictionary<string, string>();
        foreach (var f in def.Fields)
        {
            if (mapping.TryGetValue(f.Field, out var header) && !string.IsNullOrEmpty(header) && row.TryGetValue(header, out var value))
            {
                mapped[f.Field] = value;
            }
        }
        return mapped;
    }

    /// <summary>Mutable name↔id lookup maps loaded once per request and shared across its rows.</summary>
    private sealed class LookupCache
    {
        private readonly IImportRepository _repo;
        private readonly Dictionary<string, (Dictionary<string, LookupRow> ByName, Dictionary<int, string> ById)> _maps = new();

        private LookupCache(IImportRepository repo) => _repo = repo;

        public static async Task<LookupCache> LoadAsync(IImportRepository repo, CancellationToken ct)
        {
            var cache = new LookupCache(repo);
            var tables = ImportDefinitions.Targets.Values.SelectMany(t => t.Fields).Where(f => f.Type == ImportFieldType.Lookup).Select(f => f.Table!).Distinct().ToList();
            var all = await repo.LoadLookupsAsync(tables, ct);
            foreach (var table in tables)
            {
                var byName = new Dictionary<string, LookupRow>();
                var byId = new Dictionary<int, string>();
                foreach (var r in all.GetValueOrDefault(table) ?? new List<LookupRow>())
                {
                    byName[r.Name.Trim().ToLowerInvariant()] = r;
                    byId[r.Id] = r.Name;
                }
                cache._maps[table] = (byName, byId);
            }
            return cache;
        }

        public (int? Id, string Name, bool Missing) Resolve(string table, string? name)
        {
            var n = (name ?? string.Empty).Trim();
            if (n.Length == 0) return (null, string.Empty, false);
            return _maps[table].ByName.TryGetValue(n.ToLowerInvariant(), out var hit) ? (hit.Id, hit.Name, false) : (null, n, true);
        }

        public string NameFor(string table, object? id) =>
            id is int i && _maps[table].ById.TryGetValue(i, out var name) ? name : string.Empty;

        public async Task<int> CreateAsync(string table, string name, CancellationToken ct)
        {
            var clean = name.Trim();
            var id = await _repo.CreateLookupAsync(table, clean, ct);
            _maps[table].ByName[clean.ToLowerInvariant()] = new LookupRow(id, clean);
            _maps[table].ById[id] = clean;
            return id;
        }
    }

    private sealed class BuiltRecord
    {
        public Dictionary<string, object?> Store { get; } = new();
        public Dictionary<string, string> Display { get; } = new();
        public List<string> Errors { get; } = new();
        public List<NewLookupValue> NewLookups { get; } = new();
        public HashSet<string> MappedFields { get; } = new();
    }

    private static BuiltRecord BuildRecord(ImportTarget def, IReadOnlyDictionary<string, string> mapped, LookupCache lookups)
    {
        var rec = new BuiltRecord();
        foreach (var f in def.Fields)
        {
            if (!mapped.TryGetValue(f.Field, out var raw)) continue;
            rec.MappedFields.Add(f.Field);
            switch (f.Type)
            {
                case ImportFieldType.Lookup:
                    var r = lookups.Resolve(f.Table!, raw);
                    rec.Store[f.IdField!] = r.Id;
                    rec.Display[f.Field] = r.Name;
                    if (r.Missing) rec.NewLookups.Add(new NewLookupValue(f.Field, f.Label, f.Table!, f.IdField!, r.Name));
                    break;
                case ImportFieldType.Bool:
                    var b = ParseBool(raw);
                    rec.Store[f.Field] = b;
                    rec.Display[f.Field] = b == 1 ? "Yes" : "No";
                    break;
                case ImportFieldType.Date:
                    var (value, error) = NormalizeDate(raw);
                    if (error is not null) rec.Errors.Add($"{f.Label}: {error}");
                    rec.Store[f.Field] = value;
                    rec.Display[f.Field] = value ?? string.Empty;
                    break;
                default:
                    var s = raw?.Trim();
                    rec.Store[f.Field] = string.IsNullOrEmpty(s) ? null : s;
                    rec.Display[f.Field] = s ?? string.Empty;
                    break;
            }
        }
        return rec;
    }

    private static string? KeyText(object? v) => v is null ? null : Convert.ToString(v, CultureInfo.InvariantCulture)?.Trim();

    private static (string? Field, string? Label, string Value) KeyOf(ImportTarget def, Dictionary<string, object?> store)
    {
        foreach (var k in def.IdKeys)
        {
            var val = KeyText(store.GetValueOrDefault(k.Field));
            if (!string.IsNullOrEmpty(val)) return (k.Field, k.Label, val);
        }
        return (null, null, string.Empty);
    }

    private async Task<(Dictionary<string, object?> Row, string MatchedBy)?> FindExistingAsync(ImportTarget def, Dictionary<string, object?> store, CancellationToken ct)
    {
        foreach (var k in def.IdKeys)
        {
            var val = KeyText(store.GetValueOrDefault(k.Field));
            if (string.IsNullOrEmpty(val)) continue;
            var row = await _repo.FindByKeyAsync(def.Kind, k.Field, val, ct);
            if (row is not null) return (row, k.Label);
        }
        return null;
    }

    private static string RawString(object? v) => v switch
    {
        null => string.Empty,
        bool b => b ? "1" : "0",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? string.Empty,
    };

    private static bool Truthy(object? v) => v switch
    {
        null => false,
        bool b => b,
        int i => i != 0,
        long l => l != 0,
        string s => s.Length > 0 && s != "0",
        _ => true,
    };

    private static Dictionary<string, string> DisplayForExisting(LookupCache lookups, Dictionary<string, object?> row, IEnumerable<ImportField> fields)
    {
        var output = new Dictionary<string, string>();
        foreach (var f in fields)
        {
            output[f.Field] = f.Type switch
            {
                ImportFieldType.Lookup => lookups.NameFor(f.Table!, row.GetValueOrDefault(f.IdField!)),
                ImportFieldType.Bool => Truthy(row.GetValueOrDefault(f.Field)) ? "Yes" : "No",
                _ => RawString(row.GetValueOrDefault(f.Field)),
            };
        }
        return output;
    }

    private static List<FieldDiff> ComputeDiffs(ImportTarget def, BuiltRecord rec, Dictionary<string, object?> existing, LookupCache lookups)
    {
        var diffs = new List<FieldDiff>();
        foreach (var f in def.Fields)
        {
            if (!rec.MappedFields.Contains(f.Field)) continue;
            bool changed;
            string currentDisp, uploadedDisp;
            if (f.Type == ImportFieldType.Lookup)
            {
                currentDisp = lookups.NameFor(f.Table!, existing.GetValueOrDefault(f.IdField!));
                uploadedDisp = rec.Display.GetValueOrDefault(f.Field) ?? string.Empty;
                changed = currentDisp.Trim().ToLowerInvariant() != uploadedDisp.Trim().ToLowerInvariant();
            }
            else if (f.Type == ImportFieldType.Bool)
            {
                var cur = Truthy(existing.GetValueOrDefault(f.Field)) ? 1 : 0;
                var up = Truthy(rec.Store.GetValueOrDefault(f.Field)) ? 1 : 0;
                changed = cur != up;
                currentDisp = cur == 1 ? "Yes" : "No";
                uploadedDisp = up == 1 ? "Yes" : "No";
            }
            else
            {
                var cur = RawString(existing.GetValueOrDefault(f.Field)).Trim();
                var up = RawString(rec.Store.GetValueOrDefault(f.Field)).Trim();
                changed = cur != up;
                currentDisp = cur;
                uploadedDisp = up;
            }
            if (changed) diffs.Add(new FieldDiff(f.Field, f.Label, currentDisp, uploadedDisp));
        }
        return diffs;
    }

    private async Task<ClassifiedRecord> ClassifyAsync(ImportTarget def, int rowIndex, IReadOnlyDictionary<string, string> mapped, LookupCache lookups, CancellationToken ct)
    {
        var rec = BuildRecord(def, mapped, lookups);
        var key = KeyOf(def, rec.Store);
        var mappedDefs = def.Fields.Where(f => rec.MappedFields.Contains(f.Field)).ToList();
        var uploaded = mappedDefs.ToDictionary(f => f.Field, f => rec.Display.GetValueOrDefault(f.Field) ?? string.Empty);
        var title = RawString(rec.Store.GetValueOrDefault("title"));

        ClassifiedRecord Make(string outcome, string action, int? entityId = null, string? matchedBy = null, List<FieldDiff>? diffs = null,
            List<string>? errors = null, Dictionary<string, string>? current = null) => new()
        {
            RowIndex = rowIndex,
            Key = key.Value,
            KeyField = key.Field,
            Title = title,
            Uploaded = uploaded,
            NewLookups = rec.NewLookups,
            MappedFields = rec.MappedFields.ToList(),
            Outcome = outcome,
            SuggestedAction = action,
            EntityId = entityId,
            MatchedBy = matchedBy,
            Diffs = diffs ?? new List<FieldDiff>(),
            Errors = errors ?? new List<string>(),
            Current = current ?? new Dictionary<string, string>(),
        };

        if (rec.Errors.Count > 0) return Make("invalid", "skip", errors: rec.Errors);

        var existing = await FindExistingAsync(def, rec.Store, ct);
        if (existing is null)
        {
            if (string.IsNullOrEmpty(title)) return Make("invalid", "skip", errors: new List<string> { "Title is required to insert a new record." });
            return Make("new", "insert");
        }

        var (row, matchedBy) = existing.Value;
        var entityId = Convert.ToInt32(row["id"], CultureInfo.InvariantCulture);
        var diffs = ComputeDiffs(def, rec, row, lookups);
        var currentDisplay = DisplayForExisting(lookups, row, mappedDefs);
        return diffs.Count == 0
            ? Make("duplicate", "skip", entityId, matchedBy, current: currentDisplay)
            : Make("update", "update", entityId, matchedBy, diffs, current: currentDisplay);
    }

    public async Task<AnalysisResult> AnalyzeAsync(string target, IReadOnlyDictionary<string, string?> mapping, IReadOnlyList<Dictionary<string, string>> rows, CancellationToken ct = default)
    {
        var def = TargetOf(target);
        if (rows.Count > ImportDefinitions.MaxRows) throw ServiceException.BadRequest($"Too many rows (max {ImportDefinitions.MaxRows}).");
        var lookups = await LookupCache.LoadAsync(_repo, ct);
        var records = new List<ClassifiedRecord>();
        for (var i = 0; i < rows.Count; i++) records.Add(await ClassifyAsync(def, i, MapRow(target, mapping, rows[i]), lookups, ct));
        var summary = new AnalysisSummary(records.Count,
            records.Count(r => r.Outcome == "new"), records.Count(r => r.Outcome == "update"),
            records.Count(r => r.Outcome == "duplicate"), records.Count(r => r.Outcome == "invalid"));
        return new AnalysisResult(target, records, summary);
    }

    // ------------------------------------------------------------------ commit

    private static async Task ApplyLookupCreationAsync(ImportTarget def, BuiltRecord rec, LookupCache lookups, CancellationToken ct)
    {
        foreach (var f in def.Fields)
        {
            if (f.Type != ImportFieldType.Lookup || !rec.MappedFields.Contains(f.Field)) continue;
            var name = rec.Display.GetValueOrDefault(f.Field);
            if (!string.IsNullOrEmpty(name) && rec.Store.GetValueOrDefault(f.IdField!) is null)
            {
                rec.Store[f.IdField!] = await lookups.CreateAsync(f.Table!, name, ct);
            }
        }
    }

    /// <summary>Existing row values (or defaults for a new row), overlaid with only the columns mapped from the file.</summary>
    private static Dictionary<string, object?> BuildEntityBody(ImportTarget def, BuiltRecord rec, Dictionary<string, object?>? existing)
    {
        var body = new Dictionary<string, object?>();
        foreach (var c in def.EntityCols) body[c.Col] = existing is not null ? existing.GetValueOrDefault(c.Col) : c.IsBool ? 0 : null;
        foreach (var kv in rec.Store) body[kv.Key] = kv.Value;
        return body;
    }

    private static List<string> ValidateBody(ImportTarget def, Dictionary<string, object?> body)
    {
        var strings = body.ToDictionary(kv => kv.Key, kv => kv.Value is null ? null : RawString(kv.Value));
        return def.Kind == TrackerKind.Incident ? RequestValidation.ValidateIncident(strings) : RequestValidation.ValidateBusinessRequest(strings);
    }

    private async Task<string?> KeyConflictAsync(ImportTarget def, Dictionary<string, object?> store, int? excludeId, CancellationToken ct)
    {
        foreach (var k in def.IdKeys)
        {
            var val = KeyText(store.GetValueOrDefault(k.Field));
            if (string.IsNullOrEmpty(val)) continue;
            if (await _repo.KeyInUseAsync(def.Kind, k.Field, val, excludeId, ct)) return $"{k.Label} \"{val}\" is already used by another record.";
        }
        return null;
    }

    public async Task<CommitResult> CommitAsync(string target, string? filename, string? fileType, IReadOnlyDictionary<string, string?> mapping,
        IReadOnlyList<Dictionary<string, string>> rows, IReadOnlyDictionary<int, string> decisions, CancellationToken ct = default)
    {
        var def = TargetOf(target);
        if (rows.Count > ImportDefinitions.MaxRows) throw ServiceException.BadRequest($"Too many rows (max {ImportDefinitions.MaxRows}).");
        var changedBy = _user.DisplayName;
        var lookups = await LookupCache.LoadAsync(_repo, ct);

        var batchId = await _repo.CreateBatchAsync(new ImportBatch
        {
            Filename = string.IsNullOrEmpty(filename) ? null : filename,
            FileType = string.IsNullOrEmpty(fileType) ? null : fileType,
            Target = target,
            CreatedBy = changedBy,
            TotalRows = rows.Count,
        }, ct);

        int added = 0, updated = 0, duplicate = 0, skipped = 0, invalid = 0, failed = 0;
        var errors = new List<CommitError>();
        for (var i = 0; i < rows.Count; i++)
        {
            var mapped = MapRow(target, mapping, rows[i]);
            var cls = await ClassifyAsync(def, i, mapped, lookups, ct);
            var rec = BuildRecord(def, mapped, lookups);
            var action = decisions.TryGetValue(i, out var decided) && !string.IsNullOrEmpty(decided) ? decided : cls.SuggestedAction;
            string outcome = "skipped";
            string? message = null;
            var entityId = cls.EntityId;

            try
            {
                if (cls.Outcome == "invalid")
                {
                    action = "skip";
                    outcome = "invalid";
                    message = string.Join(" ", cls.Errors);
                    invalid++;
                }
                else if (action == "skip")
                {
                    outcome = cls.Outcome == "duplicate" ? "duplicate" : "skipped";
                    if (outcome == "duplicate") duplicate++; else skipped++;
                }
                else if (action == "insert")
                {
                    await ApplyLookupCreationAsync(def, rec, lookups, ct);
                    var conflict = await KeyConflictAsync(def, rec.Store, null, ct);
                    var body = BuildEntityBody(def, rec, null);
                    var validationErrors = ValidateBody(def, body);
                    if (conflict is not null) throw new InvalidOperationException(conflict);
                    if (validationErrors.Count > 0) throw new InvalidOperationException(string.Join(" ", validationErrors));
                    entityId = await _repo.InsertEntityAsync(def.Kind, body, changedBy, ct);
                    outcome = "new";
                    added++;
                }
                else if (action == "update")
                {
                    var existing = await FindExistingAsync(def, rec.Store, ct) ?? throw new InvalidOperationException("No matching record found to update.");
                    var existingId = Convert.ToInt32(existing.Row["id"], CultureInfo.InvariantCulture);
                    entityId = existingId;
                    await ApplyLookupCreationAsync(def, rec, lookups, ct);
                    var merged = BuildEntityBody(def, rec, existing.Row);
                    var conflict = await KeyConflictAsync(def, merged, existingId, ct);
                    var validationErrors = ValidateBody(def, merged);
                    if (conflict is not null) throw new InvalidOperationException(conflict);
                    if (validationErrors.Count > 0) throw new InvalidOperationException(string.Join(" ", validationErrors));
                    await _repo.UpdateEntityAsync(def.Kind, existingId, merged, changedBy, ct);
                    outcome = "updated";
                    updated++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                outcome = "error";
                message = ex.Message;
                failed++;
                errors.Add(new CommitError(i, cls.Key, ex.Message));
            }

            await _repo.AddBatchRowAsync(new ImportBatchRow
            {
                BatchId = batchId,
                RowIndex = i,
                Action = action,
                Outcome = outcome,
                EntityId = entityId,
                KeyValue = string.IsNullOrEmpty(cls.Key) ? null : cls.Key,
                Title = string.IsNullOrEmpty(cls.Title) ? null : cls.Title,
                DiffJson = cls.Diffs.Count > 0
                    ? TrackerSnapshot.Serialize(cls.Diffs.Select(d => new Dictionary<string, string> { ["field"] = d.Field, ["label"] = d.Label, ["current"] = d.Current, ["uploaded"] = d.Uploaded }).ToList())
                    : null,
                Message = message,
                RawJson = TrackerSnapshot.Serialize(mapped),
            }, ct);
        }

        var summary = new CommitSummary(rows.Count, added, updated, duplicate, skipped, invalid, failed);
        var summaryJson = TrackerSnapshot.Serialize(new Dictionary<string, int>
        {
            ["total"] = summary.Total, ["new"] = summary.New, ["updated"] = summary.Updated, ["duplicate"] = summary.Duplicate,
            ["skipped"] = summary.Skipped, ["invalid"] = summary.Invalid, ["error"] = summary.Error,
        });
        await _repo.CompleteBatchAsync(batchId, summary, summaryJson, ct);
        _audit.Log("import.commit", "import_batches", batchId, $"{target} {filename}: {summaryJson}");
        return new CommitResult(batchId, summary, errors);
    }

    public Task<List<ImportBatch>> HistoryAsync(CancellationToken ct = default) => _repo.ListBatchesAsync(200, ct);

    public async Task<(ImportBatch Batch, List<ImportBatchRow> Rows)> HistoryDetailAsync(int id, CancellationToken ct = default)
    {
        var batch = (id > 0 ? await _repo.GetBatchAsync(id, ct) : null) ?? throw ServiceException.NotFound("Import batch not found.");
        return (batch, await _repo.ListBatchRowsAsync(id, ct));
    }
}
