using ItrmTracker.Application.Abstractions;
using ItrmTracker.Application.Common;
using ItrmTracker.Application.Tracker;
using Microsoft.Extensions.Caching.Memory;

namespace ItrmTracker.Application.Lookups;

/// <summary>A pick-list value.</summary>
public sealed record LookupItem(int Id, string Name);

/// <summary>Definition of an editable lookup type (URL key → table).</summary>
public sealed record LookupType(string Key, string Table, string Label, bool HasSortOrder);

/// <summary>The lookup types exposed by the original /api/lookups endpoints (same URL keys).</summary>
public static class LookupTypes
{
    public static readonly IReadOnlyList<LookupType> All = new[]
    {
        new LookupType("business-units", "business_units", "Business Units", false),
        new LookupType("business-categories", "business_categories", "Business Categories", false),
        new LookupType("priorities", "priorities", "Priorities", true),
        new LookupType("stages", "stages", "Stages", true),
        new LookupType("statuses", "statuses", "Statuses", true),
        new LookupType("tech-owners", "tech_owners", "Tech Owners", false),
        new LookupType("regulators", "regulators", "Regulators", false),
        new LookupType("requesters", "requesters", "Requesters", false),
        new LookupType("rms", "rms", "RMs", false),
        new LookupType("systems", "systems", "Systems", false),
    };

    public static LookupType? Find(string? key) => All.FirstOrDefault(t => t.Key == key);
}

/// <summary>Persistence of lookup tables.</summary>
public interface ILookupRepository
{
    /// <summary>Values ordered by sort_order, name (sorted lookups) or name.</summary>
    Task<List<LookupItem>> ListAsync(LookupType type, CancellationToken ct = default);
    Task<int> CreateAsync(LookupType type, string name, CancellationToken ct = default);
    Task<bool> RenameAsync(LookupType type, int id, string name, CancellationToken ct = default);
    Task<bool> DeleteAsync(LookupType type, int id, CancellationToken ct = default);
}

/// <summary>All lookups keyed by URL key (the shape of GET /api/lookups).</summary>
public sealed class LookupSet : Dictionary<string, IReadOnlyList<LookupItem>>
{
    public IReadOnlyList<LookupItem> Of(string key) => TryGetValue(key, out var v) ? v : Array.Empty<LookupItem>();
}

public interface ILookupService
{
    Task<LookupSet> GetAllAsync(CancellationToken ct = default);
    Task<LookupItem> CreateAsync(string typeKey, string? name, CancellationToken ct = default);
    Task<LookupItem> RenameAsync(string typeKey, int id, string? name, CancellationToken ct = default);
    Task DeleteAsync(string typeKey, int id, CancellationToken ct = default);
}

/// <summary>Lookup CRUD (port of routes/lookups.js) with a short read cache that is cleared on every change.</summary>
public sealed class LookupService : ILookupService
{
    private const string CacheKey = "itrm:lookups";
    private readonly ILookupRepository _repo;
    private readonly IMemoryCache _cache;
    private readonly IAuditLogger _audit;

    public LookupService(ILookupRepository repo, IMemoryCache cache, IAuditLogger audit)
    {
        _repo = repo;
        _cache = cache;
        _audit = audit;
    }

    public async Task<LookupSet> GetAllAsync(CancellationToken ct = default)
    {
        if (_cache.TryGetValue(CacheKey, out LookupSet? cached) && cached is not null) return cached;
        var set = new LookupSet();
        foreach (var type in LookupTypes.All) set[type.Key] = await _repo.ListAsync(type, ct);
        _cache.Set(CacheKey, set, TimeSpan.FromSeconds(30));
        return set;
    }

    private static LookupType TypeOf(string key) => LookupTypes.Find(key) ?? throw ServiceException.NotFound("Unknown lookup type.");

    private void Changed(string action, LookupType type, object? id, string? name)
    {
        _cache.Remove(CacheKey);
        // Stage/status/priority/category names feed the SLA configuration.
        _cache.Remove("itrm:sla-config");
        _audit.Log($"lookup.{action}", type.Table, id, name);
    }

    public async Task<LookupItem> CreateAsync(string typeKey, string? name, CancellationToken ct = default)
    {
        var type = TypeOf(typeKey);
        var clean = (name ?? string.Empty).Trim();
        if (clean.Length == 0) throw ServiceException.BadRequest("name is required.");
        try
        {
            var id = await _repo.CreateAsync(type, clean, ct);
            Changed("create", type, id, clean);
            return new LookupItem(id, clean);
        }
        catch (DuplicateKeyException)
        {
            throw ServiceException.Conflict($"\"{clean}\" already exists.");
        }
    }

    public async Task<LookupItem> RenameAsync(string typeKey, int id, string? name, CancellationToken ct = default)
    {
        var type = TypeOf(typeKey);
        var clean = (name ?? string.Empty).Trim();
        if (clean.Length == 0) throw ServiceException.BadRequest("name is required.");
        try
        {
            if (id <= 0 || !await _repo.RenameAsync(type, id, clean, ct)) throw ServiceException.NotFound();
            Changed("rename", type, id, clean);
            return new LookupItem(id, clean);
        }
        catch (DuplicateKeyException)
        {
            throw ServiceException.Conflict($"\"{clean}\" already exists.");
        }
    }

    public async Task DeleteAsync(string typeKey, int id, CancellationToken ct = default)
    {
        var type = TypeOf(typeKey);
        try
        {
            if (id <= 0 || !await _repo.DeleteAsync(type, id, ct)) throw ServiceException.NotFound();
            Changed("delete", type, id, null);
        }
        catch (ReferenceConstraintException)
        {
            throw ServiceException.Conflict("This value is still used by existing records and can't be deleted.");
        }
    }
}
