using System.Data;
using System.Data.Common;
using System.Globalization;
using ItrmTracker.Application.Abstractions;
using ItrmTracker.Application.Query;
using ItrmTracker.Application.Sla;
using ItrmTracker.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ItrmTracker.Infrastructure.Services;

/// <summary>System clock; calendar days are evaluated in the configured business time zone.</summary>
public sealed class SystemClock : IClock
{
    public SystemClock(IOptions<SlaOptions> options, ILogger<SystemClock> logger)
    {
        var id = options.Value.BusinessTimeZoneId;
        BusinessTimeZone = TimeZoneInfo.Local;
        if (!string.IsNullOrWhiteSpace(id))
        {
            try
            {
                BusinessTimeZone = TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                logger.LogWarning("Unknown time zone {TimeZoneId}; falling back to the server's local time zone.", id);
            }
        }
    }

    public TimeZoneInfo BusinessTimeZone { get; }

    public DateTime UtcNow
    {
        get
        {
            var now = DateTime.UtcNow;
            return new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        }
    }

    public DateOnly Today => ToBusinessDate(UtcNow);

    public DateOnly ToBusinessDate(DateTime utc) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), BusinessTimeZone));
}

/// <summary>Structured audit log (category <c>ItrmTracker.Audit</c>, event id 9000) – route it to a durable sink in production.</summary>
public sealed class AuditLogger : IAuditLogger
{
    private static readonly EventId AuditEvent = new(9000, "Audit");
    private readonly ILogger _logger;
    private readonly ICurrentUser _user;

    public AuditLogger(ILoggerFactory factory, ICurrentUser user)
    {
        _logger = factory.CreateLogger("ItrmTracker.Audit");
        _user = user;
    }

    public void Log(string action, string entity, object? entityId, string? details = null) =>
        _logger.LogInformation(AuditEvent, "AUDIT {Action} {Entity} {EntityId} by {User} ({UserId}): {Details}",
            action, entity, entityId, _user.Username ?? "system", _user.UserId, details);
}

/// <summary>
/// Executes console queries with ADO.NET. Uses <c>ConnectionStrings:QueryConsole</c> when configured (recommended:
/// a login with db_datareader only), otherwise the application connection. Every statement runs inside a transaction
/// that is always rolled back (defence in depth on top of the keyword guard).
/// </summary>
public sealed class SqlQueryExecutor : IQueryExecutor
{
    private readonly ItrmDbContext _db;
    private readonly string? _readOnlyConnectionString;

    public SqlQueryExecutor(ItrmDbContext db, IConfiguration configuration)
    {
        _db = db;
        _readOnlyConnectionString = configuration.GetConnectionString("QueryConsole");
    }

    private async Task<(DbConnection Connection, bool Owned)> OpenAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_readOnlyConnectionString))
        {
            var own = new SqlConnection(_readOnlyConnectionString);
            await own.OpenAsync(ct);
            return (own, true);
        }
        var connection = _db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(ct);
        return (connection, false);
    }

    private static string? Format(DbDataReader reader, int i)
    {
        if (reader.IsDBNull(i)) return null;
        var value = reader.GetValue(i);
        var typeName = reader.GetDataTypeName(i).ToLowerInvariant();
        return value switch
        {
            DateTime d when typeName == "date" => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            DateTimeOffset o => o.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            bool b => b ? "1" : "0",
            byte[] bytes => "0x" + Convert.ToHexString(bytes),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };
    }

    public async Task<QueryResult> ExecuteAsync(string sql, int maxRows, int timeoutSeconds, CancellationToken ct = default)
    {
        var (connection, owned) = await OpenAsync(ct);
        try
        {
            await using var tx = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            try
            {
                await using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                cmd.CommandTimeout = timeoutSeconds;
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
                var rows = new List<IReadOnlyList<string?>>();
                var truncated = false;
                while (await reader.ReadAsync(ct))
                {
                    if (maxRows > 0 && rows.Count >= maxRows)
                    {
                        truncated = true;
                        break;
                    }
                    var row = new string?[reader.FieldCount];
                    for (var i = 0; i < reader.FieldCount; i++) row[i] = Format(reader, i);
                    rows.Add(row);
                }
                return new QueryResult(columns, rows, rows.Count, truncated);
            }
            finally
            {
                await tx.RollbackAsync(CancellationToken.None);
            }
        }
        finally
        {
            if (owned) await connection.DisposeAsync();
        }
    }

    public async Task<IReadOnlyList<SchemaTable>> GetSchemaAsync(CancellationToken ct = default)
    {
        var (connection, owned) = await OpenAsync(ct);
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
SELECT c.TABLE_NAME, c.COLUMN_NAME, c.DATA_TYPE, c.CHARACTER_MAXIMUM_LENGTH
  FROM INFORMATION_SCHEMA.COLUMNS c
  JOIN INFORMATION_SCHEMA.TABLES t ON t.TABLE_SCHEMA = c.TABLE_SCHEMA AND t.TABLE_NAME = c.TABLE_NAME
 WHERE t.TABLE_TYPE = 'BASE TABLE' AND c.TABLE_SCHEMA = 'dbo'
 ORDER BY c.TABLE_NAME, c.ORDINAL_POSITION";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var tables = new List<(string Table, List<SchemaColumn> Columns)>();
            while (await reader.ReadAsync(ct))
            {
                var table = reader.GetString(0);
                var size = reader.IsDBNull(3) ? string.Empty : $"({(reader.GetInt32(3) == -1 ? "max" : reader.GetInt32(3).ToString(CultureInfo.InvariantCulture))})";
                if (tables.Count == 0 || tables[^1].Table != table) tables.Add((table, new List<SchemaColumn>()));
                tables[^1].Columns.Add(new SchemaColumn(reader.GetString(1), reader.GetString(2) + size));
            }
            return tables.Select(t => new SchemaTable(t.Table, t.Columns)).ToList();
        }
        finally
        {
            if (owned) await connection.DisposeAsync();
        }
    }
}
