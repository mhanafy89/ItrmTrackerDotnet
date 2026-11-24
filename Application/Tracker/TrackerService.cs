using ItrmTracker.Application.Abstractions;
using ItrmTracker.Application.Common;
using ItrmTracker.Application.Sla;
using ItrmTracker.Application.Validation;
using ItrmTracker.Domain.Constants;
using Microsoft.Extensions.Logging;

namespace ItrmTracker.Application.Tracker;

/// <summary>Business requests and incidents: register, CRUD, bulk edit, history, comments and follows.</summary>
public interface ITrackerService
{
    /// <summary>Every record with SLA and aging fields (unfiltered, newest update first).</summary>
    Task<List<TrackerRecord>> ListAllAsync(TrackerKind kind, CancellationToken ct = default);

    /// <summary>Filtered and sorted register rows.</summary>
    Task<List<TrackerRecord>> ListAsync(TrackerKind kind, TrackerQuery query, CancellationToken ct = default);
    Task<TrackerRecord> GetAsync(TrackerKind kind, int id, CancellationToken ct = default);
    Task<TrackerRecord> CreateAsync(TrackerKind kind, TrackerInput input, CancellationToken ct = default);
    Task<TrackerRecord> UpdateAsync(TrackerKind kind, int id, TrackerInput input, CancellationToken ct = default);
    Task DeleteAsync(TrackerKind kind, int id, CancellationToken ct = default);
    Task<BulkUpdateResult> BulkUpdateAsync(TrackerKind kind, IEnumerable<int> ids, IReadOnlyDictionary<string, string?> fields, CancellationToken ct = default);
    Task<List<HistoryEntryView>> GetHistoryAsync(TrackerKind kind, int id, Func<string, string> formatDateTime, CancellationToken ct = default);
    Task<List<CommentDto>> ListCommentsAsync(TrackerKind kind, int id, CancellationToken ct = default);
    Task<CommentDto> AddCommentAsync(TrackerKind kind, int id, string? body, CancellationToken ct = default);
    Task DeleteCommentAsync(TrackerKind kind, int id, int commentId, CancellationToken ct = default);

    /// <summary>Rows for the Excel export (with the concatenated comment thread), filtered like the register.</summary>
    Task<List<TrackerRecord>> ExportAsync(TrackerKind kind, TrackerQuery query, CancellationToken ct = default);
    Task<HashSet<int>> FollowedIdsAsync(TrackerKind kind, CancellationToken ct = default);
    Task FollowAsync(TrackerKind kind, int id, CancellationToken ct = default);
    Task UnfollowAsync(TrackerKind kind, int id, CancellationToken ct = default);

    /// <summary>Lookup columns the bulk editor may change for this tracker.</summary>
    IReadOnlyList<string> BulkFields(TrackerKind kind);
}

/// <inheritdoc />
public sealed class TrackerService : ITrackerService
{
    private static readonly string[] BrLookupFields =
        { "business_unit_id", "business_category_id", "requester_id", "priority_id", "stage_id", "status_id", "tech_owner_id", "regulator_id", "rm_id" };
    private static readonly string[] IncidentLookupFields =
        { "business_unit_id", "requester_id", "priority_id", "stage_id", "status_id", "tech_owner_id", "rm_id" };

    private readonly ITrackerRepository _repo;
    private readonly ISlaConfigService _slaConfig;
    private readonly ISlaService _sla;
    private readonly IClock _clock;
    private readonly ICurrentUser _user;
    private readonly IAuditLogger _audit;
    private readonly ILogger<TrackerService> _logger;

    public TrackerService(ITrackerRepository repo, ISlaConfigService slaConfig, ISlaService sla, IClock clock, ICurrentUser user, IAuditLogger audit, ILogger<TrackerService> logger)
    {
        _repo = repo;
        _slaConfig = slaConfig;
        _sla = sla;
        _clock = clock;
        _user = user;
        _audit = audit;
        _logger = logger;
    }

    private static string EntityName(TrackerKind kind) => kind == TrackerKind.Incident ? "incident" : "business_request";

    public IReadOnlyList<string> BulkFields(TrackerKind kind) => kind == TrackerKind.Incident ? IncidentLookupFields : BrLookupFields;

    // ------------------------------------------------------------------ reads

    public async Task<List<TrackerRecord>> ListAllAsync(TrackerKind kind, CancellationToken ct = default)
    {
        var rows = await _repo.ListAsync(kind, ct);
        await EnrichAsync(rows, kind, ct);
        return rows;
    }

    public async Task<List<TrackerRecord>> ListAsync(TrackerKind kind, TrackerQuery query, CancellationToken ct = default)
    {
        var rows = TrackerFilter.Apply(await ListAllAsync(kind, ct), query);
        return TrackerFilter.Sort(rows, query.Sort, query.Dir, TrackerColumns.Table(kind));
    }

    public async Task<TrackerRecord> GetAsync(TrackerKind kind, int id, CancellationToken ct = default)
    {
        var row = id > 0 ? await _repo.GetAsync(kind, id, ct) : null;
        if (row is null) throw ServiceException.NotFound();
        await EnrichAsync(new List<TrackerRecord> { row }, kind, ct);
        return row;
    }

    public async Task<List<TrackerRecord>> ExportAsync(TrackerKind kind, TrackerQuery query, CancellationToken ct = default)
    {
        var rows = await ListAllAsync(kind, ct);
        var comments = await _repo.ListAllCommentsAsync(kind, ct);
        foreach (var r in rows)
        {
            r.AllComments = comments.TryGetValue(r.Id, out var list)
                ? string.Join("\n", list.Select(c => $"[{LegacyFormat.DateTime(c.CreatedAt)}] {c.Body}"))
                : string.Empty;
        }
        // Filters apply, sorting does not (same as the original export).
        return TrackerFilter.Apply(rows, query);
    }

    /// <summary>Adds the legacy SLA fields and the aging fields (Days Since Update / stale) to each row.</summary>
    private async Task EnrichAsync(List<TrackerRecord> rows, TrackerKind kind, CancellationToken ct)
    {
        var calculator = new LegacySlaCalculator(await _slaConfig.GetAsync(ct));
        var today = _clock.Today;
        var now = _clock.UtcNow;
        foreach (var r in rows)
        {
            r.Sla = calculator.Compute(r, kind, today);
            r.AgeDays = (int)Math.Floor((now - DateTime.SpecifyKind(r.UpdatedAt, DateTimeKind.Utc)).TotalDays);
            r.IsStale = !TrackerRules.IsDoneStatus(r.Status) && r.AgeDays >= TrackerRules.StaleThresholdDays;
        }
    }

    // ------------------------------------------------------------------ writes

    private static List<string> Validate(TrackerKind kind, TrackerInput input) =>
        kind == TrackerKind.Incident
            ? RequestValidation.ValidateIncident(input.ToValidationBody())
            : RequestValidation.ValidateBusinessRequest(input.ToValidationBody());

    private async Task<List<string>> CheckUniquenessAsync(TrackerKind kind, TrackerData data, int? excludeId, CancellationToken ct)
    {
        var errors = new List<string>();
        if (!string.IsNullOrEmpty(data.RefNo) && await _repo.IsValueInUseAsync(kind, "ref", data.RefNo, excludeId, ct))
        {
            errors.Add(kind == TrackerKind.Incident
                ? $"Incident ID \"{data.RefNo}\" is already in use."
                : $"BR No \"{data.RefNo}\" is already in use by another request.");
        }
        if (!string.IsNullOrEmpty(data.Cr) && await _repo.IsValueInUseAsync(kind, "cr", data.Cr, excludeId, ct))
        {
            errors.Add(kind == TrackerKind.Incident
                ? $"CR \"{data.Cr}\" is already in use by another incident."
                : $"CR \"{data.Cr}\" is already in use by another request.");
        }
        return errors;
    }

    private static string UniqueRaceMessage(TrackerKind kind) => kind == TrackerKind.Incident
        ? "Incident ID or CR is already in use by another incident."
        : "BR No or CR is already in use by another request.";

    public async Task<TrackerRecord> CreateAsync(TrackerKind kind, TrackerInput input, CancellationToken ct = default)
    {
        var errors = Validate(kind, input);
        if (errors.Count > 0) throw new ServiceException(400, errors);
        var data = TrackerData.From(input, kind);
        var uniqueErrors = await CheckUniquenessAsync(kind, data, null, ct);
        if (uniqueErrors.Count > 0) throw new ServiceException(400, uniqueErrors);

        var changedBy = _user.DisplayName;
        TrackerRecord created;
        try
        {
            created = await _repo.InTransactionAsync(async () =>
            {
                var id = await _repo.CreateAsync(kind, data, changedBy, ct);
                var row = (await _repo.GetAsync(kind, id, ct))!;
                await _repo.AddHistoryAsync(kind, id, "create", TrackerSnapshot.FromListRow(row), changedBy, ct);
                return row;
            }, ct);
        }
        catch (DuplicateKeyException)
        {
            throw ServiceException.BadRequest(UniqueRaceMessage(kind));
        }

        if (kind == TrackerKind.BusinessRequest) await _sla.SyncLegacyRequestAsync(created.Id, changedBy, ct);
        _audit.Log("create", EntityName(kind), created.Id, created.RefNo);
        return created;
    }

    public async Task<TrackerRecord> UpdateAsync(TrackerKind kind, int id, TrackerInput input, CancellationToken ct = default)
    {
        if (id <= 0 || !await _repo.ExistsAsync(kind, id, ct)) throw ServiceException.NotFound();
        var errors = Validate(kind, input);
        if (errors.Count > 0) throw new ServiceException(400, errors);
        var data = TrackerData.From(input, kind);
        var uniqueErrors = await CheckUniquenessAsync(kind, data, id, ct);
        if (uniqueErrors.Count > 0) throw new ServiceException(400, uniqueErrors);

        var changedBy = _user.DisplayName;
        TrackerRecord updated;
        try
        {
            updated = await _repo.InTransactionAsync(async () =>
            {
                await _repo.UpdateAsync(kind, id, data, changedBy, ct);
                var row = (await _repo.GetAsync(kind, id, ct))!;
                await _repo.AddHistoryAsync(kind, id, "update", TrackerSnapshot.FromListRow(row), changedBy, ct);
                return row;
            }, ct);
        }
        catch (DuplicateKeyException)
        {
            throw ServiceException.BadRequest(UniqueRaceMessage(kind));
        }

        if (kind == TrackerKind.BusinessRequest) await _sla.SyncLegacyRequestAsync(updated.Id, changedBy, ct);
        _audit.Log("update", EntityName(kind), id, updated.RefNo);
        return updated;
    }

    /// <remarks>
    /// The original app wrote a "delete" history row and then deleted the record; the history table cascades, so that
    /// row disappears with the record. That behaviour is preserved; the structured audit log keeps a durable trace.
    /// </remarks>
    public async Task DeleteAsync(TrackerKind kind, int id, CancellationToken ct = default)
    {
        var existing = id > 0 ? await _repo.GetRawRowAsync(kind, id, ct) : null;
        if (existing is null) throw ServiceException.NotFound();
        var changedBy = _user.DisplayName;
        await _repo.InTransactionAsync(async () =>
        {
            await _repo.AddHistoryAsync(kind, id, "delete", TrackerSnapshot.FromRawRow(existing), changedBy, ct);
            await _repo.DeleteAsync(kind, id, ct);
            return true;
        }, ct);
        _audit.Log("delete", EntityName(kind), id, TrackerSnapshot.FromRawRow(existing));
    }

    public async Task<BulkUpdateResult> BulkUpdateAsync(TrackerKind kind, IEnumerable<int> ids, IReadOnlyDictionary<string, string?> fields, CancellationToken ct = default)
    {
        var idList = ids.Where(n => n > 0).Distinct().ToList();
        if (idList.Count == 0) throw ServiceException.BadRequest("Select at least one record to update.");

        var allowed = BulkFields(kind);
        var fieldNames = fields.Keys.Where(allowed.Contains).ToList();
        if (fieldNames.Count == 0) throw ServiceException.BadRequest("No valid lookup fields to update.");

        var data = new Dictionary<string, int?>();
        foreach (var f in fieldNames)
        {
            var v = fields[f];
            if (string.IsNullOrEmpty(v))
            {
                data[f] = null;
                continue;
            }
            var n = JsMath.ToNumber(v);
            if (!JsMath.IsInteger(n) || n > int.MaxValue || n < int.MinValue) throw ServiceException.BadRequest($"Invalid value for {f}.");
            data[f] = (int)n;
        }

        var changedBy = _user.DisplayName;
        var updated = 0;
        var errors = new List<string>();
        foreach (var id in idList)
        {
            try
            {
                var found = await _repo.InTransactionAsync(async () =>
                {
                    if (!await _repo.BulkUpdateAsync(kind, id, data, changedBy, ct)) return false;
                    var row = (await _repo.GetAsync(kind, id, ct))!;
                    await _repo.AddHistoryAsync(kind, id, "bulk-update", TrackerSnapshot.FromListRow(row), changedBy, ct);
                    return true;
                }, ct);
                if (found)
                {
                    updated++;
                    if (kind == TrackerKind.BusinessRequest && (fieldNames.Contains("stage_id") || fieldNames.Contains("status_id")))
                    {
                        await _sla.SyncLegacyRequestAsync(id, changedBy, ct);
                    }
                }
                else
                {
                    errors.Add($"Record {id} not found.");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Bulk update failed for {Entity} {Id}", EntityName(kind), id);
                errors.Add($"Record {id}: {ex.Message}");
            }
        }
        _audit.Log("bulk-update", EntityName(kind), string.Join(",", idList), $"fields={string.Join(",", fieldNames)} updated={updated}");
        return new BulkUpdateResult(updated, idList.Count, errors);
    }

    // ------------------------------------------------------------------ history & comments

    public async Task<List<HistoryEntryView>> GetHistoryAsync(TrackerKind kind, int id, Func<string, string> formatDateTime, CancellationToken ct = default)
    {
        var rows = id > 0 ? await _repo.ListHistoryAsync(kind, id, ct) : new List<HistoryRow>();
        return HistoryDiffer.Build(rows, TrackerColumns.Table(kind), formatDateTime);
    }

    public Task<List<CommentDto>> ListCommentsAsync(TrackerKind kind, int id, CancellationToken ct = default) =>
        id > 0 ? _repo.ListCommentsAsync(kind, id, ct) : Task.FromResult(new List<CommentDto>());

    public async Task<CommentDto> AddCommentAsync(TrackerKind kind, int id, string? body, CancellationToken ct = default)
    {
        if (id <= 0 || !await _repo.ExistsAsync(kind, id, ct))
        {
            throw ServiceException.NotFound(kind == TrackerKind.Incident ? "Incident not found." : "Request not found.");
        }
        var text = (body ?? string.Empty).Trim();
        if (text.Length == 0) throw ServiceException.BadRequest("Comment body is required.");
        var comment = await _repo.AddCommentAsync(kind, id, text, _user.DisplayName, ct);
        _audit.Log("comment.add", EntityName(kind), id);
        return comment;
    }

    public async Task DeleteCommentAsync(TrackerKind kind, int id, int commentId, CancellationToken ct = default)
    {
        var deleted = id > 0 && commentId > 0 && await _repo.DeleteCommentAsync(kind, id, commentId, ct);
        if (!deleted) throw ServiceException.NotFound("Comment not found.");
        _audit.Log("comment.delete", EntityName(kind), id, $"comment {commentId}");
    }

    // ------------------------------------------------------------------ personal follow-ups

    public async Task<HashSet<int>> FollowedIdsAsync(TrackerKind kind, CancellationToken ct = default) =>
        _user.UserId is { } uid ? new HashSet<int>(await _repo.ListFollowedIdsAsync(kind, uid, ct)) : new HashSet<int>();

    public async Task FollowAsync(TrackerKind kind, int id, CancellationToken ct = default)
    {
        if (id <= 0) throw ServiceException.BadRequest("Invalid id.");
        if (!await _repo.ExistsAsync(kind, id, ct)) throw ServiceException.NotFound();
        await _repo.FollowAsync(kind, id, _user.UserId ?? throw new UnauthorizedAccessException(), ct);
    }

    public async Task UnfollowAsync(TrackerKind kind, int id, CancellationToken ct = default)
    {
        if (id <= 0) throw ServiceException.BadRequest("Invalid id.");
        await _repo.UnfollowAsync(kind, id, _user.UserId ?? throw new UnauthorizedAccessException(), ct);
    }
}
