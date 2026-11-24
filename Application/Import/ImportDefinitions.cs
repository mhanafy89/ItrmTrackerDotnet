using ItrmTracker.Application.Sla;

namespace ItrmTracker.Application.Import;

/// <summary>Field type of an importable column.</summary>
public enum ImportFieldType
{
    Text,
    Date,
    Lookup,
    Bool,
}

/// <summary>An importable field (port of BR_FIELDS / INCIDENT_FIELDS).</summary>
public sealed record ImportField(string Field, string Label, ImportFieldType Type, bool Required = false, string? Table = null, string? IdField = null);

/// <summary>Key used to match uploaded rows to existing records (first non-blank wins).</summary>
public sealed record ImportKey(string Field, string Label);

/// <summary>A storage column written on insert/update.</summary>
public sealed record EntityColumn(string Col, bool IsBool);

/// <summary>Import target definition (business_requests / incidents).</summary>
public sealed record ImportTarget(
    string Key,
    string Label,
    TrackerKind Kind,
    IReadOnlyList<ImportKey> IdKeys,
    IReadOnlyList<ImportField> Fields,
    IReadOnlyList<EntityColumn> EntityCols);

/// <summary>Static definitions of the import &amp; merge wizard (identical to routes/import.js).</summary>
public static class ImportDefinitions
{
    public const int MaxRows = 20000;

    private static ImportField Lookup(string field, string label, string table, string idField) => new(field, label, ImportFieldType.Lookup, false, table, idField);
    private static ImportField Date(string field, string label) => new(field, label, ImportFieldType.Date);
    private static ImportField Bool(string field, string label) => new(field, label, ImportFieldType.Bool);
    private static ImportField Text(string field, string label, bool required = false) => new(field, label, ImportFieldType.Text, required);

    public static readonly IReadOnlyList<ImportField> BrFields = new[]
    {
        Text("br_no", "BR No"),
        Text("cr", "CR"),
        Date("submission_date", "Submission Date"),
        Text("title", "Title", required: true),
        Text("description", "Description"),
        Lookup("business_unit", "Business Unit", "business_units", "business_unit_id"),
        Lookup("business_category", "Business Category", "business_categories", "business_category_id"),
        Lookup("requester", "Requester", "requesters", "requester_id"),
        Lookup("priority", "Priority", "priorities", "priority_id"),
        Lookup("stage", "Stage", "stages", "stage_id"),
        Lookup("status", "Status", "statuses", "status_id"),
        Lookup("tech_owner", "Tech Owner", "tech_owners", "tech_owner_id"),
        Lookup("rm", "RM", "rms", "rm_id"),
        Lookup("regulator", "Regulator", "regulators", "regulator_id"),
        Bool("newgen_related", "Newgen Related"),
        Bool("estimated", "Estimated"),
        Bool("ba_required", "BA Required"),
        Bool("brd_assigned", "BRD Assigned"),
        Bool("regulatory", "Regulatory"),
        Date("regulatory_deadline", "Regulatory Deadline"),
        Date("analysis_planned_start_date", "Analysis Planned Start Date"),
        Date("analysis_planned_end_date", "Analysis Planned End Date"),
        Date("dev_planned_start_date", "Dev Planned Start Date"),
        Date("dev_planned_end_date", "Dev Planned End Date"),
        Date("qa_planned_start_date", "QA Planned Start Date"),
        Date("qa_planned_end_date", "QA Planned End Date"),
        Date("uat_planned_start_date", "UAT Planned Start Date"),
        Date("uat_planned_date", "UAT Planned End Date"),
        Date("cab_date", "CAB Date"),
        Date("deployment_planned_end_date", "Deployment Planned End Date"),
        Date("closed_date", "Closed Date"),
    };

    public static readonly IReadOnlyList<ImportField> IncidentFields = new[]
    {
        Text("incident_id", "Incident ID"),
        Text("cr", "CR"),
        Text("title", "Title", required: true),
        Text("description", "Description"),
        Lookup("business_unit", "Business Unit", "business_units", "business_unit_id"),
        Lookup("requester", "Requester", "requesters", "requester_id"),
        Lookup("priority", "Priority", "priorities", "priority_id"),
        Lookup("stage", "Stage", "stages", "stage_id"),
        Lookup("status", "Status", "statuses", "status_id"),
        Lookup("tech_owner", "Tech Owner", "tech_owners", "tech_owner_id"),
        Lookup("rm", "RM", "rms", "rm_id"),
        Bool("newgen_related", "Newgen Related"),
        Bool("estimated", "Estimated"),
        Date("dev_planned_start_date", "Dev Planned Start Date"),
        Date("dev_planned_end_date", "Dev Planned End Date"),
        Date("qa_planned_start_date", "QA Planned Start Date"),
        Date("qa_planned_end_date", "QA Planned End Date"),
        Date("uat_planned_start_date", "UAT Planned Start Date"),
        Date("uat_planned_date", "UAT Planned End Date"),
        Date("cab_date", "CAB Date"),
        Date("deployment_planned_end_date", "Deployment Planned End Date"),
        Date("closed_date", "Closed Date"),
    };

    private static IReadOnlyList<EntityColumn> Cols(params (string Col, bool Bool)[] cols) => cols.Select(c => new EntityColumn(c.Col, c.Bool)).ToList();

    public static readonly IReadOnlyList<EntityColumn> BrEntityCols = Cols(
        ("br_no", false), ("cr", false), ("submission_date", false), ("business_unit_id", false), ("business_category_id", false), ("requester_id", false),
        ("title", false), ("description", false), ("priority_id", false), ("stage_id", false), ("status_id", false), ("newgen_related", true), ("estimated", true),
        ("ba_required", true), ("brd_assigned", true), ("tech_owner_id", false), ("regulatory", true), ("regulator_id", false),
        ("regulatory_deadline", false), ("analysis_planned_start_date", false), ("analysis_planned_end_date", false), ("dev_planned_start_date", false),
        ("dev_planned_end_date", false), ("qa_planned_start_date", false), ("qa_planned_end_date", false), ("uat_planned_start_date", false), ("uat_planned_date", false),
        ("cab_date", false), ("deployment_planned_end_date", false), ("rm_id", false), ("closed_date", false));

    public static readonly IReadOnlyList<EntityColumn> IncidentEntityCols = Cols(
        ("incident_id", false), ("cr", false), ("business_unit_id", false), ("requester_id", false), ("title", false), ("description", false),
        ("priority_id", false), ("stage_id", false), ("status_id", false), ("newgen_related", true), ("estimated", true),
        ("tech_owner_id", false), ("dev_planned_start_date", false), ("dev_planned_end_date", false), ("qa_planned_start_date", false),
        ("qa_planned_end_date", false), ("uat_planned_start_date", false), ("uat_planned_date", false),
        ("cab_date", false), ("deployment_planned_end_date", false), ("rm_id", false), ("closed_date", false));

    public static readonly IReadOnlyDictionary<string, ImportTarget> Targets = new Dictionary<string, ImportTarget>
    {
        ["business_requests"] = new("business_requests", "Business Requests", TrackerKind.BusinessRequest,
            new[] { new ImportKey("br_no", "BR No"), new ImportKey("cr", "CR") }, BrFields, BrEntityCols),
        ["incidents"] = new("incidents", "Incidents", TrackerKind.Incident,
            new[] { new ImportKey("incident_id", "Incident ID"), new ImportKey("cr", "CR") }, IncidentFields, IncidentEntityCols),
    };

    /// <summary>Header aliases used to auto-detect column mappings.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> FieldAliases = new Dictionary<string, string[]>
    {
        ["br_no"] = new[] { "brno", "br", "brnumber", "requestno", "requestnumber", "businessrequestno" },
        ["incident_id"] = new[] { "incidentid", "incidentno", "incidentnumber", "incident", "inc", "id" },
        ["cr"] = new[] { "cr", "crno", "crnumber", "changerequest", "changerequestno" },
        ["submission_date"] = new[] { "submissiondate", "dateofsubmission", "submitted", "submitteddate", "requestdate" },
        ["title"] = new[] { "title", "subject", "summary", "requesttitle", "name" },
        ["description"] = new[] { "description", "desc", "details", "detail" },
        ["business_unit"] = new[] { "businessunit", "bu", "department", "dept", "unit" },
        ["business_category"] = new[] { "businesscategory", "category", "bucategory", "requestcategory" },
        ["requester"] = new[] { "requester", "requestedby", "requestor", "raisedby" },
        ["priority"] = new[] { "priority", "prio" },
        ["stage"] = new[] { "stage", "phase" },
        ["status"] = new[] { "status", "state" },
        ["tech_owner"] = new[] { "techowner", "technicalowner", "owner", "developer", "assignedto" },
        ["rm"] = new[] { "rm", "relationshipmanager", "relmanager" },
        ["regulator"] = new[] { "regulator", "regulatorybody" },
        ["newgen_related"] = new[] { "newgenrelated", "newgen" },
        ["estimated"] = new[] { "estimated", "isestimated" },
        ["ba_required"] = new[] { "barequired", "ba", "businessanalyst" },
        ["brd_assigned"] = new[] { "brdassigned", "brd" },
        ["regulatory"] = new[] { "regulatory", "isregulatory" },
        ["regulatory_deadline"] = new[] { "regulatorydeadline", "regdeadline" },
        ["analysis_planned_start_date"] = new[] { "analysisplannedstartdate", "analysisstartdate", "analysisstart" },
        ["analysis_planned_end_date"] = new[] { "analysisplannedenddate", "analysisplanned", "analysisdate", "analysis" },
        ["dev_planned_start_date"] = new[] { "devplannedstartdate", "devstartdate", "devstart" },
        ["dev_planned_end_date"] = new[] { "devplannedenddate", "devplanned", "devdate", "development", "dev" },
        ["qa_planned_start_date"] = new[] { "qaplannedstartdate", "qastartdate", "qastart" },
        ["qa_planned_end_date"] = new[] { "qaplannedenddate", "qaplanned", "qadate", "qa", "testing" },
        ["uat_planned_start_date"] = new[] { "uatplannedstartdate", "uatstartdate", "uatstart" },
        ["uat_planned_date"] = new[] { "uatplannedenddate", "uatplanneddate", "uatplanned", "uatdate", "uat" },
        ["cab_date"] = new[] { "cabdate", "cab", "changeadvisoryboarddate", "cabapprovaldate" },
        ["deployment_planned_end_date"] = new[] { "deploymentplannedenddate", "deploymentplanned", "deploydate", "deployment", "deploy", "golive" },
        ["closed_date"] = new[] { "closeddate", "closedon", "closuredate", "completeddate", "completiondate", "resolveddate" },
    };
}
