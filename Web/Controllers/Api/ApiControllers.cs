using ItrmTracker.Application.Lookups;
using ItrmTracker.Application.Sla;
using ItrmTracker.Application.Tracker;
using ItrmTracker.Domain.Constants;
using ItrmTracker.Infrastructure.Data;
using ItrmTracker.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ItrmTracker.Web.Controllers.Api;

/// <summary>Liveness/readiness probe used by the UI's connection banner and by load balancers (legacy GET /healthz).</summary>
[ApiController]
[AllowAnonymous]
public sealed class HealthController : ControllerBase
{
    [HttpGet("/healthz")]
    public async Task<IActionResult> Get([FromServices] ItrmDbContext db, CancellationToken ct)
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync("SELECT 1", ct);
            return Ok(new { ok = true });
        }
        catch (Exception)
        {
            // Details are logged by EF Core; do not leak connection information to anonymous callers.
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { ok = false, error = "Database unavailable." });
        }
    }
}

/// <summary>
/// Read-only JSON API kept for integrations and reporting tools that used the original REST endpoints
/// (same URLs and snake_case-free camelCase payloads of the documented SLA API). Requires the auth cookie.
/// </summary>
[ApiController]
[Route("api")]
public sealed class TrackerApiController : ControllerBase
{
    private readonly ITrackerService _tracker;
    private readonly ILookupService _lookups;

    public TrackerApiController(ITrackerService tracker, ILookupService lookups)
    {
        _tracker = tracker;
        _lookups = lookups;
    }

    [HttpGet("auth/me")]
    public IActionResult Me() => Ok(new
    {
        user = new
        {
            id = int.TryParse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var id) ? id : 0,
            username = User.Identity?.Name,
            name = User.DisplayName(),
            role = User.CanWrite() ? AppRoles.ReadWrite : AppRoles.ReadOnly,
        },
    });

    [HttpGet("lookups")]
    public async Task<IActionResult> Lookups(CancellationToken ct) => Ok(await _lookups.GetAllAsync(ct));

    [HttpGet("business-requests")]
    public async Task<IActionResult> BusinessRequests(CancellationToken ct) => Ok(await _tracker.ListAllAsync(TrackerKind.BusinessRequest, ct));

    [HttpGet("business-requests/{id:int}")]
    public async Task<IActionResult> BusinessRequest(int id, CancellationToken ct) => Ok(await _tracker.GetAsync(TrackerKind.BusinessRequest, id, ct));

    [HttpGet("business-requests/{id:int}/history")]
    public async Task<IActionResult> BusinessRequestHistory(int id, CancellationToken ct) => Ok(await _tracker.GetHistoryAsync(TrackerKind.BusinessRequest, id, s => s, ct));

    [HttpGet("business-requests/{id:int}/comments")]
    public async Task<IActionResult> BusinessRequestComments(int id, CancellationToken ct) => Ok(await _tracker.ListCommentsAsync(TrackerKind.BusinessRequest, id, ct));

    [HttpGet("export")]
    public async Task<IActionResult> ExportRequests(CancellationToken ct) => Ok(await _tracker.ExportAsync(TrackerKind.BusinessRequest, new TrackerQuery { View = "all" }, ct));

    [HttpGet("incidents")]
    public async Task<IActionResult> Incidents(CancellationToken ct) => Ok(await _tracker.ListAllAsync(TrackerKind.Incident, ct));

    [HttpGet("incidents/{id:int}")]
    public async Task<IActionResult> Incident(int id, CancellationToken ct) => Ok(await _tracker.GetAsync(TrackerKind.Incident, id, ct));

    [HttpGet("incidents/{id:int}/history")]
    public async Task<IActionResult> IncidentHistory(int id, CancellationToken ct) => Ok(await _tracker.GetHistoryAsync(TrackerKind.Incident, id, s => s, ct));

    [HttpGet("incidents/{id:int}/comments")]
    public async Task<IActionResult> IncidentComments(int id, CancellationToken ct) => Ok(await _tracker.ListCommentsAsync(TrackerKind.Incident, id, ct));

    [HttpGet("incidents/export")]
    public async Task<IActionResult> ExportIncidents(CancellationToken ct) => Ok(await _tracker.ExportAsync(TrackerKind.Incident, new TrackerQuery { View = "all" }, ct));
}

/// <summary>Stage-driven SLA API (docs/SLA-ENGINE.md §4): same routes as the original, including the /api/business-requests aliases.</summary>
[ApiController]
[Route("api")]
public sealed class SlaApiController : ControllerBase
{
    private readonly ISlaService _sla;
    private readonly ISlaConfigService _config;
    private readonly Application.Abstractions.ICurrentUser _user;

    public SlaApiController(ISlaService sla, ISlaConfigService config, Application.Abstractions.ICurrentUser user)
    {
        _sla = sla;
        _config = config;
        _user = user;
    }

    [HttpGet("requests/{id:int}/sla")]
    [HttpGet("business-requests/{id:int}/sla")]
    public async Task<IActionResult> Get(int id, CancellationToken ct) => Ok(await _sla.GetRequestSlaAsync(id, ct));

    [HttpGet("requests/{id:int}/sla/history")]
    [HttpGet("business-requests/{id:int}/sla/history")]
    public async Task<IActionResult> History(int id, CancellationToken ct) => Ok(await _sla.GetHistoryAsync(id, ct));

    /// <summary>
    /// <c>{ stage, status, plannedEffort?, actualEffortDays?, note? }</c>. Read/write role and the antiforgery header
    /// (<c>X-CSRF-TOKEN</c>) are required; the response is the transition plus the refreshed SLA.
    /// </summary>
    [HttpPost("requests/{id:int}/stage-status")]
    [HttpPost("business-requests/{id:int}/stage-status")]
    [Authorize(Policy = Policies.ReadWrite)]
    public async Task<IActionResult> ChangeStatus(int id, [FromBody] StageStatusInput input, CancellationToken ct) =>
        Ok(await _sla.UpdateStageStatusAsync(id, input, _user.DisplayName, ct));

    [HttpGet("sla/dashboard")]
    public async Task<IActionResult> Dashboard(string? priority, string? stage, string? health, CancellationToken ct) =>
        Ok(await _sla.GetDashboardAsync(priority, stage, health, ct));

    [HttpGet("sla/config")]
    public async Task<IActionResult> Config(CancellationToken ct) => Ok(await _config.LoadAsync(ct));
}
