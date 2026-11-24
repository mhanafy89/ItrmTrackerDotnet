using ItrmTracker.Application.Abstractions;
using ItrmTracker.Application.Sla;
using ItrmTracker.Application.Users;
using ItrmTracker.Domain.Constants;
using ItrmTracker.Domain.Entities;
using ItrmTracker.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ItrmTracker.Infrastructure.Seeding;

/// <summary>Options bound from the <c>Database</c> section.</summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>Apply pending EF Core migrations at startup (the legacy app ran schema.sql + migrations on every start).</summary>
    public bool ApplyMigrationsOnStartup { get; set; } = true;

    /// <summary>Seed the lookup tables (only tables that are empty) and the first administrator (only when dbo.users is empty).</summary>
    public bool SeedOnStartup { get; set; } = true;

    /// <summary>Username of the first administrator (legacy SEED_ADMIN_USERNAME).</summary>
    public string SeedAdminUsername { get; set; } = "admin";

    /// <summary>
    /// Password of the first administrator (legacy SEED_ADMIN_PASSWORD). Must be supplied through a secret store /
    /// environment variable outside Development; no admin is created when it is empty.
    /// </summary>
    public string? SeedAdminPassword { get; set; }
}

/// <summary>
/// Startup database initialization: applies migrations and seeds the same lookup values as the original Excel
/// "Lookups" sheet (db/seed.js), preserving the original spellings.
/// </summary>
public sealed class DatabaseInitializer
{
    /// <summary>Values of the original seed (kept verbatim; see the note in db/seed.js about legacy spellings).</summary>
    private static readonly Dictionary<string, string[]> Seed = new()
    {
        ["priorities"] = new[] { "Critical", "High", "Medium", "Low", "Very Low" },
        ["business_units"] = new[] { "Customer Experience", "Customer Care", "Collection", "Product Development Retail", "Product Development SME", "Digital Channels" },
        ["stages"] = new[] { "Draft", "Demand", "Development", "QA", "UAT", "CAB", "Deployment" },
        ["statuses"] = new[] { "Not Started", "On Hold", "In Progress", "Pending for Approval", "Completed", "Cancelled" },
        ["tech_owners"] = new[] { "Murad", "Sameenullah", "Noushad", "Raiyan" },
    };

    private readonly ItrmDbContext _db;
    private readonly IPasswordHasher<AppUser> _hasher;
    private readonly IClock _clock;
    private readonly DatabaseOptions _options;
    private readonly IHostEnvironment _env;
    private readonly ILogger<DatabaseInitializer> _logger;

    public DatabaseInitializer(ItrmDbContext db, IPasswordHasher<AppUser> hasher, IClock clock, IOptions<DatabaseOptions> options, IHostEnvironment env, ILogger<DatabaseInitializer> logger)
    {
        _db = db;
        _hasher = hasher;
        _clock = clock;
        _options = options.Value;
        _env = env;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_options.ApplyMigrationsOnStartup)
        {
            var pending = (await _db.Database.GetPendingMigrationsAsync(ct)).ToList();
            if (pending.Count > 0)
            {
                _logger.LogInformation("Applying {Count} database migration(s): {Migrations}", pending.Count, string.Join(", ", pending));
                await _db.Database.MigrateAsync(ct);
            }
        }
        if (_options.SeedOnStartup)
        {
            await SeedLookupsAsync(ct);
            await SeedAdminAsync(ct);
        }
    }

    private async Task SeedLookupsAsync(CancellationToken ct)
    {
        async Task SeedTable<T>(DbSet<T> set, string[] names, Func<string, int, T> make) where T : LookupEntity
        {
            if (await set.AnyAsync(ct)) return;
            set.AddRange(names.Select((n, i) => make(n, i)));
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation("Seeded {Count} values into {Table}", names.Length, typeof(T).Name);
        }

        await SeedTable(_db.Priorities, Seed["priorities"], (n, i) => new Priority { Name = n, SortOrder = i });
        await SeedTable(_db.BusinessUnits, Seed["business_units"], (n, _) => new BusinessUnit { Name = n });
        await SeedTable(_db.Stages, Seed["stages"], (n, i) => new Stage { Name = n, SortOrder = i });
        await SeedTable(_db.Statuses, Seed["statuses"], (n, i) => new Status { Name = n, SortOrder = i });
        await SeedTable(_db.TechOwners, Seed["tech_owners"], (n, _) => new TechOwner { Name = n });
    }

    /// <summary>Only runs while dbo.users is empty, so it never resets a password that was already changed.</summary>
    private async Task SeedAdminAsync(CancellationToken ct)
    {
        if (await _db.Users.AnyAsync(ct)) return;
        var password = _options.SeedAdminPassword;
        if (string.IsNullOrEmpty(password) && _env.IsDevelopment()) password = "admin123";
        if (string.IsNullOrEmpty(password))
        {
            _logger.LogWarning("No users exist and Database:SeedAdminPassword is not configured – no administrator was created.");
            return;
        }
        var now = _clock.UtcNow;
        var user = new AppUser { Username = _options.SeedAdminUsername, Name = "Administrator", Role = AppRoles.ReadWrite, IsActive = true, CreatedAt = now, UpdatedAt = now };
        user.PasswordHash = _hasher.HashPassword(user, password);
        _db.Users.Add(user);
        await _db.SaveChangesAsync(ct);
        _logger.LogWarning("Created default administrator {Username} – change the password after first login.", user.Username);
    }
}

/// <summary>
/// Background job: creates per-stage SLA tracking for requests that have none (and re-syncs requests whose tracking no
/// longer matches the pipeline) once at startup – the legacy SLA_BACKFILL_ON_START behaviour.
/// </summary>
public sealed class SlaBackfillHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IOptions<SlaOptions> _options;
    private readonly ILogger<SlaBackfillHostedService> _logger;

    public SlaBackfillHostedService(IServiceScopeFactory scopes, IOptions<SlaOptions> options, ILogger<SlaBackfillHostedService> logger)
    {
        _scopes = scopes;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Value.BackfillOnStartup) return;
        // Let the web host finish starting first.
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        try
        {
            using var scope = _scopes.CreateScope();
            var sla = scope.ServiceProvider.GetRequiredService<ISlaService>();
            var (total, created) = await sla.BackfillMissingAsync("sla-migration", stoppingToken);
            if (total > 0) _logger.LogInformation("SLA tracking created for {Created}/{Total} existing requests.", created, total);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SLA backfill failed.");
        }
    }
}
