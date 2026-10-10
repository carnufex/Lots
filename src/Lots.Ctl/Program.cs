using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Lots.Shell.Core.Config;
using Lots.Shell.Core.Profiles;

namespace Lots.Ctl;

/// <summary>
/// lotsctl: Lots configuration as code (#67).
///   lotsctl validate -f profiles/            offline: parse and validate every manifest (no server needed)
///   lotsctl diff     -f gitops/ --url URL    server dry run: what apply would change (exit 3 with --exit-code if anything)
///   lotsctl apply    -f gitops/ --url URL [--prune] [--managed-by gitops|api]
///   lotsctl export   --url URL [-o all.yaml]
/// Authentication: --token or LOTS_TOKEN, or a client-credentials service token (--token-url, --client-id, LOTS_CLIENT_SECRET).
/// --output markdown prints a report suitable for a pull request comment.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return 0;
        }
        try
        {
            var o = Options.Parse(args.Skip(1).ToArray());
            return args[0] switch
            {
                "validate" => Validate(o),
                "diff" => await ApplyAsync(o, dryRun: true),
                "apply" => await ApplyAsync(o, dryRun: false),
                "export" => await ExportAsync(o),
                _ => Fail($"unknown command '{args[0]}'\n\n{Usage}"),
            };
        }
        catch (UsageException ex)
        {
            return Fail(ex.Message);
        }
        catch (HttpRequestException ex)
        {
            Console.Error.WriteLine($"lotsctl: cannot reach the server: {ex.Message}");
            return 2;
        }
    }

    public const string Usage = """
        lotsctl validate -f <file|dir> [-f ...]
        lotsctl diff     -f <file|dir> --url <shell> [--managed-by gitops|api] [--prune] [--exit-code] [--output text|markdown]
        lotsctl apply    -f <file|dir> --url <shell> [--managed-by gitops|api] [--prune] [--output text|markdown]
        lotsctl export   --url <shell> [-o <file>]
        auth: --token <jwt> | LOTS_TOKEN | --token-url <url> --client-id <id> (secret in LOTS_CLIENT_SECRET) [--scope <s>]
        """;

    private static int Fail(string message)
    {
        Console.Error.WriteLine("lotsctl: " + message);
        return 2;
    }

    /// <summary>Reads every .yaml/.yml file (directories recursively, sorted) into one multi-document stream.</summary>
    public static string ReadInputs(IEnumerable<string> paths)
    {
        var files = new List<string>();
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
                files.AddRange(Directory.EnumerateFiles(p, "*.*", SearchOption.AllDirectories)
                    .Where(f => f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
                    .Order(StringComparer.Ordinal));
            else if (File.Exists(p)) files.Add(p);
            else throw new UsageException($"no such file or directory: {p}");
        }
        if (files.Count == 0) throw new UsageException("no YAML files given (-f)");
        return string.Join("---\n", files.Select(f => File.ReadAllText(f).TrimEnd() + "\n"));
    }

    /// <summary>Offline validation with the shell's own parser: syntax, kinds, names and every profile rule.</summary>
    public static int Validate(Options o)
    {
        var errors = new List<string>();
        var docs = ConfigService.Split(ReadInputs(o.Files), errors);
        foreach (var d in docs.Where(d => d.Kind == ResourceKinds.Profile))
            try { ProfileParser.Parse(d.Spec, d.Name); }
            catch (ProfileException ex) { errors.AddRange(ex.Errors); }
        foreach (var dup in docs.GroupBy(d => (d.Kind, d.Name)).Where(g => g.Count() > 1))
            errors.Add($"{dup.Key.Kind} {dup.Key.Name} is defined {dup.Count()} times");
        foreach (var e in errors) Console.Error.WriteLine("error: " + e);
        Console.WriteLine(errors.Count == 0 ? $"ok: {docs.Count} resources valid" : $"{errors.Count} problem(s)");
        return errors.Count == 0 ? 0 : 1;
    }

    private static async Task<int> ApplyAsync(Options o, bool dryRun)
    {
        var yaml = ReadInputs(o.Files);
        using var http = await o.ClientAsync();
        using var res = await http.PostAsJsonAsync("admin/v1/apply", new { yaml, dryRun, managedBy = o.ManagedBy, prune = o.Prune });
        if (res.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            return Fail($"{(int)res.StatusCode}: the token is not accepted or lacks the admin role");
        var outcome = await res.Content.ReadFromJsonAsync<JsonElement>();
        Console.Write(o.Markdown ? Render.Markdown(outcome, dryRun) : Render.Text(outcome));
        var results = outcome.GetProperty("results").EnumerateArray().ToList();
        if (results.Any(r => r.GetProperty("errors").GetArrayLength() > 0)) return 1;
        return o.ExitCode && results.Any(r => r.GetProperty("action").GetString() != "unchanged") ? 3 : 0;
    }

    private static async Task<int> ExportAsync(Options o)
    {
        using var http = await o.ClientAsync();
        var yaml = await http.GetStringAsync("admin/v1/export");
        if (o.Out is { } file) await File.WriteAllTextAsync(file, yaml);
        else Console.Write(yaml);
        return 0;
    }
}

public sealed class UsageException(string message) : Exception(message);

public sealed class Options
{
    public List<string> Files { get; } = [];
    public string? Url { get; private set; }
    public string ManagedBy { get; private set; } = "gitops";
    public bool Prune { get; private set; }
    public bool ExitCode { get; private set; }
    public bool Markdown { get; private set; }
    public string? Out { get; private set; }
    public string? Token { get; private set; }
    public string? TokenUrl { get; private set; }
    public string? ClientId { get; private set; }
    public string? Scope { get; private set; }

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new UsageException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "-f" or "--file": o.Files.Add(Next()); break;
                case "--url": o.Url = Next().TrimEnd('/') + "/"; break;
                case "--managed-by": o.ManagedBy = Next() is var m && m is "api" or "gitops" ? m : throw new UsageException("--managed-by is api or gitops"); break;
                case "--prune": o.Prune = true; break;
                case "--exit-code": o.ExitCode = true; break;
                case "--output": o.Markdown = Next() == "markdown"; break;
                case "-o": o.Out = Next(); break;
                case "--token": o.Token = Next(); break;
                case "--token-url": o.TokenUrl = Next(); break;
                case "--client-id": o.ClientId = Next(); break;
                case "--scope": o.Scope = Next(); break;
                default: throw new UsageException($"unknown option {args[i]}");
            }
        }
        return o;
    }

    public async Task<HttpClient> ClientAsync()
    {
        var url = Url ?? Environment.GetEnvironmentVariable("LOTS_URL") ?? throw new UsageException("--url (or LOTS_URL) is required");
        var http = new HttpClient { BaseAddress = new Uri(url.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(2) };
        var token = Token ?? Environment.GetEnvironmentVariable("LOTS_TOKEN");
        if (token is null && TokenUrl is not null)
            token = await ClientCredentialsAsync(TokenUrl, ClientId ?? throw new UsageException("--client-id is required with --token-url"),
                Environment.GetEnvironmentVariable("LOTS_CLIENT_SECRET") ?? throw new UsageException("set LOTS_CLIENT_SECRET for --token-url"), Scope);
        if (token is not null) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        // Local stacks with dev identities only (Auth:Dev:AllowHeaders): act as a dedicated admin identity.
        if (token is null && Environment.GetEnvironmentVariable("LOTS_DEV_USER") is { } devUser)
        {
            http.DefaultRequestHeaders.Add("X-Dev-User", devUser);
            http.DefaultRequestHeaders.Add("X-Dev-Roles", "admin");
        }
        return http;
    }

    private static async Task<string> ClientCredentialsAsync(string tokenUrl, string clientId, string secret, string? scope)
    {
        using var http = new HttpClient();
        var form = new Dictionary<string, string> { ["grant_type"] = "client_credentials", ["client_id"] = clientId, ["client_secret"] = secret };
        if (scope is not null) form["scope"] = scope;
        using var res = await http.PostAsync(tokenUrl, new FormUrlEncodedContent(form));
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("access_token").GetString()!;
    }
}

public static class Render
{
    public static string Text(JsonElement outcome)
    {
        var sb = new StringBuilder();
        foreach (var r in outcome.GetProperty("results").EnumerateArray())
        {
            var action = r.GetProperty("action").GetString();
            sb.Append($"{action,-9} {r.GetProperty("kind").GetString()} {r.GetProperty("name").GetString()}");
            if (r.GetProperty("version").GetInt32() is var v and > 0) sb.Append($" v{v}");
            sb.Append('\n');
            foreach (var e in r.GetProperty("errors").EnumerateArray()) sb.Append($"  error: {e.GetString()}\n");
            if (action is "create" or "update" or "delete" && r.GetProperty("diff").GetString() is { } diff)
                foreach (var line in diff.TrimEnd('\n').Split('\n').Where(l => !l.StartsWith("  ", StringComparison.Ordinal)))
                    sb.Append("    ").Append(line).Append('\n');
        }
        sb.Append(outcome.GetProperty("applied").GetBoolean() ? "applied\n" : outcome.GetProperty("dryRun").GetBoolean() ? "dry run: nothing changed\n" : "not applied\n");
        return sb.ToString();
    }

    public static string Markdown(JsonElement outcome, bool dryRun)
    {
        var results = outcome.GetProperty("results").EnumerateArray().ToList();
        var sb = new StringBuilder($"### Lots configuration {(dryRun ? "plan" : "apply")}\n\n| Action | Kind | Name | Version |\n|---|---|---|---|\n");
        foreach (var r in results)
            sb.Append($"| {r.GetProperty("action").GetString()} | {r.GetProperty("kind").GetString()} | {r.GetProperty("name").GetString()} | {r.GetProperty("version").GetInt32()} |\n");
        foreach (var r in results.Where(r => r.GetProperty("errors").GetArrayLength() > 0))
            sb.Append($"\n**{r.GetProperty("name").GetString()}**: ").AppendJoin("; ", r.GetProperty("errors").EnumerateArray().Select(e => e.GetString())).Append('\n');
        foreach (var r in results.Where(r => r.GetProperty("action").GetString() is "create" or "update" or "delete"))
            sb.Append($"\n<details><summary>{r.GetProperty("kind").GetString()} {r.GetProperty("name").GetString()}</summary>\n\n```diff\n")
              .Append(("\n" + r.GetProperty("diff").GetString()).Replace("\n  ", "\n ").TrimStart('\n')).Append("```\n</details>\n");
        return sb.ToString();
    }
}
