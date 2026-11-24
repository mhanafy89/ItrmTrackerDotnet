using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ItrmTracker.Infrastructure.Migrations;

/// <summary>
/// Runs an embedded, idempotent T-SQL script (batches separated by GO lines) inside an EF Core migration.
/// The scripts are the original db/schema.sql and db/migrations/*.sql, so the EF migrations create exactly the
/// legacy schema on a new database and are harmless (no-ops) on the existing production database.
/// </summary>
internal static class SqlScript
{
    private static readonly Regex GoSeparator = new(@"^\s*GO\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline);

    public static void Run(MigrationBuilder migrationBuilder, string fileName)
    {
        foreach (var batch in Batches(Load(fileName)))
        {
            // suppressTransaction: false – EF wraps each migration in a transaction.
            migrationBuilder.Sql(batch);
        }
    }

    internal static IEnumerable<string> Batches(string script) =>
        GoSeparator.Split(script).Where(b => !string.IsNullOrWhiteSpace(b) && !IsOnlyComments(b));

    private static bool IsOnlyComments(string batch) =>
        batch.Split('\n').All(line => string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("--", StringComparison.Ordinal));

    internal static string Load(string fileName)
    {
        var assembly = typeof(SqlScript).Assembly;
        var resource = assembly.GetManifestResourceNames().SingleOrDefault(n => n.EndsWith("." + fileName, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Embedded migration script '{fileName}' not found.");
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
