using System.Threading.RateLimiting;
using FluentValidation;
using FluentValidation.AspNetCore;
using ItrmTracker.Application;
using ItrmTracker.Application.Abstractions;
using ItrmTracker.Domain.Constants;
using ItrmTracker.Infrastructure;
using ItrmTracker.Infrastructure.Identity;
using ItrmTracker.Infrastructure.Seeding;
using ItrmTracker.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

// Runs as a Windows Service when started by the Service Control Manager (no effect otherwise).
builder.Host.UseWindowsService();

// ---------------------------------------------------------------- services
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();
builder.Services.AddSingleton<DisplayFormatter>();
builder.Services.AddApplication(builder.Configuration);
builder.Services.AddInfrastructure(builder.Configuration);

var security = builder.Configuration.GetSection(SecurityOptions.SectionName).Get<SecurityOptions>() ?? new SecurityOptions();
builder.Services
    .AddAuthentication(IdentityConstants.ApplicationScheme)
    .AddIdentityCookies(cookies => cookies.ApplicationCookie?.Configure(options =>
    {
        options.Cookie.Name = "itrm_auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = Enum.TryParse<CookieSecurePolicy>(security.CookieSecurePolicy, true, out var policy) ? policy : CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromDays(security.SessionDays);
        options.SlidingExpiration = false; // the legacy session was a fixed 7 days
        options.LoginPath = "/Account/Login";
        options.LogoutPath = "/Account/Logout";
        options.AccessDeniedPath = "/Account/AccessDenied";
        options.Events.OnValidatePrincipal = CookieValidation.ValidateAsync;
        options.Events.OnRedirectToLogin = ctx =>
        {
            if (ctx.Request.Path.StartsWithSegments("/api")) ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            else ctx.Response.Redirect(ctx.RedirectUri);
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = ctx =>
        {
            if (ctx.Request.Path.StartsWithSegments("/api")) ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            else ctx.Response.Redirect(ctx.RedirectUri);
            return Task.CompletedTask;
        };
    }));

builder.Services.AddAuthorization(options =>
{
    // Every page requires a signed-in user (same as the legacy /api/* gate); write operations need read_write.
    options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
    options.AddPolicy(Policies.ReadWrite, p => p.RequireAuthenticatedUser().RequireRole(AppRoles.ReadWrite));
});

builder.Services.AddControllersWithViews(options =>
{
    options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute());
    options.Filters.Add<ApiServiceExceptionFilter>();
});
builder.Services.AddFluentValidationAutoValidation();
builder.Services.AddFluentValidationClientsideAdapters();
builder.Services.AddValidatorsFromAssemblyContaining<Program>();
builder.Services.AddAntiforgery(o => o.HeaderName = "X-CSRF-TOKEN");

// Import uploads carried base64 payloads up to 30 MB in the original app.
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o => o.MultipartBodyLengthLimit = 30L * 1024 * 1024);

// Brute-force protection for the login form (not present in the original app).
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

builder.Services.AddResponseCompression(o => o.EnableForHttps = true);
builder.Services.Configure<ForwardedHeadersOptions>(o => o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);

var app = builder.Build();

// ---------------------------------------------------------------- database
using (var scope = app.Services.CreateScope())
{
    var initializer = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();
    try
    {
        await initializer.InitializeAsync();
    }
    catch (Exception ex)
    {
        // Same as the original: the app cannot run without its database.
        app.Logger.LogCritical(ex, "Could not initialize the SQL Server database.");
        throw;
    }
}

// ---------------------------------------------------------------- pipeline
app.UseForwardedHeaders();
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler(errorApp => errorApp.Run(GlobalExceptionHandler.HandleAsync));
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/Home/Error", "?code={0}");
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseResponseCompression();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "public,max-age=604800",
});
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(name: "default", pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();

/// <summary>Entry point marker (used for assembly scanning and integration tests).</summary>
public partial class Program { }
