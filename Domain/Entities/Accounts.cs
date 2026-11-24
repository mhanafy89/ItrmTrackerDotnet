namespace ItrmTracker.Domain.Entities;

/// <summary>dbo.users – application accounts (username/password + display name + privilege level).</summary>
public class AppUser
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Either the legacy Node.js format <c>scryptSaltHex$scryptHashHex</c> or an ASP.NET Core Identity V3 hash.
    /// </summary>
    public string PasswordHash { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>read_only | read_write (see <see cref="Constants.AppRoles"/>).</summary>
    public string Role { get; set; } = Constants.AppRoles.ReadOnly;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// dbo.sessions – server-side sessions of the legacy Node.js app. Not used by the modernized app
/// (which uses ASP.NET Core authentication cookies) but kept so both apps can share the database.
/// </summary>
public class UserSession
{
    public string Token { get; set; } = string.Empty;
    public int UserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
}

/// <summary>dbo.import_batches – one row per committed import file.</summary>
public class ImportBatch
{
    public int Id { get; set; }
    public string? Filename { get; set; }
    public string? FileType { get; set; }
    public string Target { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public int TotalRows { get; set; }
    public int NewCount { get; set; }
    public int UpdatedCount { get; set; }
    public int DuplicateCount { get; set; }
    public int SkippedCount { get; set; }
    public int InvalidCount { get; set; }
    public int ErrorCount { get; set; }
    public string? SummaryJson { get; set; }
    public ICollection<ImportBatchRow> Rows { get; set; } = new List<ImportBatchRow>();
}

/// <summary>dbo.import_batch_rows – per-row audit of an import.</summary>
public class ImportBatchRow
{
    public int Id { get; set; }
    public int BatchId { get; set; }
    public int RowIndex { get; set; }
    public string Action { get; set; } = string.Empty;
    public string Outcome { get; set; } = string.Empty;
    public int? EntityId { get; set; }
    public string? KeyValue { get; set; }
    public string? Title { get; set; }
    public string? DiffJson { get; set; }
    public string? Message { get; set; }
    public string? RawJson { get; set; }
}

/// <summary>dbo.schema_migrations – migration log of the legacy app (kept for coexistence).</summary>
public class LegacySchemaMigration
{
    public string Name { get; set; } = string.Empty;
    public DateTime AppliedAt { get; set; }
}
