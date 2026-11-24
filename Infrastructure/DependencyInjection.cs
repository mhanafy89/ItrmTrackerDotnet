using ItrmTracker.Application.Abstractions;
using ItrmTracker.Application.Import;
using ItrmTracker.Application.Lookups;
using ItrmTracker.Application.Query;
using ItrmTracker.Application.Sla;
using ItrmTracker.Application.Tracker;
using ItrmTracker.Application.Users;
using ItrmTracker.Domain.Entities;
using ItrmTracker.Infrastructure.Data;
using ItrmTracker.Infrastructure.Identity;
using ItrmTracker.Infrastructure.Repositories;
using ItrmTracker.Infrastructure.Seeding;
using ItrmTracker.Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ItrmTracker.Infrastructure;

/// <summary>Registers persistence, identity, integrations and background services.</summary>
public static class DependencyInjection
{
    public const string ConnectionStringName = "ItrmDatabase";

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException($"Connection string '{ConnectionStringName}' is not configured (see Docs/CONFIGURATION.md).");

        services.AddDbContext<ItrmDbContext>(options => options.UseSqlServer(connectionString, sql =>
        {
            sql.EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(10), errorNumbersToAdd: null);
            sql.CommandTimeout(30);
            sql.MigrationsAssembly(typeof(ItrmDbContext).Assembly.FullName);
            sql.MigrationsHistoryTable("__EFMigrationsHistory", "dbo");
        }));

        services.Configure<SecurityOptions>(configuration.GetSection(SecurityOptions.SectionName));
        services.Configure<DatabaseOptions>(configuration.GetSection(DatabaseOptions.SectionName));

        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<IAuditLogger, AuditLogger>();

        services.AddScoped<ITrackerRepository, TrackerRepository>();
        services.AddScoped<ISlaRepository, SlaRepository>();
        services.AddScoped<ISlaConfigRepository, SlaConfigRepository>();
        services.AddScoped<ILookupRepository, LookupRepository>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IImportRepository, ImportRepository>();
        services.AddScoped<IQueryExecutor, SqlQueryExecutor>();
        services.AddScoped<DatabaseInitializer>();
        services.AddHostedService<SlaBackfillHostedService>();

        // ASP.NET Core Identity over the existing dbo.users table (no AspNet* tables).
        services.AddIdentityCore<AppUser>(options =>
            {
                // Same rule as the original app: at least 6 characters, no composition rules.
                options.Password.RequiredLength = 6;
                options.Password.RequireDigit = false;
                options.Password.RequireLowercase = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequiredUniqueChars = 1;
                options.User.AllowedUserNameCharacters = string.Empty; // any character (legacy usernames are unrestricted)
                options.User.RequireUniqueEmail = false;
                options.ClaimsIdentity.RoleClaimType = System.Security.Claims.ClaimTypes.Role;
            })
            .AddUserStore<AppUserStore>()
            .AddSignInManager<AppSignInManager>()
            .AddClaimsPrincipalFactory<AppClaimsPrincipalFactory>();
        services.AddScoped<IPasswordHasher<AppUser>, LegacyCompatiblePasswordHasher>();
        services.AddScoped<IPasswordService, PasswordService>();
        return services;
    }
}

