using ItrmTracker.Application.Import;
using ItrmTracker.Application.Lookups;
using ItrmTracker.Application.Query;
using ItrmTracker.Application.Reporting;
using ItrmTracker.Application.Sla;
using ItrmTracker.Application.Tracker;
using ItrmTracker.Application.Users;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ItrmTracker.Application;

/// <summary>Registers the application services (use cases).</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SlaOptions>(configuration.GetSection(SlaOptions.SectionName));
        services.Configure<QueryConsoleOptions>(configuration.GetSection(QueryConsoleOptions.SectionName));
        services.AddMemoryCache();

        services.AddScoped<ISlaConfigService, SlaConfigService>();
        services.AddScoped<ISlaService, SlaService>();
        services.AddScoped<ITrackerService, TrackerService>();
        services.AddScoped<ILookupService, LookupService>();
        services.AddScoped<IUserAdminService, UserAdminService>();
        services.AddScoped<IReportingService, ReportingService>();
        services.AddScoped<IQueryConsoleService, QueryConsoleService>();
        services.AddScoped<IImportService, ImportService>();
        return services;
    }
}
