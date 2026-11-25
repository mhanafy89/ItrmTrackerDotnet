using System.Text;
using ItrmTracker.Application.Common;
using ItrmTracker.Application.Export;
using ItrmTracker.Application.Lookups;
using ItrmTracker.Application.Sla;
using ItrmTracker.Application.Tracker;
using ItrmTracker.Domain.Constants;
using ItrmTracker.Web.Infrastructure;
using ItrmTracker.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ItrmTracker.Web.Controllers;

/// <summary>
/// Shared register / form / details / bulk-edit / export / comments / follow behaviour for business requests and
/// incidents (the original routes/businessRequests.js and routes/incidents.js were near-duplicates).
/// </summary>
public abstract class TrackerControllerBase : Controller
{
    private readonly ITrackerService _tracker;
    private readonly ILookupService _lookups;
    private readonly ISlaService _sla;
    private readonly DisplayFormatter _format;
    private readonly ILogger _logger;

    protected TrackerControllerBase(ITrackerService tracker, ILookupService lookups, ISlaService sla, DisplayFormatter format, ILogger logger)
    {
        _tracker = tracker;
        _lookups = lookups;
        _sla = sla;
        _format = format;
        _logger = logger;
    }

    protected abstract TrackerMeta Meta { get; }
    private TrackerKind Kind => Meta.Kind;

    private void AddErrors(ServiceException ex)
    {
        foreach (var error in ex.Errors) ModelState.AddModelError(string.Empty, error);
    }

    private IActionResult RedirectToLocal(string? returnUrl, string fallbackAction, object? routeValues = null) =>
        !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : RedirectToAction(fallbackAction, routeValues);

    // ------------------------------------------------------------------ register

    [HttpGet]
    public async Task<IActionResult> Index([FromQuery] TrackerQuery query, CancellationToken ct)
    {
        var vm = new TrackerIndexViewModel
        {
            Meta = Meta,
            Rows = await _tracker.ListAsync(Kind, query, ct),
            Query = query,
            Lookups = await _lookups.GetAllAsync(ct),
            Columns = TrackerColumns.Table(Kind),
            FollowedIds = await _tracker.FollowedIdsAsync(Kind, ct),
            BulkFields = _tracker.BulkFields(Kind),
            CanWrite = User.CanWrite(),
        };
        return View("~/Views/Tracker/Index.cshtml", vm);
    }

    // ------------------------------------------------------------------ details

    [HttpGet]
    public async Task<IActionResult> Details(int id, CancellationToken ct)
    {
        TrackerRecord record;
        try
        {
            record = await _tracker.GetAsync(Kind, id, ct);
        }
        catch (ServiceException ex) when (ex.StatusCode == 404)
        {
            return NotFound();
        }

        RequestSlaView? stageSla = null;
        SlaHistoryView? stageSlaHistory = null;
        string? stageSlaError = null;
        if (Kind == TrackerKind.BusinessRequest)
        {
            try
            {
                stageSla = await _sla.GetRequestSlaAsync(id, ct);
                stageSlaHistory = await _sla.GetHistoryAsync(id, ct);
            }
            catch (ServiceException ex)
            {
                stageSlaError = string.Join(" ", ex.Errors);
            }
        }

        var vm = new TrackerDetailsViewModel
        {
            Meta = Meta,
            Record = record,
            Comments = await _tracker.ListCommentsAsync(Kind, id, ct),
            History = await _tracker.GetHistoryAsync(Kind, id, _format.DateTimeString, ct),
            IsFollowed = (await _tracker.FollowedIdsAsync(Kind, ct)).Contains(id),
            CanWrite = User.CanWrite(),
            StageSla = stageSla,
            StageSlaHistory = stageSlaHistory,
            StageSlaError = stageSlaError,
        };
        return View("~/Views/Tracker/Details.cshtml", vm);
    }

    // ------------------------------------------------------------------ create / edit / delete

    private async Task<IActionResult> FormView(TrackerFormViewModel vm, CancellationToken ct)
    {
        vm.Kind = Kind;
        vm.Lookups = await _lookups.GetAllAsync(ct);
        if (vm.Id is { } id) vm.IsFollowed = (await _tracker.FollowedIdsAsync(Kind, ct)).Contains(id);
        ViewData["Meta"] = Meta;
        return View("~/Views/Tracker/Form.cshtml", vm);
    }

    [HttpGet]
    [Authorize(Policy = Policies.ReadWrite)]
    public Task<IActionResult> Create(CancellationToken ct) => FormView(new TrackerFormViewModel(), ct);

    [HttpPost]
    [Authorize(Policy = Policies.ReadWrite)]
    public async Task<IActionResult> Create(TrackerFormViewModel vm, CancellationToken ct)
    {
        vm.Id = null;
        if (!ModelState.IsValid) return await FormView(vm, ct);
        try
        {
            var created = await _tracker.CreateAsync(Kind, vm.ToInput(), ct);
            TempData["Success"] = $"{Meta.Singular} \"{created.Title}\" was created.";
            return RedirectToAction(nameof(Index));
        }
        catch (ServiceException ex)
        {
            AddErrors(ex);
            return await FormView(vm, ct);
        }
    }

    [HttpGet]
    [Authorize(Policy = Policies.ReadWrite)]
    public async Task<IActionResult> Edit(int id, CancellationToken ct)
    {
        try
        {
            return await FormView(TrackerFormViewModel.From(await _tracker.GetAsync(Kind, id, ct)), ct);
        }
        catch (ServiceException ex) when (ex.StatusCode == 404)
        {
            return NotFound();
        }
    }

    [HttpPost]
    [Authorize(Policy = Policies.ReadWrite)]
    public async Task<IActionResult> Edit(int id, TrackerFormViewModel vm, CancellationToken ct)
    {
        vm.Id = id;
        if (!ModelState.IsValid) return await FormView(vm, ct);
        try
        {
            await _tracker.UpdateAsync(Kind, id, vm.ToInput(), ct);
            TempData["Success"] = $"{Meta.Singular} was saved.";
            return RedirectToAction(nameof(Index));
        }
        catch (ServiceException ex) when (ex.StatusCode == 404)
        {
            return NotFound();
        }
        catch (ServiceException ex)
        {
            AddErrors(ex);
            return await FormView(vm, ct);
        }
    }

    [HttpPost]
    [Authorize(Policy = Policies.ReadWrite)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        try
        {
            await _tracker.DeleteAsync(Kind, id, ct);
            TempData["Success"] = $"{Meta.Singular} was deleted.";
        }
        catch (ServiceException ex)
        {
            TempData["Error"] = ex.Message;
        }
        return RedirectToAction(nameof(Index));
    }

    // ------------------------------------------------------------------ bulk edit, export, copy, e-mail

    [HttpPost]
    [Authorize(Policy = Policies.ReadWrite)]
    public async Task<IActionResult> BulkEdit(BulkEditInputModel input, CancellationToken ct)
    {
        var fields = input.Fields.Where(kv => !string.IsNullOrEmpty(kv.Value)).ToDictionary(kv => kv.Key, kv => kv.Value);
        try
        {
            var result = await _tracker.BulkUpdateAsync(Kind, input.Ids, fields, ct);
            if (result.Errors.Count == 0) TempData["Success"] = $"Updated {result.Updated} of {result.Total} record(s).";
            else TempData["Error"] = $"Updated {result.Updated} of {result.Total} record(s). {string.Join(" ", result.Errors)}";
        }
        catch (ServiceException ex)
        {
            TempData["Error"] = ex.Message;
        }
        return RedirectToLocal(input.ReturnUrl, nameof(Index));
    }

    private IReadOnlyList<TrackerColumn> DisplayedColumns(string? visibleKeys)
    {
        var keys = (visibleKeys ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
        var table = TrackerColumns.Table(Kind);
        return keys.Count == 0 ? table.Where(c => c.DefaultVisible || c.Required).ToList() : table.Where(c => keys.Contains(c.Key) || c.Required).ToList();
    }

    /// <summary>Excel export of the filtered register (Displayed Columns or All Columns).</summary>
    [HttpGet]
    public async Task<IActionResult> Export([FromQuery] TrackerQuery query, string mode = "displayed", string? cols = null, CancellationToken ct = default)
    {
        var rows = await _tracker.ExportAsync(Kind, query, ct);
        IReadOnlyList<TrackerColumn> columns = mode == "all"
            ? TrackerColumns.Export(Kind)
            : DisplayedColumns(cols).Select(c => c.Key == "last_comment" ? new TrackerColumn("all_comments", "Comments") : c).ToList();
        var bytes = XlsxWriter.WriteTracker(Meta.Plural, columns, rows);
        var prefix = Kind == TrackerKind.Incident ? "ITRM-Incidents" : "ITRM-Requests";
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"{prefix}-{DateTime.UtcNow:yyyy-MM-dd}.xlsx");
    }

    /// <summary>Tab-separated text for "Copy table" (Displayed Columns or All Columns), filtered and sorted like the register.</summary>
    [HttpGet]
    public async Task<IActionResult> CopyText([FromQuery] TrackerQuery query, string mode = "displayed", string? cols = null, CancellationToken ct = default)
    {
        var rows = await _tracker.ListAsync(Kind, query, ct);
        var columns = mode == "all" ? TrackerColumns.Export(Kind) : DisplayedColumns(cols);
        return Content(_format.Tsv(rows, columns), "text/plain", Encoding.UTF8);
    }

    /// <summary>Downloads an Outlook draft (.eml) listing the selected records. Nothing is sent by the server.</summary>
    [HttpPost]
    public async Task<IActionResult> EmailDraft(EmailDraftInputModel input, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            TempData["Error"] = string.Join(" ", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage));
            return RedirectToAction(nameof(Index));
        }
        var ids = input.Ids.ToHashSet();
        // Keep the register order of the selected rows (selectedRowsInTableOrder).
        var rows = (await _tracker.ListAllAsync(Kind, ct)).Where(r => ids.Contains(r.Id)).ToList();
        var columns = DisplayedColumns(input.Columns);
        var label = Kind == TrackerKind.Incident ? "incident" : "business request";
        var subject = string.IsNullOrWhiteSpace(input.Subject) ? $"ITRM Tracker - {rows.Count} {label}{(rows.Count != 1 ? "s" : "")}" : input.Subject.Trim();
        // The e-mail cells use the copy formatting (local date-times, Yes/No), like the original.
        var table = EmlBuilder.TableHtml(rows, columns, _format.CopyCell);
        var eml = EmlBuilder.Build(input.To, input.Cc, subject, input.Message, table);
        return File(Encoding.UTF8.GetBytes(eml), "message/rfc822", $"ITRM-draft-{DateTime.UtcNow:yyyy-MM-dd}.eml");
    }

    // ------------------------------------------------------------------ comments (adding is allowed for read-only users)

    [HttpPost]
    public async Task<IActionResult> Comment(int id, [Bind(Prefix = "NewComment")] CommentInputModel input, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            TempData["Error"] = string.Join(" ", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage));
            return RedirectToAction(nameof(Details), new { id });
        }
        try
        {
            await _tracker.AddCommentAsync(Kind, id, input.Body, ct);
        }
        catch (ServiceException ex) when (ex.StatusCode == 404)
        {
            return NotFound();
        }
        catch (ServiceException ex)
        {
            TempData["Error"] = ex.Message;
        }
        return Redirect(Url.Action(nameof(Details), new { id }) + "#comments");
    }

    [HttpPost]
    [Authorize(Policy = Policies.ReadWrite)]
    public async Task<IActionResult> DeleteComment(int id, int commentId, CancellationToken ct)
    {
        try
        {
            await _tracker.DeleteCommentAsync(Kind, id, commentId, ct);
        }
        catch (ServiceException ex)
        {
            TempData["Error"] = ex.Message;
        }
        return Redirect(Url.Action(nameof(Details), new { id }) + "#comments");
    }

    // ------------------------------------------------------------------ personal follow-ups (allowed for read-only users)

    [HttpPost]
    public async Task<IActionResult> Follow(int id, string? returnUrl, CancellationToken ct)
    {
        try
        {
            await _tracker.FollowAsync(Kind, id, ct);
        }
        catch (ServiceException ex)
        {
            TempData["Error"] = ex.Message;
        }
        return RedirectToLocal(returnUrl, nameof(Details), new { id });
    }

    [HttpPost]
    public async Task<IActionResult> Unfollow(int id, string? returnUrl, CancellationToken ct)
    {
        try
        {
            await _tracker.UnfollowAsync(Kind, id, ct);
        }
        catch (ServiceException ex)
        {
            TempData["Error"] = ex.Message;
        }
        return RedirectToLocal(returnUrl, nameof(Details), new { id });
    }
}

/// <summary>Business Requests tab.</summary>
public sealed class BusinessRequestsController : TrackerControllerBase
{
    public BusinessRequestsController(ITrackerService tracker, ILookupService lookups, ISlaService sla, DisplayFormatter format, ILogger<BusinessRequestsController> logger)
        : base(tracker, lookups, sla, format, logger) { }

    protected override TrackerMeta Meta => TrackerMeta.BusinessRequests;
}

/// <summary>Incidents tab.</summary>
public sealed class IncidentsController : TrackerControllerBase
{
    public IncidentsController(ITrackerService tracker, ILookupService lookups, ISlaService sla, DisplayFormatter format, ILogger<IncidentsController> logger)
        : base(tracker, lookups, sla, format, logger) { }

    protected override TrackerMeta Meta => TrackerMeta.Incidents;
}
