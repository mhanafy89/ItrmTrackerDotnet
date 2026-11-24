using ItrmTracker.Application.Abstractions;
using ItrmTracker.Application.Sla;
using ItrmTracker.Application.Tracker;
using ItrmTracker.Domain.Entities;
using ItrmTracker.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ItrmTracker.Infrastructure.Repositories;

/// <summary>EF Core implementation of <see cref="ITrackerRepository"/> for both trackers.</summary>
public sealed class TrackerRepository : ITrackerRepository
{
    private readonly ItrmDbContext _db;
    private readonly IClock _clock;

    public TrackerRepository(ItrmDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    public Task<T> InTransactionAsync<T>(Func<Task<T>> work, CancellationToken ct = default) => DbHelpers.InTransactionAsync(_db, work, ct);

    // ------------------------------------------------------------------ list projection (LIST_SELECT)

    private static IQueryable<TrackerRecord> ProjectBr(IQueryable<BusinessRequest> q) => q.AsNoTracking().Select(b => new TrackerRecord
    {
        Kind = TrackerKind.BusinessRequest,
        Id = b.Id,
        RefNo = b.BrNo,
        Cr = b.Cr,
        SubmissionDate = b.SubmissionDate,
        BusinessUnitId = b.BusinessUnitId,
        BusinessCategoryId = b.BusinessCategoryId,
        RequesterId = b.RequesterId,
        Title = b.Title,
        Description = b.Description,
        PriorityId = b.PriorityId,
        StageId = b.StageId,
        StatusId = b.StatusId,
        NewgenRelated = b.NewgenRelated,
        Estimated = b.Estimated,
        BaRequired = b.BaRequired,
        BrdAssigned = b.BrdAssigned,
        TechOwnerId = b.TechOwnerId,
        Regulatory = b.Regulatory,
        RegulatorId = b.RegulatorId,
        RegulatoryDeadline = b.RegulatoryDeadline,
        AnalysisPlannedStartDate = b.AnalysisPlannedStartDate,
        AnalysisPlannedEndDate = b.AnalysisPlannedEndDate,
        DevPlannedStartDate = b.DevPlannedStartDate,
        DevPlannedEndDate = b.DevPlannedEndDate,
        QaPlannedStartDate = b.QaPlannedStartDate,
        QaPlannedEndDate = b.QaPlannedEndDate,
        UatPlannedStartDate = b.UatPlannedStartDate,
        UatPlannedDate = b.UatPlannedDate,
        CabDate = b.CabDate,
        DeploymentPlannedEndDate = b.DeploymentPlannedEndDate,
        ClosedDate = b.ClosedDate,
        LegacyComments = b.Comments,
        RmId = b.RmId,
        CreatedAt = b.CreatedAt,
        UpdatedAt = b.UpdatedAt,
        CreatedBy = b.CreatedBy,
        UpdatedBy = b.UpdatedBy,
        BusinessUnit = b.BusinessUnit != null ? b.BusinessUnit.Name : null,
        BusinessCategory = b.BusinessCategory != null ? b.BusinessCategory.Name : null,
        Priority = b.Priority != null ? b.Priority.Name : null,
        Stage = b.Stage != null ? b.Stage.Name : null,
        Status = b.Status != null ? b.Status.Name : null,
        Regulator = b.Regulator != null ? b.Regulator.Name : null,
        // COALESCE(re.name, br.requester): the legacy free-text requester is the fallback.
        Requester = b.Requester != null ? b.Requester.Name : b.RequesterText,
        Rm = b.Rm != null ? b.Rm.Name : null,
        LastComment = b.CommentEntries.OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id).Select(c => c.Body).FirstOrDefault(),
        LastCommentAt = b.CommentEntries.OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id).Select(c => (DateTime?)c.CreatedAt).FirstOrDefault(),
    });

    private static IQueryable<TrackerRecord> ProjectIncident(IQueryable<Incident> q) => q.AsNoTracking().Select(i => new TrackerRecord
    {
        Kind = TrackerKind.Incident,
        Id = i.Id,
        RefNo = i.IncidentRef,
        Cr = i.Cr,
        BusinessUnitId = i.BusinessUnitId,
        RequesterId = i.RequesterId,
        Title = i.Title,
        Description = i.Description,
        PriorityId = i.PriorityId,
        StageId = i.StageId,
        StatusId = i.StatusId,
        NewgenRelated = i.NewgenRelated,
        Estimated = i.Estimated,
        TechOwnerId = i.TechOwnerId,
        DevPlannedStartDate = i.DevPlannedStartDate,
        DevPlannedEndDate = i.DevPlannedEndDate,
        QaPlannedStartDate = i.QaPlannedStartDate,
        QaPlannedEndDate = i.QaPlannedEndDate,
        UatPlannedStartDate = i.UatPlannedStartDate,
        UatPlannedDate = i.UatPlannedDate,
        CabDate = i.CabDate,
        DeploymentPlannedEndDate = i.DeploymentPlannedEndDate,
        ClosedDate = i.ClosedDate,
        RmId = i.RmId,
        CreatedAt = i.CreatedAt,
        UpdatedAt = i.UpdatedAt,
        CreatedBy = i.CreatedBy,
        UpdatedBy = i.UpdatedBy,
        BusinessUnit = i.BusinessUnit != null ? i.BusinessUnit.Name : null,
        Priority = i.Priority != null ? i.Priority.Name : null,
        Stage = i.Stage != null ? i.Stage.Name : null,
        Status = i.Status != null ? i.Status.Name : null,
        Requester = i.Requester != null ? i.Requester.Name : null,
        Rm = i.Rm != null ? i.Rm.Name : null,
        LastComment = i.CommentEntries.OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id).Select(c => c.Body).FirstOrDefault(),
        LastCommentAt = i.CommentEntries.OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id).Select(c => (DateTime?)c.CreatedAt).FirstOrDefault(),
    });

    private sealed record Link(int OwnerId, int Id, string Name);

    /// <summary>Adds tech owners and systems (sorted by name, like STRING_AGG … WITHIN GROUP (ORDER BY name)).</summary>
    private async Task AttachLinksAsync(TrackerKind kind, List<TrackerRecord> rows, int? onlyId, CancellationToken ct)
    {
        if (rows.Count == 0) return;
        List<Link> owners, systems;
        if (kind == TrackerKind.BusinessRequest)
        {
            var o = _db.BusinessRequestTechOwners.AsNoTracking();
            var s = _db.BusinessRequestSystems.AsNoTracking();
            if (onlyId is { } id) { o = o.Where(x => x.BusinessRequestId == id); s = s.Where(x => x.BusinessRequestId == id); }
            owners = await o.OrderBy(x => x.TechOwner!.Name).Select(x => new Link(x.BusinessRequestId, x.TechOwnerId, x.TechOwner!.Name)).ToListAsync(ct);
            systems = await s.OrderBy(x => x.System!.Name).Select(x => new Link(x.BusinessRequestId, x.SystemId, x.System!.Name)).ToListAsync(ct);
        }
        else
        {
            var o = _db.IncidentTechOwners.AsNoTracking();
            var s = _db.IncidentSystems.AsNoTracking();
            if (onlyId is { } id) { o = o.Where(x => x.IncidentId == id); s = s.Where(x => x.IncidentId == id); }
            owners = await o.OrderBy(x => x.TechOwner!.Name).Select(x => new Link(x.IncidentId, x.TechOwnerId, x.TechOwner!.Name)).ToListAsync(ct);
            systems = await s.OrderBy(x => x.System!.Name).Select(x => new Link(x.IncidentId, x.SystemId, x.System!.Name)).ToListAsync(ct);
        }
        var ownersBy = owners.GroupBy(x => x.OwnerId).ToDictionary(g => g.Key, g => g.ToList());
        var systemsBy = systems.GroupBy(x => x.OwnerId).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var r in rows)
        {
            if (ownersBy.TryGetValue(r.Id, out var ow))
            {
                r.TechOwner = string.Join(", ", ow.Select(x => x.Name));
                r.TechOwnerIds = ow.Select(x => x.Id).OrderBy(x => x).ToList();
            }
            if (systemsBy.TryGetValue(r.Id, out var sy))
            {
                r.System = string.Join(", ", sy.Select(x => x.Name));
                r.SystemIds = sy.Select(x => x.Id).OrderBy(x => x).ToList();
            }
        }
    }

    public async Task<List<TrackerRecord>> ListAsync(TrackerKind kind, CancellationToken ct = default)
    {
        var rows = kind == TrackerKind.BusinessRequest
            ? await ProjectBr(_db.BusinessRequests.OrderByDescending(b => b.UpdatedAt).ThenByDescending(b => b.Id)).ToListAsync(ct)
            : await ProjectIncident(_db.Incidents.OrderByDescending(b => b.UpdatedAt).ThenByDescending(b => b.Id)).ToListAsync(ct);
        await AttachLinksAsync(kind, rows, null, ct);
        return rows;
    }

    public async Task<TrackerRecord?> GetAsync(TrackerKind kind, int id, CancellationToken ct = default)
    {
        var row = kind == TrackerKind.BusinessRequest
            ? await ProjectBr(_db.BusinessRequests.Where(b => b.Id == id)).FirstOrDefaultAsync(ct)
            : await ProjectIncident(_db.Incidents.Where(b => b.Id == id)).FirstOrDefaultAsync(ct);
        if (row is not null) await AttachLinksAsync(kind, new List<TrackerRecord> { row }, id, ct);
        return row;
    }

    public Task<bool> ExistsAsync(TrackerKind kind, int id, CancellationToken ct = default) =>
        kind == TrackerKind.BusinessRequest ? _db.BusinessRequests.AnyAsync(b => b.Id == id, ct) : _db.Incidents.AnyAsync(b => b.Id == id, ct);

    public Task<bool> IsValueInUseAsync(TrackerKind kind, string field, string value, int? excludeId, CancellationToken ct = default)
    {
        if (kind == TrackerKind.BusinessRequest)
        {
            var q = _db.BusinessRequests.Where(b => excludeId == null || b.Id != excludeId);
            return field == "cr" ? q.AnyAsync(b => b.Cr == value, ct) : q.AnyAsync(b => b.BrNo == value, ct);
        }
        var qi = _db.Incidents.Where(b => excludeId == null || b.Id != excludeId);
        return field == "cr" ? qi.AnyAsync(b => b.Cr == value, ct) : qi.AnyAsync(b => b.IncidentRef == value, ct);
    }

    // ------------------------------------------------------------------ writes

    private static void Apply(BusinessRequest e, TrackerData d)
    {
        e.BrNo = d.RefNo; e.Cr = d.Cr; e.SubmissionDate = d.SubmissionDate; e.BusinessUnitId = d.BusinessUnitId;
        e.BusinessCategoryId = d.BusinessCategoryId; e.RequesterId = d.RequesterId; e.Title = d.Title; e.Description = d.Description;
        e.PriorityId = d.PriorityId; e.StageId = d.StageId; e.StatusId = d.StatusId; e.NewgenRelated = d.NewgenRelated; e.Estimated = d.Estimated;
        e.BaRequired = d.BaRequired; e.BrdAssigned = d.BrdAssigned; e.TechOwnerId = d.TechOwnerId; e.Regulatory = d.Regulatory;
        e.RegulatorId = d.RegulatorId; e.RegulatoryDeadline = d.RegulatoryDeadline; e.AnalysisPlannedStartDate = d.AnalysisPlannedStartDate;
        e.AnalysisPlannedEndDate = d.AnalysisPlannedEndDate; e.DevPlannedStartDate = d.DevPlannedStartDate; e.DevPlannedEndDate = d.DevPlannedEndDate;
        e.QaPlannedStartDate = d.QaPlannedStartDate; e.QaPlannedEndDate = d.QaPlannedEndDate; e.UatPlannedStartDate = d.UatPlannedStartDate;
        e.UatPlannedDate = d.UatPlannedDate; e.CabDate = d.CabDate; e.DeploymentPlannedEndDate = d.DeploymentPlannedEndDate;
        e.ClosedDate = d.ClosedDate; e.RmId = d.RmId;
    }

    private static void Apply(Incident e, TrackerData d)
    {
        e.IncidentRef = d.RefNo; e.Cr = d.Cr; e.BusinessUnitId = d.BusinessUnitId; e.RequesterId = d.RequesterId; e.Title = d.Title;
        e.Description = d.Description; e.PriorityId = d.PriorityId; e.StageId = d.StageId; e.StatusId = d.StatusId;
        e.NewgenRelated = d.NewgenRelated; e.Estimated = d.Estimated; e.TechOwnerId = d.TechOwnerId;
        e.DevPlannedStartDate = d.DevPlannedStartDate; e.DevPlannedEndDate = d.DevPlannedEndDate; e.QaPlannedStartDate = d.QaPlannedStartDate;
        e.QaPlannedEndDate = d.QaPlannedEndDate; e.UatPlannedStartDate = d.UatPlannedStartDate; e.UatPlannedDate = d.UatPlannedDate;
        e.CabDate = d.CabDate; e.DeploymentPlannedEndDate = d.DeploymentPlannedEndDate; e.ClosedDate = d.ClosedDate; e.RmId = d.RmId;
    }

    /// <summary>Replaces the tech-owner set (always) and the systems (only when provided).</summary>
    internal async Task SetLinksAsync(TrackerKind kind, int id, IReadOnlyList<int>? techOwnerIds, IReadOnlyList<int>? systemIds, CancellationToken ct)
    {
        if (kind == TrackerKind.BusinessRequest)
        {
            if (techOwnerIds is not null)
            {
                await _db.BusinessRequestTechOwners.Where(x => x.BusinessRequestId == id).ExecuteDeleteAsync(ct);
                _db.BusinessRequestTechOwners.AddRange(techOwnerIds.Select(t => new BusinessRequestTechOwner { BusinessRequestId = id, TechOwnerId = t }));
            }
            if (systemIds is not null)
            {
                await _db.BusinessRequestSystems.Where(x => x.BusinessRequestId == id).ExecuteDeleteAsync(ct);
                _db.BusinessRequestSystems.AddRange(systemIds.Select(s => new BusinessRequestSystem { BusinessRequestId = id, SystemId = s }));
            }
        }
        else
        {
            if (techOwnerIds is not null)
            {
                await _db.IncidentTechOwners.Where(x => x.IncidentId == id).ExecuteDeleteAsync(ct);
                _db.IncidentTechOwners.AddRange(techOwnerIds.Select(t => new IncidentTechOwner { IncidentId = id, TechOwnerId = t }));
            }
            if (systemIds is not null)
            {
                await _db.IncidentSystems.Where(x => x.IncidentId == id).ExecuteDeleteAsync(ct);
                _db.IncidentSystems.AddRange(systemIds.Select(s => new IncidentSystem { IncidentId = id, SystemId = s }));
            }
        }
        await DbHelpers.SaveAsync(_db, ct);
    }

    public async Task<int> CreateAsync(TrackerKind kind, TrackerData data, string? changedBy, CancellationToken ct = default)
    {
        var now = _clock.UtcNow;
        int id;
        if (kind == TrackerKind.BusinessRequest)
        {
            var e = new BusinessRequest { CreatedAt = now, UpdatedAt = now, CreatedBy = changedBy, UpdatedBy = changedBy };
            Apply(e, data);
            _db.BusinessRequests.Add(e);
            await DbHelpers.SaveAsync(_db, ct);
            id = e.Id;
        }
        else
        {
            var e = new Incident { CreatedAt = now, UpdatedAt = now, CreatedBy = changedBy, UpdatedBy = changedBy };
            Apply(e, data);
            _db.Incidents.Add(e);
            await DbHelpers.SaveAsync(_db, ct);
            id = e.Id;
        }
        await SetLinksAsync(kind, id, data.TechOwnerIds, data.SystemIds, ct);
        return id;
    }

    public async Task UpdateAsync(TrackerKind kind, int id, TrackerData data, string? changedBy, CancellationToken ct = default)
    {
        var now = _clock.UtcNow;
        if (kind == TrackerKind.BusinessRequest)
        {
            var e = await _db.BusinessRequests.FirstAsync(b => b.Id == id, ct);
            Apply(e, data);
            e.UpdatedAt = now;
            e.UpdatedBy = changedBy;
        }
        else
        {
            var e = await _db.Incidents.FirstAsync(b => b.Id == id, ct);
            Apply(e, data);
            e.UpdatedAt = now;
            e.UpdatedBy = changedBy;
        }
        await DbHelpers.SaveAsync(_db, ct);
        await SetLinksAsync(kind, id, data.TechOwnerIds, data.SystemIds, ct);
    }

    public async Task<Dictionary<string, object?>?> GetRawRowAsync(TrackerKind kind, int id, CancellationToken ct = default)
    {
        if (kind == TrackerKind.BusinessRequest)
        {
            var e = await _db.BusinessRequests.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct);
            return e is null ? null : RawRows.Of(e);
        }
        var i = await _db.Incidents.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct);
        return i is null ? null : RawRows.Of(i);
    }

    public async Task DeleteAsync(TrackerKind kind, int id, CancellationToken ct = default)
    {
        // Child rows (history, comments, junctions, follows, SLA tracking) are removed by ON DELETE CASCADE.
        if (kind == TrackerKind.BusinessRequest) await _db.BusinessRequests.Where(b => b.Id == id).ExecuteDeleteAsync(ct);
        else await _db.Incidents.Where(b => b.Id == id).ExecuteDeleteAsync(ct);
    }

    public async Task<bool> BulkUpdateAsync(TrackerKind kind, int id, IReadOnlyDictionary<string, int?> fields, string? changedBy, CancellationToken ct = default)
    {
        var now = _clock.UtcNow;
        if (kind == TrackerKind.BusinessRequest)
        {
            var e = await _db.BusinessRequests.FirstOrDefaultAsync(b => b.Id == id, ct);
            if (e is null) return false;
            foreach (var (field, value) in fields)
            {
                switch (field)
                {
                    case "business_unit_id": e.BusinessUnitId = value; break;
                    case "business_category_id": e.BusinessCategoryId = value; break;
                    case "requester_id": e.RequesterId = value; break;
                    case "priority_id": e.PriorityId = value; break;
                    case "stage_id": e.StageId = value; break;
                    case "status_id": e.StatusId = value; break;
                    case "tech_owner_id": e.TechOwnerId = value; break;
                    case "regulator_id": e.RegulatorId = value; break;
                    case "rm_id": e.RmId = value; break;
                }
            }
            e.UpdatedAt = now;
            e.UpdatedBy = changedBy;
        }
        else
        {
            var e = await _db.Incidents.FirstOrDefaultAsync(b => b.Id == id, ct);
            if (e is null) return false;
            foreach (var (field, value) in fields)
            {
                switch (field)
                {
                    case "business_unit_id": e.BusinessUnitId = value; break;
                    case "requester_id": e.RequesterId = value; break;
                    case "priority_id": e.PriorityId = value; break;
                    case "stage_id": e.StageId = value; break;
                    case "status_id": e.StatusId = value; break;
                    case "tech_owner_id": e.TechOwnerId = value; break;
                    case "rm_id": e.RmId = value; break;
                }
            }
            e.UpdatedAt = now;
            e.UpdatedBy = changedBy;
        }
        await DbHelpers.SaveAsync(_db, ct);
        if (fields.TryGetValue("tech_owner_id", out var owner))
        {
            await SetLinksAsync(kind, id, owner is { } o ? new[] { o } : Array.Empty<int>(), null, ct);
        }
        return true;
    }

    // ------------------------------------------------------------------ history & comments

    public async Task AddHistoryAsync(TrackerKind kind, int id, string changeType, string snapshotJson, string? changedBy, CancellationToken ct = default)
    {
        var now = _clock.UtcNow;
        if (kind == TrackerKind.BusinessRequest)
        {
            _db.BusinessRequestHistory.Add(new BusinessRequestHistory { BusinessRequestId = id, ChangeType = changeType, SnapshotJson = snapshotJson, ChangedBy = changedBy, ChangedAt = now });
        }
        else
        {
            _db.IncidentHistory.Add(new IncidentHistory { IncidentId = id, ChangeType = changeType, SnapshotJson = snapshotJson, ChangedBy = changedBy, ChangedAt = now });
        }
        await DbHelpers.SaveAsync(_db, ct);
    }

    public Task<List<HistoryRow>> ListHistoryAsync(TrackerKind kind, int id, CancellationToken ct = default) =>
        kind == TrackerKind.BusinessRequest
            ? _db.BusinessRequestHistory.AsNoTracking().Where(h => h.BusinessRequestId == id).OrderByDescending(h => h.ChangedAt).ThenByDescending(h => h.Id)
                .Select(h => new HistoryRow(h.Id, h.ChangedAt, h.ChangedBy, h.ChangeType, h.SnapshotJson)).ToListAsync(ct)
            : _db.IncidentHistory.AsNoTracking().Where(h => h.IncidentId == id).OrderByDescending(h => h.ChangedAt).ThenByDescending(h => h.Id)
                .Select(h => new HistoryRow(h.Id, h.ChangedAt, h.ChangedBy, h.ChangeType, h.SnapshotJson)).ToListAsync(ct);

    public Task<List<CommentDto>> ListCommentsAsync(TrackerKind kind, int id, CancellationToken ct = default) =>
        kind == TrackerKind.BusinessRequest
            ? _db.RequestComments.AsNoTracking().Where(c => c.BusinessRequestId == id).OrderBy(c => c.CreatedAt).ThenBy(c => c.Id)
                .Select(c => new CommentDto(c.Id, c.Body, c.CreatedAt, c.CreatedBy)).ToListAsync(ct)
            : _db.IncidentComments.AsNoTracking().Where(c => c.IncidentId == id).OrderBy(c => c.CreatedAt).ThenBy(c => c.Id)
                .Select(c => new CommentDto(c.Id, c.Body, c.CreatedAt, c.CreatedBy)).ToListAsync(ct);

    public async Task<CommentDto> AddCommentAsync(TrackerKind kind, int id, string body, string? createdBy, CancellationToken ct = default)
    {
        var now = _clock.UtcNow;
        if (kind == TrackerKind.BusinessRequest)
        {
            var c = new RequestComment { BusinessRequestId = id, Body = body, CreatedBy = createdBy, CreatedAt = now };
            _db.RequestComments.Add(c);
            await DbHelpers.SaveAsync(_db, ct);
            return new CommentDto(c.Id, c.Body, c.CreatedAt, c.CreatedBy);
        }
        var ic = new IncidentComment { IncidentId = id, Body = body, CreatedBy = createdBy, CreatedAt = now };
        _db.IncidentComments.Add(ic);
        await DbHelpers.SaveAsync(_db, ct);
        return new CommentDto(ic.Id, ic.Body, ic.CreatedAt, ic.CreatedBy);
    }

    public async Task<bool> DeleteCommentAsync(TrackerKind kind, int id, int commentId, CancellationToken ct = default)
    {
        var count = kind == TrackerKind.BusinessRequest
            ? await _db.RequestComments.Where(c => c.Id == commentId && c.BusinessRequestId == id).ExecuteDeleteAsync(ct)
            : await _db.IncidentComments.Where(c => c.Id == commentId && c.IncidentId == id).ExecuteDeleteAsync(ct);
        return count > 0;
    }

    public async Task<Dictionary<int, List<CommentDto>>> ListAllCommentsAsync(TrackerKind kind, CancellationToken ct = default)
    {
        var rows = kind == TrackerKind.BusinessRequest
            ? await _db.RequestComments.AsNoTracking().OrderBy(c => c.CreatedAt).ThenBy(c => c.Id)
                .Select(c => new { OwnerId = c.BusinessRequestId, Dto = new CommentDto(c.Id, c.Body, c.CreatedAt, c.CreatedBy) }).ToListAsync(ct)
            : await _db.IncidentComments.AsNoTracking().OrderBy(c => c.CreatedAt).ThenBy(c => c.Id)
                .Select(c => new { OwnerId = c.IncidentId, Dto = new CommentDto(c.Id, c.Body, c.CreatedAt, c.CreatedBy) }).ToListAsync(ct);
        return rows.GroupBy(r => r.OwnerId).ToDictionary(g => g.Key, g => g.Select(x => x.Dto).ToList());
    }

    // ------------------------------------------------------------------ follows

    public Task<List<int>> ListFollowedIdsAsync(TrackerKind kind, int userId, CancellationToken ct = default) =>
        kind == TrackerKind.BusinessRequest
            ? _db.BusinessRequestFollows.AsNoTracking().Where(f => f.UserId == userId).Select(f => f.BusinessRequestId).ToListAsync(ct)
            : _db.IncidentFollows.AsNoTracking().Where(f => f.UserId == userId).Select(f => f.IncidentId).ToListAsync(ct);

    public async Task FollowAsync(TrackerKind kind, int id, int userId, CancellationToken ct = default)
    {
        var now = _clock.UtcNow;
        if (kind == TrackerKind.BusinessRequest)
        {
            if (await _db.BusinessRequestFollows.AnyAsync(f => f.UserId == userId && f.BusinessRequestId == id, ct)) return;
            _db.BusinessRequestFollows.Add(new BusinessRequestFollow { UserId = userId, BusinessRequestId = id, CreatedAt = now });
        }
        else
        {
            if (await _db.IncidentFollows.AnyAsync(f => f.UserId == userId && f.IncidentId == id, ct)) return;
            _db.IncidentFollows.Add(new IncidentFollow { UserId = userId, IncidentId = id, CreatedAt = now });
        }
        try
        {
            await DbHelpers.SaveAsync(_db, ct);
        }
        catch (DuplicateKeyException)
        {
            // Followed concurrently from another tab: already in the desired state.
        }
    }

    public async Task UnfollowAsync(TrackerKind kind, int id, int userId, CancellationToken ct = default)
    {
        if (kind == TrackerKind.BusinessRequest) await _db.BusinessRequestFollows.Where(f => f.UserId == userId && f.BusinessRequestId == id).ExecuteDeleteAsync(ct);
        else await _db.IncidentFollows.Where(f => f.UserId == userId && f.IncidentId == id).ExecuteDeleteAsync(ct);
    }
}
