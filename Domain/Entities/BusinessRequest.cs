namespace ItrmTracker.Domain.Entities;

/// <summary>dbo.business_requests – the main tracker record.</summary>
public class BusinessRequest
{
    public int Id { get; set; }
    public string? BrNo { get; set; }
    public string? Cr { get; set; }
    public DateOnly? SubmissionDate { get; set; }
    public int? BusinessUnitId { get; set; }
    public int? BusinessCategoryId { get; set; }

    /// <summary>Legacy free-text requester (superseded by <see cref="RequesterId"/>; still shown as a fallback).</summary>
    public string? RequesterText { get; set; }
    public int? RequesterId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int? PriorityId { get; set; }
    public int? StageId { get; set; }
    public int? StatusId { get; set; }
    public bool NewgenRelated { get; set; }
    public bool Estimated { get; set; }
    public bool BaRequired { get; set; }
    public bool BrdAssigned { get; set; }

    /// <summary>"Primary" tech owner kept for backward compatibility; the full set lives in <see cref="TechOwners"/>.</summary>
    public int? TechOwnerId { get; set; }
    public bool Regulatory { get; set; }
    public int? RegulatorId { get; set; }
    public DateOnly? RegulatoryDeadline { get; set; }
    public DateOnly? AnalysisPlannedStartDate { get; set; }
    public DateOnly? AnalysisPlannedEndDate { get; set; }
    public DateOnly? DevPlannedStartDate { get; set; }
    public DateOnly? DevPlannedEndDate { get; set; }
    public DateOnly? QaPlannedStartDate { get; set; }
    public DateOnly? QaPlannedEndDate { get; set; }
    public DateOnly? UatPlannedStartDate { get; set; }
    public DateOnly? UatPlannedDate { get; set; }
    public DateOnly? CabDate { get; set; }
    public DateOnly? DeploymentPlannedEndDate { get; set; }
    public DateOnly? ClosedDate { get; set; }

    /// <summary>Legacy comments column, superseded by <see cref="RequestComment"/>.</summary>
    public string? Comments { get; set; }
    public int? RmId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public string? UpdatedBy { get; set; }

    public BusinessUnit? BusinessUnit { get; set; }
    public BusinessCategory? BusinessCategory { get; set; }
    public Requester? Requester { get; set; }
    public Priority? Priority { get; set; }
    public Stage? Stage { get; set; }
    public Status? Status { get; set; }
    public TechOwner? TechOwner { get; set; }
    public Regulator? Regulator { get; set; }
    public Rm? Rm { get; set; }

    public ICollection<BusinessRequestTechOwner> TechOwners { get; set; } = new List<BusinessRequestTechOwner>();
    public ICollection<BusinessRequestSystem> Systems { get; set; } = new List<BusinessRequestSystem>();
    public ICollection<RequestComment> CommentEntries { get; set; } = new List<RequestComment>();
    public ICollection<BusinessRequestHistory> History { get; set; } = new List<BusinessRequestHistory>();
    public ICollection<BusinessRequestFollow> Follows { get; set; } = new List<BusinessRequestFollow>();
}

/// <summary>dbo.business_request_tech_owners (many-to-many).</summary>
public class BusinessRequestTechOwner
{
    public int BusinessRequestId { get; set; }
    public int TechOwnerId { get; set; }
    public BusinessRequest? BusinessRequest { get; set; }
    public TechOwner? TechOwner { get; set; }
}

/// <summary>dbo.business_request_systems (many-to-many).</summary>
public class BusinessRequestSystem
{
    public int BusinessRequestId { get; set; }
    public int SystemId { get; set; }
    public BusinessRequest? BusinessRequest { get; set; }
    public SystemLookup? System { get; set; }
}

/// <summary>dbo.business_request_history – JSON snapshot audit trail.</summary>
public class BusinessRequestHistory
{
    public int Id { get; set; }
    public int BusinessRequestId { get; set; }
    public DateTime ChangedAt { get; set; }
    public string? ChangedBy { get; set; }

    /// <summary>create | update | delete | bulk-update</summary>
    public string ChangeType { get; set; } = string.Empty;
    public string SnapshotJson { get; set; } = string.Empty;
}

/// <summary>dbo.request_comments</summary>
public class RequestComment
{
    public int Id { get; set; }
    public int BusinessRequestId { get; set; }
    public string Body { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
}

/// <summary>dbo.business_request_follows – personal "My Follow-ups" bookmarks.</summary>
public class BusinessRequestFollow
{
    public int UserId { get; set; }
    public int BusinessRequestId { get; set; }
    public DateTime CreatedAt { get; set; }
}
