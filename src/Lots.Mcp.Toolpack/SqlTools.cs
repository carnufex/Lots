using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using Npgsql;

namespace Lots.Mcp.Toolpack;

/// <summary>
/// Read-only SQL against PostgreSQL. Three layers: connect with a read-only database role (your job; documented), every query runs in a
/// READ ONLY transaction with a statement timeout, and only a single SELECT / WITH / EXPLAIN / SHOW / TABLE statement is accepted.
/// </summary>
public static partial class SqlGuard
{
    public static string? Reject(string sql)
    {
        var s = Comments().Replace(sql, " ").Trim().TrimEnd(';').Trim();
        if (s.Length == 0) return "empty query";
        if (StripStrings(s).Contains(';')) return "only one statement is allowed";
        if (!Leading().IsMatch(s)) return "only SELECT, WITH, EXPLAIN, SHOW or TABLE queries are allowed";
        if (Writes().IsMatch(StripStrings(s))) return "the query contains a data-changing keyword";
        return null;
    }

    private static string StripStrings(string s) => Strings().Replace(s, "''");

    [GeneratedRegex(@"--[^\n]*|/\*.*?\*/", RegexOptions.Singleline)] private static partial Regex Comments();
    [GeneratedRegex(@"'(?:[^']|'')*'|""(?:[^""]|"""")*""")] private static partial Regex Strings();
    [GeneratedRegex(@"^(select|with|explain|show|table)\b", RegexOptions.IgnoreCase)] private static partial Regex Leading();
    [GeneratedRegex(@"\b(insert|update|delete|merge|drop|alter|create|truncate|grant|revoke|copy|call|do|vacuum|reindex|cluster|lock|refresh|set\s+role|reset|discard|listen|notify|prepare|execute)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Writes();
}

[McpServerToolType]
public sealed class SqlTools(NpgsqlDataSource data, IConfiguration config)
{
    private readonly int _timeoutSeconds = config.GetValue("Sql:StatementTimeoutSeconds", 10);

    [McpServerTool(Name = "sql_query", ReadOnly = true, Destructive = false),
     Description("Runs one read-only SQL query (PostgreSQL) and returns rows as JSON. Use sql_schema to see tables and columns.")]
    public async Task<string> Query(
        [Description("A single SELECT/WITH/EXPLAIN/SHOW statement")] string sql,
        [Description("Maximum rows (default 100, at most 1000)")] int maxRows = 100,
        CancellationToken ct = default)
    {
        if (SqlGuard.Reject(sql) is { } why) return "Error: " + why + ".";
        maxRows = Math.Clamp(maxRows, 1, 1000);
        try
        {
            await using var conn = await data.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await using (var set = new NpgsqlCommand($"SET TRANSACTION READ ONLY; SET LOCAL statement_timeout = {_timeoutSeconds * 1000}", conn, tx))
                await set.ExecuteNonQueryAsync(ct);
            await using var cmd = new NpgsqlCommand(sql, conn, tx);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var rows = new List<Dictionary<string, object?>>();
            var more = false;
            while (await reader.ReadAsync(ct))
            {
                if (rows.Count == maxRows) { more = true; break; }
                var row = new Dictionary<string, object?>();
                for (var i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = reader.IsDBNull(i) ? null : Simple(reader.GetValue(i));
                rows.Add(row);
            }
            await reader.CloseAsync();
            await tx.RollbackAsync(ct);
            return JsonSerializer.Serialize(rows) + (more ? $"\n[more rows: showing the first {maxRows}]" : "");
        }
        catch (PostgresException ex) { return $"Error: {ex.MessageText}"; }
    }

    [McpServerTool(Name = "sql_schema", ReadOnly = true, Destructive = false),
     Description("Lists the tables and columns the read-only database user can see.")]
    public Task<string> Schema(CancellationToken ct = default) => Query("""
        SELECT table_schema, table_name, string_agg(column_name || ' ' || data_type, ', ' ORDER BY ordinal_position) AS columns
        FROM information_schema.columns
        WHERE table_schema NOT IN ('pg_catalog', 'information_schema')
        GROUP BY table_schema, table_name ORDER BY table_schema, table_name
        """, 500, ct);

    private static object? Simple(object v) => v switch
    {
        DateTime or DateTimeOffset or Guid or decimal or TimeSpan => v.ToString(),
        byte[] b => $"[{b.Length} bytes]",
        _ => v,
    };
}
