using System.Globalization;
using System.Text;
using ItrmTracker.Application.Abstractions;
using ItrmTracker.Application.Common;
using ItrmTracker.Application.Import;
using ItrmTracker.Application.Lookups;
using ItrmTracker.Application.Query;
using ItrmTracker.Application.Sla;
using ItrmTracker.Domain.Constants;
using ItrmTracker.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace ItrmTracker.Web.Controllers;

/// <summary>Manage Lookups (read/write only).</summary>
[Authorize(Policy = Policies.ReadWrite)]
public sealed class LookupsController : Controller
{
    private readonly ILookupService _lookups;

    public LookupsController(ILookupService lookups) => _lookups = lookups;

    [HttpGet]
    public async Task<IActionResult> Index(string? type, CancellationToken ct)
    {
        var active = LookupTypes.Find(type) ?? LookupTypes.All[0];
        var all = await _lookups.GetAllAsync(ct);
        return View(new LookupsViewModel { Types = LookupTypes.All, Active = active, Items = all.Of(active.Key) });
    }

    [HttpPost]
    public async Task<IActionResult> Create(LookupNameInputModel input, CancellationToken ct)
    {
        await Run(() => _lookups.CreateAsync(input.Type ?? string.Empty, input.Name, ct), $"\"{input.Name?.Trim()}\" was added.");
        return RedirectToAction(nameof(Index), new { type = input.Type });
    }

    [HttpPost]
    public async Task<IActionResult> Rename(LookupNameInputModel input, CancellationToken ct)
    {
        await Run(() => _lookups.RenameAsync(input.Type ?? string.Empty, input.Id, input.Name, ct), "Value was renamed.");
        return RedirectToAction(nameof(Index), new { type = input.Type });
    }

    [HttpPost]
    public async Task<IActionResult> Delete(string type, int id, CancellationToken ct)
    {
        await Run(async () => { await _lookups.DeleteAsync(type, id, ct); return true; }, "Value was deleted.");
        return RedirectToAction(nameof(Index), new { type });
    }

    /// <summary>The "+" button next to lookup fields on the request form (JSON; antiforgery header required).</summary>
    [HttpPost]
    public async Task<IActionResult> QuickAdd([FromBody] LookupNameInputModel input, CancellationToken ct)
    {
        try
        {
            var item = await _lookups.CreateAsync(input.Type ?? string.Empty, input.Name, ct);
            return Json(new { id = item.Id, name = item.Name });
        }
        catch (ServiceException ex)
        {
            return StatusCode(ex.StatusCode, new { error = ex.Message });
        }
    }

    private async Task Run<T>(Func<Task<T>> action, string success)
    {
        if (!ModelState.IsValid)
        {
            TempData["Error"] = string.Join(" ", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage));
            return;
        }
        try
        {
            await action();
            TempData["Success"] = success;
        }
        catch (ServiceException ex)
        {
            TempData["Error"] = ex.Message;
        }
    }
}

/// <summary>SLA Settings: assumptions per stage / priority / category / status, weekend and engine settings.</summary>
[Authorize(Policy = Policies.ReadWrite)]
public sealed class SlaSettingsController : Controller
{
    private readonly ISlaConfigService _config;

    public SlaSettingsController(ISlaConfigService config) => _config = config;

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var current = await _config.LoadAsync(ct);
        return View(new SlaSettingsViewModel { Current = current, Form = SlaSettingsViewModel.ToInput(current) });
    }

    [HttpPost]
    public async Task<IActionResult> Index([Bind(Prefix = "Form")] SlaConfigInput form, CancellationToken ct)
    {
        try
        {
            await _config.SaveAsync(form, ct);
            TempData["Success"] = "SLA settings were saved.";
            return RedirectToAction(nameof(Index));
        }
        catch (ServiceException ex)
        {
            return View(new SlaSettingsViewModel { Current = await _config.LoadAsync(ct), Form = form, Errors = ex.Errors });
        }
    }

    [HttpPost]
    public async Task<IActionResult> Reset(CancellationToken ct)
    {
        await _config.ResetAsync(ct);
        TempData["Success"] = "SLA settings were reset to the built-in defaults.";
        return RedirectToAction(nameof(Index));
    }
}

/// <summary>
/// Import &amp; Merge wizard. Parsing, mapping and analysis are read-only (allowed for every user, as in the original);
/// the merge itself needs read/write access. Wizard state is kept server-side (30 minutes) instead of being
/// round-tripped through the browser.
/// </summary>
public sealed class ImportController : Controller
{
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(30);
    private const long MaxUploadBytes = 30L * 1024 * 1024;

    private readonly IImportService _import;
    private readonly IMemoryCache _cache;
    private readonly ICurrentUser _user;

    public ImportController(IImportService import, IMemoryCache cache, ICurrentUser user)
    {
        _import = import;
        _cache = cache;
        _user = user;
    }

    private string Key(string token) => $"itrm:import:{_user.UserId}:{token}";

    private ImportSession? Load(string? token) =>
        token is not null && _cache.TryGetValue(Key(token), out ImportSession? s) && s is not null && s.UserId == _user.UserId ? s : null;

    private void Save(ImportSession s) => _cache.Set(Key(s.Token), s, new MemoryCacheEntryOptions { SlidingExpiration = SessionLifetime, Size = null });

    private IActionResult Expired()
    {
        TempData["Error"] = "The import session expired or was not found. Please upload the file again.";
        return RedirectToAction(nameof(Index));
    }

    private IActionResult Step(string view, ImportSession s, int step) =>
        View(view, new ImportStepViewModel { Session = s, Target = ImportDefinitions.Targets[s.Target], Step = step });

    [HttpGet]
    public IActionResult Index() => View(new ImportUploadViewModel());

    /// <summary>Step 1 – Upload (.xlsx / .csv / .txt).</summary>
    [HttpPost]
    [RequestSizeLimit(MaxUploadBytes)]
    public async Task<IActionResult> Upload(string target, IFormFile? file, CancellationToken ct)
    {
        if (!ImportDefinitions.Targets.ContainsKey(target ?? string.Empty))
        {
            TempData["Error"] = "Unknown import target.";
            return RedirectToAction(nameof(Index));
        }
        if (file is null || file.Length == 0)
        {
            TempData["Error"] = "No file content received.";
            return RedirectToAction(nameof(Index));
        }
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        try
        {
            var parsed = _import.Parse(Path.GetFileName(file.FileName), ms.ToArray(), target);
            var session = new ImportSession
            {
                Token = Guid.NewGuid().ToString("N"),
                UserId = _user.UserId ?? 0,
                Target = target!,
                File = parsed,
                Mapping = parsed.SuggestedMapping ?? new Dictionary<string, string?>(),
            };
            Save(session);
            return RedirectToAction(nameof(Map), new { token = session.Token });
        }
        catch (ServiceException ex)
        {
            TempData["Error"] = ex.Message;
            return RedirectToAction(nameof(Index));
        }
    }

    /// <summary>Steps 2–3 – Auto-detect and map columns.</summary>
    [HttpGet]
    public IActionResult Map(string token)
    {
        var s = Load(token);
        return s is null ? Expired() : Step("Map", s, 3);
    }

    [HttpPost]
    public IActionResult Map(string token, Dictionary<string, string?> mapping)
    {
        var s = Load(token);
        if (s is null) return Expired();
        var def = ImportDefinitions.Targets[s.Target];
        s.Mapping = def.Fields.ToDictionary(f => f.Field, f => mapping.TryGetValue(f.Field, out var h) && !string.IsNullOrEmpty(h) && s.File.Headers.Contains(h) ? h : null);
        s.Analysis = null;
        Save(s);
        if (s.Mapping.Values.All(v => v is null))
        {
            TempData["Error"] = "Map at least one column.";
            return RedirectToAction(nameof(Map), new { token });
        }
        return RedirectToAction(nameof(Preview), new { token });
    }

    /// <summary>Step 4 – Preview the first 100 mapped rows.</summary>
    [HttpGet]
    public IActionResult Preview(string token)
    {
        var s = Load(token);
        return s is null ? Expired() : Step("Preview", s, 4);
    }

    /// <summary>Step 5 – Compare with the database.</summary>
    [HttpPost]
    public async Task<IActionResult> Analyze(string token, CancellationToken ct)
    {
        var s = Load(token);
        if (s is null) return Expired();
        try
        {
            s.Analysis = await _import.AnalyzeAsync(s.Target, s.Mapping, s.File.Rows, ct);
            Save(s);
            return RedirectToAction(nameof(Review), new { token });
        }
        catch (ServiceException ex)
        {
            TempData["Error"] = ex.Message;
            return RedirectToAction(nameof(Preview), new { token });
        }
    }

    /// <summary>Steps 5–7 – Compare summary, field differences and per-row decisions.</summary>
    [HttpGet]
    public IActionResult Review(string token)
    {
        var s = Load(token);
        if (s is null) return Expired();
        return s.Analysis is null ? RedirectToAction(nameof(Preview), new { token }) : Step("Review", s, 7);
    }

    /// <summary>Step 8 – Merge (read/write only). Everything is re-validated server-side.</summary>
    [HttpPost]
    [Authorize(Policy = Policies.ReadWrite)]
    public async Task<IActionResult> Commit(string token, Dictionary<int, string> decisions, CancellationToken ct)
    {
        var s = Load(token);
        if (s is null) return Expired();
        var allowed = new HashSet<string> { "insert", "update", "skip" };
        var clean = decisions.Where(d => allowed.Contains(d.Value)).ToDictionary(d => d.Key, d => d.Value);
        try
        {
            s.Result = await _import.CommitAsync(s.Target, s.File.Filename, s.File.FileType, s.Mapping, s.File.Rows, clean, ct);
            Save(s);
            return RedirectToAction(nameof(Summary), new { token });
        }
        catch (ServiceException ex)
        {
            TempData["Error"] = ex.Message;
            return RedirectToAction(nameof(Review), new { token });
        }
    }

    /// <summary>Step 9 – Summary.</summary>
    [HttpGet]
    public IActionResult Summary(string token)
    {
        var s = Load(token);
        if (s is null) return Expired();
        return s.Result is null ? RedirectToAction(nameof(Review), new { token }) : Step("Summary", s, 9);
    }

    [HttpGet]
    public async Task<IActionResult> History(CancellationToken ct) => View(new ImportHistoryViewModel { Batches = await _import.HistoryAsync(ct) });

    [HttpGet]
    public async Task<IActionResult> HistoryDetail(int id, CancellationToken ct)
    {
        try
        {
            var (batch, rows) = await _import.HistoryDetailAsync(id, ct);
            return View(new ImportHistoryDetailViewModel { Batch = batch, Rows = rows });
        }
        catch (ServiceException ex) when (ex.StatusCode == 404)
        {
            return NotFound();
        }
    }
}

/// <summary>Read-only SQL query console (any signed-in user, as in the original).</summary>
public sealed class QueryController : Controller
{
    private readonly IQueryConsoleService _console;

    public QueryController(IQueryConsoleService console) => _console = console;

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct) => View(new QueryViewModel { Schema = await _console.GetSchemaAsync(ct) });

    [HttpPost]
    public async Task<IActionResult> Index(QueryViewModel vm, CancellationToken ct)
    {
        try
        {
            vm.Result = await _console.RunAsync(vm.Sql, ct);
        }
        catch (ServiceException ex)
        {
            vm.Error = ex.Message;
        }
        vm.Schema = await _console.GetSchemaAsync(ct);
        return View(vm);
    }

    /// <summary>Downloads the result of a query as CSV.</summary>
    [HttpPost]
    public async Task<IActionResult> Export(string? sql, CancellationToken ct)
    {
        try
        {
            var result = await _console.RunAsync(sql, ct);
            static string Esc(string? v) => v is null ? string.Empty : v.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(",", result.Columns.Select(Esc)));
            foreach (var row in result.Rows) sb.AppendLine(string.Join(",", row.Select(Esc)));
            return File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray(), "text/csv",
                $"ITRM-query-{DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.csv");
        }
        catch (ServiceException ex)
        {
            TempData["Error"] = ex.Message;
            return RedirectToAction(nameof(Index));
        }
    }
}
