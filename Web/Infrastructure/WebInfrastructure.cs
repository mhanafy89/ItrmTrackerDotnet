using System.Security.Claims;
using ItrmTracker.Application.Abstractions;
using ItrmTracker.Application.Common;
using ItrmTracker.Domain.Constants;
using ItrmTracker.Infrastructure.Data;
using ItrmTracker.Infrastructure.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;

namespace ItrmTracker.Web.Infrastructure;

/// <summary>The signed-in user, read from the authentication cookie claims.</summary>
public sealed class HttpCurrentUser : ICurrentUser
{
    private readonly IHttpContextAccessor _accessor;

    public HttpCurrentUser(IHttpContextAccessor accessor) => _accessor = accessor;

    private ClaimsPrincipal? Principal => _accessor.HttpContext?.User;

    public int? UserId => int.TryParse(Principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
    public string? Username => Principal?.Identity?.IsAuthenticated == true ? Principal.Identity.Name : null;
    public string? DisplayName => Principal?.FindFirstValue(AppClaimTypes.DisplayName) ?? Username;
    public bool CanWrite => Principal?.IsInRole(AppRoles.ReadWrite) == true;
}

/// <summary>Helpers for views and controllers.</summary>
public static class PrincipalExtensions
{
    public static bool CanWrite(this ClaimsPrincipal user) => user.IsInRole(AppRoles.ReadWrite);
    public static string DisplayName(this ClaimsPrincipal user) => user.FindFirstValue(AppClaimTypes.DisplayName) ?? user.Identity?.Name ?? string.Empty;
}

/// <summary>
/// Re-validates the authentication cookie on every request, like the legacy server-side session lookup:
/// deleted or deactivated accounts are signed out immediately, and role / name changes take effect at once.
/// </summary>
public static class CookieValidation
{
    public static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var principal = context.Principal;
        if (principal is null || !int.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id))
        {
            context.RejectPrincipal();
            return;
        }
        var db = context.HttpContext.RequestServices.GetRequiredService<ItrmDbContext>();
        var user = await db.Users.AsNoTracking().Where(u => u.Id == id).Select(u => new { u.IsActive, u.Role, u.Name }).FirstOrDefaultAsync();
        if (user is null || !user.IsActive)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
            return;
        }
        if (!principal.IsInRole(user.Role) || principal.FindFirstValue(AppClaimTypes.DisplayName) != user.Name)
        {
            var identity = new ClaimsIdentity(principal.Claims.Where(c => c.Type != ClaimTypes.Role && c.Type != AppClaimTypes.DisplayName),
                principal.Identity?.AuthenticationType, ClaimTypes.Name, ClaimTypes.Role);
            identity.AddClaim(new Claim(ClaimTypes.Role, user.Role));
            identity.AddClaim(new Claim(AppClaimTypes.DisplayName, user.Name));
            context.ReplacePrincipal(new ClaimsPrincipal(identity));
            context.ShouldRenew = true;
        }
    }
}

/// <summary>Adds conservative security headers (XSS / clickjacking / MIME sniffing protection).</summary>
public sealed class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;

    public SecurityHeadersMiddleware(RequestDelegate next) => _next = next;

    public Task InvokeAsync(HttpContext context)
    {
        var h = context.Response.Headers;
        h["X-Content-Type-Options"] = "nosniff";
        h["X-Frame-Options"] = "DENY";
        h["Referrer-Policy"] = "same-origin";
        h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        h["Content-Security-Policy"] =
            "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; " +
            "connect-src 'self'; frame-ancestors 'none'; form-action 'self'; base-uri 'self'; object-src 'none'";
        return _next(context);
    }
}

/// <summary>
/// Maps <see cref="ServiceException"/> thrown by API actions to <c>{ errors: [...] }</c> with the matching status code
/// (the error contract of the original REST API). MVC actions handle their own validation messages.
/// </summary>
public sealed class ApiServiceExceptionFilter : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        if (context.Exception is not ServiceException ex) return;
        if (!context.HttpContext.Request.Path.StartsWithSegments("/api")) return;
        context.Result = new ObjectResult(new { errors = ex.Errors, error = ex.Message }) { StatusCode = ex.StatusCode };
        context.ExceptionHandled = true;
    }
}

/// <summary>Global exception handler: logs with a correlation id and returns a friendly page or JSON problem.</summary>
public static class GlobalExceptionHandler
{
    public static async Task HandleAsync(HttpContext context)
    {
        var feature = context.Features.Get<IExceptionHandlerPathFeature>();
        var ex = feature?.Error;
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("ItrmTracker.Errors");
        var traceId = context.TraceIdentifier;

        if (ex is ServiceException se)
        {
            context.Response.StatusCode = se.StatusCode;
            logger.LogWarning("Business rule failure {StatusCode} on {Path}: {Errors} (trace {TraceId})", se.StatusCode, feature?.Path, string.Join(" | ", se.Errors), traceId);
        }
        else
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            logger.LogError(ex, "Unhandled exception on {Path} (trace {TraceId})", feature?.Path, traceId);
        }

        if (feature?.Path.StartsWith("/api", StringComparison.OrdinalIgnoreCase) == true || context.Request.Headers.Accept.ToString().Contains("application/json"))
        {
            var message = ex is ServiceException s ? s.Message : "Internal server error.";
            await context.Response.WriteAsJsonAsync(new { error = message, traceId });
            return;
        }
        context.Response.Redirect($"/Home/Error?code={context.Response.StatusCode}&traceId={Uri.EscapeDataString(traceId)}");
    }
}

