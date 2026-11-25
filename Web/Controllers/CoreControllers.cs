using ItrmTracker.Application.Common;
using ItrmTracker.Application.Reporting;
using ItrmTracker.Application.Sla;
using ItrmTracker.Application.Users;
using ItrmTracker.Domain.Constants;
using ItrmTracker.Domain.Entities;
using ItrmTracker.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ItrmTracker.Web.Controllers;

/// <summary>Dashboard tab and error page.</summary>
public sealed class HomeController : Controller
{
    private readonly IReportingService _reporting;

    public HomeController(IReportingService reporting) => _reporting = reporting;

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct) => View(await _reporting.GetDashboardAsync(ct));

    [AllowAnonymous]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error(int? code, string? traceId) =>
        View(new ErrorViewModel { StatusCode = code ?? 500, TraceId = traceId ?? HttpContext.TraceIdentifier });
}

/// <summary>Follow-up (computed, shared) and My Follow-ups (personal bookmarks) tabs.</summary>
public sealed class FollowUpController : Controller
{
    private readonly IReportingService _reporting;

    public FollowUpController(IReportingService reporting) => _reporting = reporting;

    [HttpGet]
    public async Task<IActionResult> Index(bool includeIncidents = true, CancellationToken ct = default) =>
        View(new FollowUpViewModel { Data = await _reporting.GetFollowUpsAsync(includeIncidents, ct), IncludeIncidents = includeIncidents });

    [HttpGet]
    public async Task<IActionResult> Mine(CancellationToken ct) => View(await _reporting.GetMyFollowUpsAsync(ct));
}

/// <summary>Stage SLA dashboard and the per-stage actions of the request details page.</summary>
public sealed class StageSlaController : Controller
{
    private readonly ISlaService _sla;
    private readonly Application.Abstractions.ICurrentUser _user;

    public StageSlaController(ISlaService sla, Application.Abstractions.ICurrentUser user)
    {
        _sla = sla;
        _user = user;
    }

    [HttpGet]
    public async Task<IActionResult> Index(string? health, string? priority, string? stage, CancellationToken ct)
    {
        var data = await _sla.GetDashboardAsync(priority, stage, health, ct);
        return View(new StageSlaDashboardViewModel { Data = data, Health = health });
    }

    /// <summary>Start / Hold / Resume / Complete / Cancel / Reopen / Reinstate / Estimate a stage (read/write only).</summary>
    [HttpPost]
    [Authorize(Policy = Policies.ReadWrite)]
    public async Task<IActionResult> Change(int id, StageActionInputModel input, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            TempData["Error"] = string.Join(" ", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage));
        }
        else
        {
            try
            {
                var result = await _sla.UpdateStageStatusAsync(id, new StageStatusInput
                {
                    Stage = input.Stage, Status = input.Status, PlannedEffort = input.PlannedEffort, ActualEffortDays = input.ActualEffortDays, Note = input.Note,
                }, _user.DisplayName, ct);
                TempData["Success"] = result.Transition.Changed
                    ? $"{result.Transition.Stage}: {result.Transition.From} \u2192 {result.Transition.To}."
                    : $"{result.Transition.Stage}: no change.";
            }
            catch (ServiceException ex)
            {
                TempData["Error"] = string.Join(" ", ex.Errors);
            }
        }
        return Redirect(Url.Action("Details", "BusinessRequests", new { id }) + "#stage-sla");
    }
}

/// <summary>Sign in / out and self-service password change.</summary>
public sealed class AccountController : Controller
{
    private readonly SignInManager<AppUser> _signIn;
    private readonly IUserAdminService _users;
    private readonly ILogger<AccountController> _logger;

    public AccountController(SignInManager<AppUser> signIn, IUserAdminService users, ILogger<AccountController> logger)
    {
        _signIn = signIn;
        _users = users;
        _logger = logger;
    }

    [HttpGet]
    [AllowAnonymous]
    public IActionResult Login(string? returnUrl) =>
        User.Identity?.IsAuthenticated == true ? RedirectToAction("Index", "Home") : View(new LoginViewModel { ReturnUrl = returnUrl });

    [HttpPost]
    [AllowAnonymous]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> Login(LoginViewModel vm)
    {
        if (!ModelState.IsValid) return View(vm);
        var result = await _signIn.PasswordSignInAsync(vm.Username!.Trim(), vm.Password!, isPersistent: true, lockoutOnFailure: false);
        if (!result.Succeeded)
        {
            _logger.LogWarning("Failed login for {Username} from {Ip}", vm.Username, HttpContext.Connection.RemoteIpAddress);
            ModelState.AddModelError(string.Empty, "Invalid username or password.");
            vm.Password = null;
            return View(vm);
        }
        _logger.LogInformation("User {Username} signed in", vm.Username);
        return !string.IsNullOrEmpty(vm.ReturnUrl) && Url.IsLocalUrl(vm.ReturnUrl) ? LocalRedirect(vm.ReturnUrl) : RedirectToAction("Index", "Home");
    }

    [HttpPost]
    public async Task<IActionResult> Logout()
    {
        await _signIn.SignOutAsync();
        return RedirectToAction(nameof(Login));
    }

    [HttpGet]
    public IActionResult ChangePassword() => View(new ChangePasswordViewModel());

    /// <summary>Any signed-in user (read-only included) can change their own password.</summary>
    [HttpPost]
    public async Task<IActionResult> ChangePassword(ChangePasswordViewModel vm, CancellationToken ct)
    {
        if (!ModelState.IsValid) return View(vm);
        try
        {
            await _users.ChangeOwnPasswordAsync(vm.CurrentPassword, vm.NewPassword, ct);
            TempData["Success"] = "Your password was changed.";
            return RedirectToAction("Index", "Home");
        }
        catch (ServiceException ex)
        {
            foreach (var e in ex.Errors) ModelState.AddModelError(string.Empty, e);
            return View(new ChangePasswordViewModel());
        }
    }

    [HttpGet]
    [AllowAnonymous]
    public IActionResult AccessDenied() => View();
}

/// <summary>User administration (read/write accounts only – the legacy /api/auth/users routes required it for every method).</summary>
[Authorize(Policy = Policies.ReadWrite)]
public sealed class UsersController : Controller
{
    private readonly IUserAdminService _users;
    private readonly Application.Abstractions.ICurrentUser _current;

    public UsersController(IUserAdminService users, Application.Abstractions.ICurrentUser current)
    {
        _users = users;
        _current = current;
    }

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct) =>
        View(new UsersIndexViewModel { Users = await _users.ListAsync(ct), CurrentUserId = _current.UserId });

    [HttpGet]
    public IActionResult Create() => View(new UserCreateViewModel());

    [HttpPost]
    public async Task<IActionResult> Create(UserCreateViewModel vm, CancellationToken ct)
    {
        if (!ModelState.IsValid) return View(vm);
        try
        {
            await _users.CreateAsync(new CreateUserInput(vm.Username, vm.Password, vm.Name, vm.Role), ct);
            TempData["Success"] = $"User \"{vm.Username}\" was created.";
            return RedirectToAction(nameof(Index));
        }
        catch (ServiceException ex)
        {
            foreach (var e in ex.Errors) ModelState.AddModelError(string.Empty, e);
            vm.Password = null;
            return View(vm);
        }
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id, CancellationToken ct)
    {
        try
        {
            var u = await _users.GetAsync(id, ct);
            return View(new UserEditViewModel { Id = u.Id, Username = u.Username, Name = u.Name, Role = u.Role, IsActive = u.IsActive });
        }
        catch (ServiceException ex) when (ex.StatusCode == 404)
        {
            return NotFound();
        }
    }

    [HttpPost]
    public async Task<IActionResult> Edit(int id, UserEditViewModel vm, CancellationToken ct)
    {
        vm.Id = id;
        if (!ModelState.IsValid) return View(vm);
        try
        {
            await _users.UpdateAsync(id, new UpdateUserInput(vm.Name, vm.Role, vm.IsActive, vm.Password), ct);
            TempData["Success"] = "User was saved.";
            return RedirectToAction(nameof(Index));
        }
        catch (ServiceException ex)
        {
            foreach (var e in ex.Errors) ModelState.AddModelError(string.Empty, e);
            vm.Password = null;
            return View(vm);
        }
    }

    [HttpPost]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        try
        {
            await _users.DeleteAsync(id, ct);
            TempData["Success"] = "User was deleted.";
        }
        catch (ServiceException ex)
        {
            TempData["Error"] = ex.Message;
        }
        return RedirectToAction(nameof(Index));
    }
}
