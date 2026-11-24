namespace ItrmTracker.Domain.Entities;

/// <summary>dbo.incidents – incident tracker (same lookups, fewer fields than business requests).</summary>
public class Incident
{
    public int Id { get; set; }

    /// <summary>The business reference (column incident_id), not the identity key.</summary>
    public string? IncidentRef { get; set; }
    public string? Cr { get; set; }
    public int? BusinessUnitId { get; set; }
    public int? RequesterId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int? PriorityId { get; set; }
    public int? StageId { get; set; }
    public int? StatusId { get; set; }
    public bool NewgenRelated { get; set; }
    public bool Estimated { get; set; }
    public int? TechOwnerId { get; set; }
    public DateOnly? DevPlannedStartDate { get; set; }
    public DateOnly? DevPlannedEndDate { get; set; }
    public DateOnly? QaPlannedStartDate { get; set; }
    public DateOnly? QaPlannedEndDate { get; set; }
    public DateOnly? UatPlannedStartDate { get; set; }
    public DateOnly? UatPlannedDate { get; set; }
    public DateOnly? CabDate { get; set; }
    public DateOnly? DeploymentPlannedEndDate { get; set; }
    public DateOnly? ClosedDate { get; set; }
    public int? RmId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public string? UpdatedBy { get; set; }

    public BusinessUnit? BusinessUnit { get; set; }
    public Requester? Requester { get; set; }
    public Priority? Priority { get; set; }
    public Stage? Stage { get; set; }
    public Status? Status { get; set; }
    public TechOwner? TechOwner { get; set; }
    public Rm? Rm { get; set; }

    public ICollection<IncidentTechOwner> TechOwners { get; set; } = new List<IncidentTechOwner>();
    public ICollection<IncidentSystem> Systems { get; set; } = new List<IncidentSystem>();
    public ICollection<IncidentComment> CommentEntries { get; set; } = new List<IncidentComment>();
    public ICollection<IncidentHistory> History { get; set; } = new List<IncidentHistory>();
    public ICollection<IncidentFollow> Follows { get; set; } = new List<IncidentFollow>();
}

/// <summary>dbo.incident_tech_owners</summary>
public class IncidentTechOwner
{
    public int IncidentId { get; set; }
    public int TechOwnerId { get; set; }
    public Incident? Incident { get; set; }
    public TechOwner? TechOwner { get; set; }
}

/// <summary>dbo.incident_systems</summary>
public class IncidentSystem
{
    public int IncidentId { get; set; }
    public int SystemId { get; set; }
    public Incident? Incident { get; set; }
    public SystemLookup? System { get; set; }
}

/// <summary>dbo.incident_history</summary>
public class IncidentHistory
{
    public int Id { get; set; }
    public int IncidentId { get; set; }
    public DateTime ChangedAt { get; set; }
    public string? ChangedBy { get; set; }
    public string ChangeType { get; set; } = string.Empty;
    public string SnapshotJson { get; set; } = string.Empty;
}

/// <summary>dbo.incident_comments</summary>
public class IncidentComment
{
    public int Id { get; set; }
    public int IncidentId { get; set; }
    public string Body { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
}

/// <summary>dbo.incident_follows</summary>
public class IncidentFollow
{
    public int UserId { get; set; }
    public int IncidentId { get; set; }
    public DateTime CreatedAt { get; set; }
}
