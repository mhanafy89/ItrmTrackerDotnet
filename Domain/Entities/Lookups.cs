namespace ItrmTracker.Domain.Entities;

/// <summary>
/// Base type for the editable pick-lists (the "Lookups" sheet of the original Excel tracker).
/// Every lookup table has an identity key and a unique name.
/// </summary>
public abstract class LookupEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

/// <summary>Lookup with an explicit display order (priorities, stages, statuses).</summary>
public abstract class SortedLookupEntity : LookupEntity
{
    public int SortOrder { get; set; }
}

/// <summary>dbo.business_units</summary>
public class BusinessUnit : LookupEntity { }

/// <summary>dbo.business_categories</summary>
public class BusinessCategory : LookupEntity { }

/// <summary>dbo.priorities</summary>
public class Priority : SortedLookupEntity { }

/// <summary>dbo.stages</summary>
public class Stage : SortedLookupEntity { }

/// <summary>dbo.statuses</summary>
public class Status : SortedLookupEntity { }

/// <summary>dbo.tech_owners</summary>
public class TechOwner : LookupEntity { }

/// <summary>dbo.regulators</summary>
public class Regulator : LookupEntity { }

/// <summary>dbo.requesters</summary>
public class Requester : LookupEntity { }

/// <summary>dbo.rms (relationship managers)</summary>
public class Rm : LookupEntity { }

/// <summary>dbo.systems (named SystemLookup to avoid clashing with the System namespace).</summary>
public class SystemLookup : LookupEntity { }
