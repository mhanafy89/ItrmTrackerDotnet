namespace ItrmTracker.Application.Abstractions;

/// <summary>
/// Clock abstraction (injectable for tests). All persisted timestamps are UTC; "calendar days" for SLA
/// purposes are evaluated in the configured business time zone (the server's local zone by default,
/// exactly like the original app, which used the Node process' local time).
/// </summary>
public interface IClock
{
    /// <summary>Current UTC time truncated to whole seconds (the precision of the DATETIME2(0) columns).</summary>
    DateTime UtcNow { get; }

    /// <summary>The business time zone used to map instants to calendar days.</summary>
    TimeZoneInfo BusinessTimeZone { get; }

    /// <summary>Today's calendar date in the business time zone.</summary>
    DateOnly Today { get; }

    /// <summary>Calendar day of a UTC instant in the business time zone.</summary>
    DateOnly ToBusinessDate(DateTime utc);
}

/// <summary>The authenticated user performing the current operation.</summary>
public interface ICurrentUser
{
    int? UserId { get; }
    string? Username { get; }

    /// <summary>Display name – stamped into created_by / updated_by / changed_by (same as the original app).</summary>
    string? DisplayName { get; }
    bool CanWrite { get; }
}

/// <summary>Structured audit log of important business actions (in addition to the database audit tables).</summary>
public interface IAuditLogger
{
    void Log(string action, string entity, object? entityId, string? details = null);
}
