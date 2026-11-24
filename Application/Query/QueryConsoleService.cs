using System.Text.RegularExpressions;
using ItrmTracker.Application.Abstractions;
using ItrmTracker.Application.Common;
using Microsoft.Extensions.Options;

namespace ItrmTracker.Application.Query;

/// <summary>Options bound from the <c>QueryConsole</c> section.</summary>
public sealed class QueryConsoleOptions
{
    public const string SectionName = "QueryConsole";

    /// <summary>
    /// Tables that may not be referenced (security hardening – see the risks document). The original app allowed
    /// SELECTs on every table, including password hashes in dbo.users.
    /// </summary>
    public List<string> BlockedTables { get; set; } = new() { "users", "sessions" };

    /// <summary>Maximum rows returned (0 = unlimited, the original behaviour).</summary>
    public int MaxRows { get; set; } = 10000;

    public int CommandTimeoutSeconds { get; set; } = 30;
}

public sealed record QueryResult(IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string?>> Rows, int RowCount, bool Truncated);

public sealed record SchemaColumn(string Name, string Type);

public sealed record SchemaTable(string Name, IReadOnlyList<SchemaColumn> Columns);

/// <summary>Executes read-only statements (implemented with ADO.NET in the infrastructure layer).</summary>
public interface IQueryExecutor
{
    Task<QueryResult> ExecuteAsync(string sql, int maxRows, int timeoutSeconds, CancellationToken ct = default);
    Task<IReadOnlyList<SchemaTable>> GetSchemaAsync(CancellationToken ct = default);
}

public interface IQueryConsoleService
{
    Task<QueryResult> RunAsync(string? sql, CancellationToken ct = default);
    Task<IReadOnlyList<SchemaTable>> GetSchemaAsync(CancellationToken ct = default);
}

/// <summary>Read-only SQL console (port of routes/query.js) with an additional sensitive-table guard.</summary>
public sealed class QueryConsoleService : IQueryConsoleService
{
    /// <summary>Keywords that must never appear (anything that writes, executes code or reaches outside the database).</summary>
    /// <remarks>ECMAScript mode gives JavaScript's ASCII-only <c>\b</c> semantics, matching the original regex exactly.</remarks>
    private static readonly Regex Forbidden = new(
        @"\b(insert|update|delete|merge|drop|alter|create|truncate|exec|execute|into|grant|revoke|deny|backup|restore|shutdown|kill|waitfor|openrowset|opendatasource|openquery|openjson|bulk|dbcc|use|declare|set)\b|\bxp_|\bsp_",
        RegexOptions.IgnoreCase | RegexOptions.ECMAScript | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private static readonly Regex StartsWithSelect = new(@"^(select|with)\b", RegexOptions.IgnoreCase | RegexOptions.ECMAScript | RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex StringLiteral = new(@"'(?:[^']|'')*'", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex QuotedIdentifier = new(@"\[[^\]]*\]|""[^""]*""", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex TrailingSemicolon = new(@";\s*$", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private readonly IQueryExecutor _executor;
    private readonly QueryConsoleOptions _options;
    private readonly IAuditLogger _audit;

    public QueryConsoleService(IQueryExecutor executor, IOptions<QueryConsoleOptions> options, IAuditLogger audit)
    {
        _executor = executor;
        _options = options.Value;
        _audit = audit;
    }

    /// <summary>Returns an error message, or null when the statement is acceptable.</summary>
    public static string? CheckReadOnly(string sql, IEnumerable<string>? blockedTables = null)
    {
        if (!StartsWithSelect.IsMatch(sql)) return "Only SELECT queries are allowed.";
        // Drop string literals so their contents can't trigger (or hide) anything.
        var withoutStrings = StringLiteral.Replace(sql, "''");
        var code = QuotedIdentifier.Replace(withoutStrings, "[]");
        if (code.Contains("--") || code.Contains("/*")) return "Comments are not allowed in queries.";
        if (TrailingSemicolon.Replace(code, string.Empty).Contains(';')) return "Multiple statements are not allowed.";
        if (Forbidden.IsMatch(code)) return "Only read-only SELECT queries are allowed.";

        foreach (var table in blockedTables ?? Enumerable.Empty<string>())
        {
            // Quoted identifiers are matched too ([users], "users", dbo.[users]).
            var pattern = $@"(^|[^\w])(\[|""|)(dbo(\]|""|)\.(\[|""|))?{Regex.Escape(table)}(\]|""|)(?![\w])";
            if (Regex.IsMatch(withoutStrings, pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)))
            {
                return $"Queries on the \"{table}\" table are not allowed.";
            }
        }
        return null;
    }

    public async Task<QueryResult> RunAsync(string? sql, CancellationToken ct = default)
    {
        var text = (sql ?? string.Empty).Trim();
        if (text.Length == 0) throw ServiceException.BadRequest("No query provided.");
        var problem = CheckReadOnly(text, _options.BlockedTables);
        if (problem is not null) throw ServiceException.BadRequest(problem);

        _audit.Log("query.run", "query_console", null, text.Length > 500 ? text[..500] : text);
        try
        {
            return await _executor.ExecuteAsync(TrailingSemicolon.Replace(text, string.Empty), _options.MaxRows, _options.CommandTimeoutSeconds, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ServiceException)
        {
            // Same as the original: SQL errors are reported back to the user as a 400.
            throw ServiceException.BadRequest(ex.Message);
        }
    }

    public Task<IReadOnlyList<SchemaTable>> GetSchemaAsync(CancellationToken ct = default) => _executor.GetSchemaAsync(ct);
}
