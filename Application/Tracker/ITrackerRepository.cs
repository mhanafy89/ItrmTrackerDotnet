using ItrmTracker.Application.Sla;

namespace ItrmTracker.Application.Tracker;

/// <summary>Raised by the repository when a unique index (BR No / Incident ID / CR / lookup name / username) is violated.</summary>
public sealed class DuplicateKeyException : Exception
{
    public DuplicateKeyException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Raised by the repository when a row is still referenced by a foreign key (SQL error 547).</summary>
public sealed class ReferenceConstraintException : Exception
{
    public ReferenceConstraintException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Data access for business requests and incidents (one implementation handles both trackers).</summary>
public interface ITrackerRepository
{
    Task<T> InTransactionAsync<T>(Func<Task<T>> work, CancellationToken ct = default);

    /// <summary>Every record with lookup names, tech owners, systems and last comment, newest update first.</summary>
    Task<List<TrackerRecord>> ListAsync(TrackerKind kind, CancellationToken ct = default);
    Task<TrackerRecord?> GetAsync(TrackerKind kind, int id, CancellationToken ct = default);
    Task<bool> ExistsAsync(TrackerKind kind, int id, CancellationToken ct = default);

    /// <summary><paramref name="field"/> is "ref" (br_no / incident_id) or "cr".</summary>
    Task<bool> IsValueInUseAsync(TrackerKind kind, string field, string value, int? excludeId, CancellationToken ct = default);

    Task<int> CreateAsync(TrackerKind kind, TrackerData data, string? changedBy, CancellationToken ct = default);
    Task UpdateAsync(TrackerKind kind, int id, TrackerData data, string? changedBy, CancellationToken ct = default);

    /// <summary>The raw table row (SELECT *) as snake_case key/value pairs, or null.</summary>
    Task<Dictionary<string, object?>?> GetRawRowAsync(TrackerKind kind, int id, CancellationToken ct = default);
    Task DeleteAsync(TrackerKind kind, int id, CancellationToken ct = default);

    /// <summary>Updates only the given lookup columns (snake_case keys). Returns false when the record does not exist.</summary>
    Task<bool> BulkUpdateAsync(TrackerKind kind, int id, IReadOnlyDictionary<string, int?> fields, string? changedBy, CancellationToken ct = default);

    Task AddHistoryAsync(TrackerKind kind, int id, string changeType, string snapshotJson, string? changedBy, CancellationToken ct = default);
    Task<List<HistoryRow>> ListHistoryAsync(TrackerKind kind, int id, CancellationToken ct = default);

    Task<List<CommentDto>> ListCommentsAsync(TrackerKind kind, int id, CancellationToken ct = default);
    Task<CommentDto> AddCommentAsync(TrackerKind kind, int id, string body, string? createdBy, CancellationToken ct = default);
    Task<bool> DeleteCommentAsync(TrackerKind kind, int id, int commentId, CancellationToken ct = default);

    /// <summary>Every comment (oldest first) keyed by record id – used by the export's "Comments" column.</summary>
    Task<Dictionary<int, List<CommentDto>>> ListAllCommentsAsync(TrackerKind kind, CancellationToken ct = default);

    Task<List<int>> ListFollowedIdsAsync(TrackerKind kind, int userId, CancellationToken ct = default);
    Task FollowAsync(TrackerKind kind, int id, int userId, CancellationToken ct = default);
    Task UnfollowAsync(TrackerKind kind, int id, int userId, CancellationToken ct = default);
}
