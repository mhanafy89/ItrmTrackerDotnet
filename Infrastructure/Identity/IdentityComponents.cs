using System.Security.Claims;
using ItrmTracker.Application.Users;
using ItrmTracker.Domain.Entities;
using ItrmTracker.Infrastructure.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ItrmTracker.Infrastructure.Identity;

/// <summary>Options bound from the <c>Security</c> configuration section.</summary>
public sealed class SecurityOptions
{
    public const string SectionName = "Security";

    /// <summary>
    /// <c>Identity</c> (default): new passwords use ASP.NET Core Identity's PBKDF2 (V3) hash.
    /// <c>LegacyScrypt</c>: new passwords use the Node.js scrypt format, so the legacy app can still verify them
    /// (use during a parallel run of both applications).
    /// </summary>
    public string PasswordHashFormat { get; set; } = "Identity";

    /// <summary>Re-hash legacy scrypt hashes to the Identity format on successful login (only with PasswordHashFormat=Identity).</summary>
    public bool UpgradeLegacyHashesOnLogin { get; set; } = true;

    /// <summary>Lifetime of the authentication cookie (the legacy session lasted 7 days).</summary>
    public int SessionDays { get; set; } = 7;

    /// <summary>Always | SameAsRequest (default, like the legacy COOKIE_SECURE=false) | None.</summary>
    public string CookieSecurePolicy { get; set; } = "SameAsRequest";

    public bool UseLegacyScrypt => string.Equals(PasswordHashFormat, "LegacyScrypt", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Password hasher that understands both formats stored in dbo.users: the legacy Node.js
/// <c>scryptSaltHex$scryptHashHex</c> and ASP.NET Core Identity V3 (PBKDF2-HMAC-SHA512, 100k iterations).
/// </summary>
public sealed class LegacyCompatiblePasswordHasher : IPasswordHasher<AppUser>
{
    private readonly PasswordHasher<AppUser> _identity = new(Options.Create(new PasswordHasherOptions { CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3 }));
    private readonly SecurityOptions _options;

    public LegacyCompatiblePasswordHasher(IOptions<SecurityOptions> options) => _options = options.Value;

    public string HashPassword(AppUser user, string password) =>
        _options.UseLegacyScrypt ? Scrypt.HashLegacy(password) : _identity.HashPassword(user, password);

    public PasswordVerificationResult VerifyHashedPassword(AppUser user, string hashedPassword, string providedPassword)
    {
        if (Scrypt.IsLegacyFormat(hashedPassword))
        {
            if (!Scrypt.VerifyLegacy(providedPassword, hashedPassword)) return PasswordVerificationResult.Failed;
            return !_options.UseLegacyScrypt && _options.UpgradeLegacyHashesOnLogin
                ? PasswordVerificationResult.SuccessRehashNeeded
                : PasswordVerificationResult.Success;
        }
        try
        {
            return _identity.VerifyHashedPassword(user, hashedPassword, providedPassword);
        }
        catch (FormatException)
        {
            return PasswordVerificationResult.Failed;
        }
    }
}

/// <summary><see cref="IPasswordService"/> on top of the Identity password hasher.</summary>
public sealed class PasswordService : IPasswordService
{
    private readonly IPasswordHasher<AppUser> _hasher;

    public PasswordService(IPasswordHasher<AppUser> hasher) => _hasher = hasher;

    public string Hash(AppUser user, string password) => _hasher.HashPassword(user, password);

    public bool Verify(AppUser user, string password) =>
        _hasher.VerifyHashedPassword(user, user.PasswordHash, password) != PasswordVerificationResult.Failed;
}

/// <summary>
/// ASP.NET Core Identity user store over the EXISTING dbo.users table (no AspNet* tables are introduced, so the
/// legacy and modernized applications can share accounts).
/// </summary>
public sealed class AppUserStore : IUserPasswordStore<AppUser>
{
    private readonly ItrmDbContext _db;

    public AppUserStore(ItrmDbContext db) => _db = db;

    public void Dispose() { }

    public Task<string> GetUserIdAsync(AppUser user, CancellationToken ct) => Task.FromResult(user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
    public Task<string?> GetUserNameAsync(AppUser user, CancellationToken ct) => Task.FromResult<string?>(user.Username);
    public Task SetUserNameAsync(AppUser user, string? userName, CancellationToken ct) { user.Username = userName ?? string.Empty; return Task.CompletedTask; }
    public Task<string?> GetNormalizedUserNameAsync(AppUser user, CancellationToken ct) => Task.FromResult<string?>(user.Username.ToUpperInvariant());
    public Task SetNormalizedUserNameAsync(AppUser user, string? normalizedName, CancellationToken ct) => Task.CompletedTask;

    public async Task<IdentityResult> CreateAsync(AppUser user, CancellationToken ct)
    {
        _db.Users.Add(user);
        await _db.SaveChangesAsync(ct);
        return IdentityResult.Success;
    }

    public async Task<IdentityResult> UpdateAsync(AppUser user, CancellationToken ct)
    {
        if (_db.Entry(user).State == EntityState.Detached) _db.Users.Update(user);
        user.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return IdentityResult.Success;
    }

    public async Task<IdentityResult> DeleteAsync(AppUser user, CancellationToken ct)
    {
        await _db.Users.Where(u => u.Id == user.Id).ExecuteDeleteAsync(ct);
        return IdentityResult.Success;
    }

    public Task<AppUser?> FindByIdAsync(string userId, CancellationToken ct) =>
        int.TryParse(userId, out var id) ? _db.Users.FirstOrDefaultAsync(u => u.Id == id, ct) : Task.FromResult<AppUser?>(null);

    /// <summary>Case-insensitive match on the username (the legacy lookup relied on the database's CI collation).</summary>
    public Task<AppUser?> FindByNameAsync(string normalizedUserName, CancellationToken ct) =>
        _db.Users.FirstOrDefaultAsync(u => u.Username.ToUpper() == normalizedUserName, ct);

    public Task SetPasswordHashAsync(AppUser user, string? passwordHash, CancellationToken ct) { user.PasswordHash = passwordHash ?? string.Empty; return Task.CompletedTask; }
    public Task<string?> GetPasswordHashAsync(AppUser user, CancellationToken ct) => Task.FromResult<string?>(user.PasswordHash);
    public Task<bool> HasPasswordAsync(AppUser user, CancellationToken ct) => Task.FromResult(!string.IsNullOrEmpty(user.PasswordHash));
}

/// <summary>Claim types issued for the signed-in user.</summary>
public static class AppClaimTypes
{
    public const string DisplayName = "itrm:display_name";
}

/// <summary>Adds the role (read_only / read_write) and the display name to the authentication cookie.</summary>
public sealed class AppClaimsPrincipalFactory : UserClaimsPrincipalFactory<AppUser>
{
    public AppClaimsPrincipalFactory(UserManager<AppUser> userManager, IOptions<IdentityOptions> options) : base(userManager, options) { }

    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(AppUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        identity.AddClaim(new Claim(Options.ClaimsIdentity.RoleClaimType, user.Role));
        identity.AddClaim(new Claim(AppClaimTypes.DisplayName, user.Name));
        return identity;
    }
}

/// <summary>Only active accounts may sign in (legacy: <c>WHERE is_active = 1</c>).</summary>
public sealed class AppSignInManager : SignInManager<AppUser>
{
    public AppSignInManager(UserManager<AppUser> userManager, IHttpContextAccessor contextAccessor, IUserClaimsPrincipalFactory<AppUser> claimsFactory,
        IOptions<IdentityOptions> optionsAccessor, ILogger<SignInManager<AppUser>> logger, IAuthenticationSchemeProvider schemes, IUserConfirmation<AppUser> confirmation)
        : base(userManager, contextAccessor, claimsFactory, optionsAccessor, logger, schemes, confirmation) { }

    public override async Task<bool> CanSignInAsync(AppUser user) => user.IsActive && await base.CanSignInAsync(user);
}
