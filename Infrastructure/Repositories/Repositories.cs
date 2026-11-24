using ItrmTracker.Application.Import;
using ItrmTracker.Application.Lookups;
using ItrmTracker.Application.Sla;
using ItrmTracker.Application.Tracker;
using ItrmTracker.Application.Users;
using ItrmTracker.Application.Abstractions;
using ItrmTracker.Domain.Entities;
using ItrmTracker.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ItrmTracker.Infrastructure.Repositories;

/// <summary>SLA assumption tables (port of loadConfig / PUT / reset in lib/sla.js and routes/sla.js).</summary>
public sealed class SlaConfigRepository : ISlaConfigRepository
{
    private readonly ItrmDbContext _db;

    public SlaConfigRepository(ItrmDbContext db) => _db = db;

    public async Task<SlaConfigSource> LoadSourceAsync(CancellationToken ct = default) => new()
    {
        StageNames = await _db.Stages.AsNoTracking().OrderBy(x => x.SortOrder).ThenBy(x => x.Name).Select(x => x.Name).ToListAsync(ct),
        PriorityNames = await _db.Priorities.AsNoTracking().OrderBy(x => x.SortOrder).ThenBy(x => x.Name).Select(x => x.Name).ToListAsync(ct),
        CategoryNames = await _db.BusinessCategories.AsNoTracking().OrderBy(x => x.Name).Select(x => x.Name).ToListAsync(ct),
        StatusNames = await _db.Statuses.AsNoTracking().OrderBy(x => x.SortOrder).ThenBy(x => x.Name).Select(x => x.Name).ToListAsync(ct),
        StoredStages = (await _db.SlaStages.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct))
            .Select(s => new SlaStageDefinition(s.Name, s.StageOrder, s.IsPipeline, s.BrDays, s.IncidentDays, (double)s.EffortDays)).ToList(),
        StoredPriorities = (await _db.SlaPriorities.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct))
            .Select(p => new SlaPriorityDefinition(p.Name, (double)p.Factor, (double)p.ResponseHours, p.UpdateDays)).ToList(),
        StoredCategories = (await _db.SlaCategories.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct))
            .Select(c => new SlaCategoryDefinition(c.Name, (double)c.Factor)).ToList(),
        StoredStatuses = (await _db.SlaStatuses.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct))
            .Select(s => new SlaStatusDefinition(s.Name, s.State)).ToList(),
        Settings = await _db.SlaSettings.AsNoTracking().ToDictionaryAsync(s => s.SettingKey, s => s.SettingValue, ct),
    };

    public Task ReplaceAsync(SlaConfigUpdate update, CancellationToken ct = default) =>
        DbHelpers.InTransactionAsync(_db, async () =>
        {
            await _db.SlaStages.ExecuteDeleteAsync(ct);
            await _db.SlaPriorities.ExecuteDeleteAsync(ct);
            await _db.SlaCategories.ExecuteDeleteAsync(ct);
            await _db.SlaStatuses.ExecuteDeleteAsync(ct);
            _db.SlaStages.AddRange(update.Stages.Select(s => new SlaStageConfig
            {
                Name = s.Name, StageOrder = s.StageOrder, IsPipeline = s.IsPipeline, BrDays = (int)s.BrDays, IncidentDays = (int)s.IncidentDays, EffortDays = (decimal)s.EffortDays,
            }));
            _db.SlaPriorities.AddRange(update.Priorities.Select(p => new SlaPriorityConfig
            {
                Name = p.Name, Factor = (decimal)p.Factor, ResponseHours = (decimal)p.ResponseHours, UpdateDays = (int)p.UpdateDays,
            }));
            _db.SlaCategories.AddRange(update.Categories.Select(c => new SlaCategoryConfig { Name = c.Name, Factor = (decimal)c.Factor }));
            _db.SlaStatuses.AddRange(update.Statuses.Select(s => new SlaStatusConfig { Name = s.Name, State = s.State }));
            foreach (var (key, value) in update.Settings)
            {
                await _db.SlaSettings.Where(s => s.SettingKey == key).ExecuteDeleteAsync(ct);
                _db.SlaSettings.Add(new SlaSetting { SettingKey = key, SettingValue = value });
            }
            await DbHelpers.SaveAsync(_db, ct);
            _db.ChangeTracker.Clear();
            return true;
        }, ct);

    public Task ResetAsync(CancellationToken ct = default) =>
        DbHelpers.InTransactionAsync(_db, async () =>
        {
            await _db.SlaStages.ExecuteDeleteAsync(ct);
            await _db.SlaPriorities.ExecuteDeleteAsync(ct);
            await _db.SlaCategories.ExecuteDeleteAsync(ct);
            await _db.SlaStatuses.ExecuteDeleteAsync(ct);
            await _db.SlaSettings.ExecuteDeleteAsync(ct);
            return true;
        }, ct);
}

/// <summary>Lookup tables (port of routes/lookups.js); one generic implementation for the ten lookup types.</summary>
public sealed class LookupRepository : ILookupRepository
{
    private readonly ItrmDbContext _db;

    public LookupRepository(ItrmDbContext db) => _db = db;

    private Task<List<LookupItem>> List<T>(bool sorted, CancellationToken ct) where T : LookupEntity =>
        _db.Set<T>().AsNoTracking().OrderBy(x => x.Name).Select(x => new LookupItem(x.Id, x.Name)).ToListAsync(ct);

    public Task<List<LookupItem>> ListAsync(LookupType type, CancellationToken ct = default) => type.Table switch
    {
        "business_units" => List<BusinessUnit>(false, ct),
        "business_categories" => List<BusinessCategory>(false, ct),
        "priorities" => _db.Priorities.AsNoTracking().OrderBy(x => x.SortOrder).ThenBy(x => x.Name).Select(x => new LookupItem(x.Id, x.Name)).ToListAsync(ct),
        "stages" => _db.Stages.AsNoTracking().OrderBy(x => x.SortOrder).ThenBy(x => x.Name).Select(x => new LookupItem(x.Id, x.Name)).ToListAsync(ct),
        "statuses" => _db.Statuses.AsNoTracking().OrderBy(x => x.SortOrder).ThenBy(x => x.Name).Select(x => new LookupItem(x.Id, x.Name)).ToListAsync(ct),
        "tech_owners" => List<TechOwner>(false, ct),
        "regulators" => List<Regulator>(false, ct),
        "requesters" => List<Requester>(false, ct),
        "rms" => List<Rm>(false, ct),
        "systems" => List<SystemLookup>(false, ct),
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    internal static LookupEntity New(string table) => table switch
    {
        "business_units" => new BusinessUnit(),
        "business_categories" => new BusinessCategory(),
        "priorities" => new Priority(),
        "stages" => new Stage(),
        "statuses" => new Status(),
        "tech_owners" => new TechOwner(),
        "regulators" => new Regulator(),
        "requesters" => new Requester(),
        "rms" => new Rm(),
        "systems" => new SystemLookup(),
        _ => throw new ArgumentOutOfRangeException(nameof(table)),
    };

    internal static async Task<int> CreateAsync(ItrmDbContext db, string table, string name, CancellationToken ct)
    {
        // New values get sort_order 0 (the column default), exactly like the original INSERT (name) only.
        var entity = New(table);
        entity.Name = name;
        db.Add((object)entity);
        await DbHelpers.SaveAsync(db, ct);
        db.Entry((object)entity).State = EntityState.Detached;
        return entity.Id;
    }

    public Task<int> CreateAsync(LookupType type, string name, CancellationToken ct = default) => CreateAsync(_db, type.Table, name, ct);

    private async Task<bool> RenameCore<T>(int id, string name, CancellationToken ct) where T : LookupEntity
    {
        var entity = await _db.Set<T>().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (entity is null) return false;
        entity.Name = name;
        await DbHelpers.SaveAsync(_db, ct);
        _db.Entry(entity).State = EntityState.Detached;
        return true;
    }

    private async Task<bool> DeleteCore<T>(int id, CancellationToken ct) where T : LookupEntity =>
        await DbHelpers.TranslateAsync(() => _db.Set<T>().Where(x => x.Id == id).ExecuteDeleteAsync(ct)) > 0;

    public Task<bool> RenameAsync(LookupType type, int id, string name, CancellationToken ct = default) => type.Table switch
    {
        "business_units" => RenameCore<BusinessUnit>(id, name, ct),
        "business_categories" => RenameCore<BusinessCategory>(id, name, ct),
        "priorities" => RenameCore<Priority>(id, name, ct),
        "stages" => RenameCore<Stage>(id, name, ct),
        "statuses" => RenameCore<Status>(id, name, ct),
        "tech_owners" => RenameCore<TechOwner>(id, name, ct),
        "regulators" => RenameCore<Regulator>(id, name, ct),
        "requesters" => RenameCore<Requester>(id, name, ct),
        "rms" => RenameCore<Rm>(id, name, ct),
        "systems" => RenameCore<SystemLookup>(id, name, ct),
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public Task<bool> DeleteAsync(LookupType type, int id, CancellationToken ct = default) => type.Table switch
    {
        "business_units" => DeleteCore<BusinessUnit>(id, ct),
        "business_categories" => DeleteCore<BusinessCategory>(id, ct),
        "priorities" => DeleteCore<Priority>(id, ct),
        "stages" => DeleteCore<Stage>(id, ct),
        "statuses" => DeleteCore<Status>(id, ct),
        "tech_owners" => DeleteCore<TechOwner>(id, ct),
        "regulators" => DeleteCore<Regulator>(id, ct),
        "requesters" => DeleteCore<Requester>(id, ct),
        "rms" => DeleteCore<Rm>(id, ct),
        "systems" => DeleteCore<SystemLookup>(id, ct),
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };
}
/// <summary>dbo.users persistence.</summary>
public sealed class UserRepository : IUserRepository
{
    private readonly ItrmDbContext _db;

    public UserRepository(ItrmDbContext db) => _db = db;

    public Task<List<UserListItem>> ListAsync(CancellationToken ct = default) =>
        _db.Users.AsNoTracking().OrderBy(u => u.Username).Select(u => new UserListItem(u.Id, u.Username, u.Name, u.Role, u.IsActive, u.CreatedAt)).ToListAsync(ct);

    public Task<AppUser?> FindAsync(int id, CancellationToken ct = default) => _db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);

    public async Task<int> CreateAsync(AppUser user, CancellationToken ct = default)
    {
        _db.Users.Add(user);
        await DbHelpers.SaveAsync(_db, ct);
        return user.Id;
    }

    public Task UpdateAsync(AppUser user, CancellationToken ct = default)
    {
        if (_db.Entry(user).State == EntityState.Detached) _db.Users.Update(user);
        return DbHelpers.SaveAsync(_db, ct);
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken ct = default) =>
        await _db.Users.Where(u => u.Id == id).ExecuteDeleteAsync(ct) > 0;

    public Task<int> CountAsync(CancellationToken ct = default) => _db.Users.CountAsync(ct);
}

/// <summary>Persistence used by the import wizard (port of the SQL in routes/import.js).</summary>
public sealed class ImportRepository : IImportRepository
{
    private readonly ItrmDbContext _db;
    private readonly IClock _clock;

    public ImportRepository(ItrmDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<Dictionary<string, List<LookupRow>>> LoadLookupsAsync(IEnumerable<string> tables, CancellationToken ct = default)
    {
        var lookups = new LookupRepository(_db);
        var result = new Dictionary<string, List<LookupRow>>();
        foreach (var table in tables)
        {
            var type = LookupTypes.All.First(t => t.Table == table);
            result[table] = (await lookups.ListAsync(type, ct)).Select(x => new LookupRow(x.Id, x.Name)).ToList();
        }
        return result;
    }

    public Task<int> CreateLookupAsync(string table, string name, CancellationToken ct = default) => LookupRepository.CreateAsync(_db, table, name, ct);

    public async Task<Dictionary<string, object?>?> FindByKeyAsync(TrackerKind kind, string field, string value, CancellationToken ct = default)
    {
        if (kind == TrackerKind.BusinessRequest)
        {
            var q = _db.BusinessRequests.AsNoTracking();
            q = field == "cr" ? q.Where(b => b.Cr == value) : q.Where(b => b.BrNo == value);
            var e = await q.OrderBy(b => b.Id).FirstOrDefaultAsync(ct);
            return e is null ? null : RawRows.Of(e);
        }
        var qi = _db.Incidents.AsNoTracking();
        qi = field == "cr" ? qi.Where(b => b.Cr == value) : qi.Where(b => b.IncidentRef == value);
        var i = await qi.OrderBy(b => b.Id).FirstOrDefaultAsync(ct);
        return i is null ? null : RawRows.Of(i);
    }

    public Task<bool> KeyInUseAsync(TrackerKind kind, string field, string value, int? excludeId, CancellationToken ct = default)
    {
        if (kind == TrackerKind.BusinessRequest)
        {
            var q = _db.BusinessRequests.Where(b => excludeId == null || b.Id != excludeId);
            return field == "cr" ? q.AnyAsync(b => b.Cr == value, ct) : q.AnyAsync(b => b.BrNo == value, ct);
        }
        var qi = _db.Incidents.Where(b => excludeId == null || b.Id != excludeId);
        return field == "cr" ? qi.AnyAsync(b => b.Cr == value, ct) : qi.AnyAsync(b => b.IncidentRef == value, ct);
    }

    /// <summary>Keeps the multi tech-owner junction in sync with the single tech_owner_id an imported row carries.</summary>
    private async Task SyncTechOwnerJunctionAsync(TrackerKind kind, int id, int? techOwnerId, CancellationToken ct)
    {
        if (kind == TrackerKind.BusinessRequest)
        {
            await _db.BusinessRequestTechOwners.Where(x => x.BusinessRequestId == id).ExecuteDeleteAsync(ct);
            if (techOwnerId is { } t) _db.BusinessRequestTechOwners.Add(new BusinessRequestTechOwner { BusinessRequestId = id, TechOwnerId = t });
        }
        else
        {
            await _db.IncidentTechOwners.Where(x => x.IncidentId == id).ExecuteDeleteAsync(ct);
            if (techOwnerId is { } t) _db.IncidentTechOwners.Add(new IncidentTechOwner { IncidentId = id, TechOwnerId = t });
        }
        await DbHelpers.SaveAsync(_db, ct);
    }

    private async Task RecordHistoryAsync(TrackerKind kind, int id, string changeType, string? changedBy, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        if (kind == TrackerKind.BusinessRequest)
        {
            var e = await _db.BusinessRequests.AsNoTracking().FirstAsync(b => b.Id == id, ct);
            _db.BusinessRequestHistory.Add(new BusinessRequestHistory { BusinessRequestId = id, ChangeType = changeType, ChangedBy = changedBy, ChangedAt = now, SnapshotJson = TrackerSnapshot.FromRawRow(RawRows.Of(e)) });
        }
        else
        {
            var e = await _db.Incidents.AsNoTracking().FirstAsync(b => b.Id == id, ct);
            _db.IncidentHistory.Add(new IncidentHistory { IncidentId = id, ChangeType = changeType, ChangedBy = changedBy, ChangedAt = now, SnapshotJson = TrackerSnapshot.FromRawRow(RawRows.Of(e)) });
        }
        await DbHelpers.SaveAsync(_db, ct);
    }

    private static int? TechOwnerOf(IReadOnlyDictionary<string, object?> body) => body.GetValueOrDefault("tech_owner_id") is int i ? i : null;

    public async Task<int> InsertEntityAsync(TrackerKind kind, IReadOnlyDictionary<string, object?> body, string? changedBy, CancellationToken ct = default)
    {
        var now = _clock.UtcNow;
        int id;
        if (kind == TrackerKind.BusinessRequest)
        {
            var e = new BusinessRequest { CreatedAt = now, UpdatedAt = now, CreatedBy = changedBy, UpdatedBy = changedBy };
            RawRows.Apply(e, body);
            _db.BusinessRequests.Add(e);
            await DbHelpers.SaveAsync(_db, ct);
            id = e.Id;
        }
        else
        {
            var e = new Incident { CreatedAt = now, UpdatedAt = now, CreatedBy = changedBy, UpdatedBy = changedBy };
            RawRows.Apply(e, body);
            _db.Incidents.Add(e);
            await DbHelpers.SaveAsync(_db, ct);
            id = e.Id;
        }
        _db.ChangeTracker.Clear();
        await SyncTechOwnerJunctionAsync(kind, id, TechOwnerOf(body), ct);
        await RecordHistoryAsync(kind, id, "create", changedBy, ct);
        return id;
    }

    public async Task UpdateEntityAsync(TrackerKind kind, int id, IReadOnlyDictionary<string, object?> body, string? changedBy, CancellationToken ct = default)
    {
        var now = _clock.UtcNow;
        if (kind == TrackerKind.BusinessRequest)
        {
            var e = await _db.BusinessRequests.FirstAsync(b => b.Id == id, ct);
            RawRows.Apply(e, body);
            e.UpdatedAt = now;
            e.UpdatedBy = changedBy;
        }
        else
        {
            var e = await _db.Incidents.FirstAsync(b => b.Id == id, ct);
            RawRows.Apply(e, body);
            e.UpdatedAt = now;
            e.UpdatedBy = changedBy;
        }
        await DbHelpers.SaveAsync(_db, ct);
        _db.ChangeTracker.Clear();
        await SyncTechOwnerJunctionAsync(kind, id, TechOwnerOf(body), ct);
        await RecordHistoryAsync(kind, id, "update", changedBy, ct);
    }

    public async Task<int> CreateBatchAsync(ImportBatch batch, CancellationToken ct = default)
    {
        batch.CreatedAt = _clock.UtcNow;
        _db.ImportBatches.Add(batch);
        await DbHelpers.SaveAsync(_db, ct);
        _db.Entry(batch).State = EntityState.Detached;
        return batch.Id;
    }

    public async Task AddBatchRowAsync(ImportBatchRow row, CancellationToken ct = default)
    {
        _db.ImportBatchRows.Add(row);
        await DbHelpers.SaveAsync(_db, ct);
        _db.Entry(row).State = EntityState.Detached;
    }

    public Task CompleteBatchAsync(int batchId, CommitSummary summary, string summaryJson, CancellationToken ct = default) =>
        _db.ImportBatches.Where(b => b.Id == batchId).ExecuteUpdateAsync(s => s
            .SetProperty(b => b.NewCount, summary.New)
            .SetProperty(b => b.UpdatedCount, summary.Updated)
            .SetProperty(b => b.DuplicateCount, summary.Duplicate)
            .SetProperty(b => b.SkippedCount, summary.Skipped)
            .SetProperty(b => b.InvalidCount, summary.Invalid)
            .SetProperty(b => b.ErrorCount, summary.Error)
            .SetProperty(b => b.SummaryJson, summaryJson), ct);

    public Task<List<ImportBatch>> ListBatchesAsync(int top, CancellationToken ct = default) =>
        _db.ImportBatches.AsNoTracking().OrderByDescending(b => b.CreatedAt).ThenByDescending(b => b.Id).Take(top).ToListAsync(ct);

    public Task<ImportBatch?> GetBatchAsync(int id, CancellationToken ct = default) => _db.ImportBatches.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct);

    public Task<List<ImportBatchRow>> ListBatchRowsAsync(int batchId, CancellationToken ct = default) =>
        _db.ImportBatchRows.AsNoTracking().Where(r => r.BatchId == batchId).OrderBy(r => r.RowIndex).ToListAsync(ct);
}

