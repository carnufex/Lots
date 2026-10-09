using Lots.Shell.Core.Tools;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Lots.Shell.Core.Profiles;

/// <summary>An MCP server a profile talks to.</summary>
/// <summary>An MCP server a profile talks to and the ADR 0004 strategy used to authenticate towards it.</summary>
public sealed record McpServerConfig(
    string Name, string Url, string Auth = AuthStrategies.SharedServiceAccount, ServerCredentials? Credentials = null);

public static class CredentialTypes
{
    /// <summary>A static bearer token read from an environment variable.</summary>
    public const string Bearer = "bearer";

    /// <summary>OAuth client-credentials grant (Authentik style, optionally with a service account username/app password).</summary>
    public const string OAuthClientCredentials = "oauth-client-credentials";
}

/// <summary>How the shell authenticates to a server. Secrets are referenced by environment variable name, never stored.</summary>
public sealed record ServerCredentials(
    string Type, string? TokenEnv = null, string? TokenUrl = null, string? ClientId = null,
    string? Username = null, string? PasswordEnv = null, string? Scope = null);

/// <summary>The backend authentication strategies of ADR 0004, strongest first.</summary>
public static class AuthStrategies
{
    public const string Delegated = "delegated";
    public const string UserConnected = "user-connected";
    public const string Impersonation = "impersonation";
    public const string ServiceAccountPerRole = "service-account-per-role";
    public const string SharedServiceAccount = "shared-service-account";

    public static readonly IReadOnlyList<string> All =
        [Delegated, UserConnected, Impersonation, ServiceAccountPerRole, SharedServiceAccount];
}

/// <summary>A tool the profile exposes, with the risk class an administrator assigned to it.</summary>
public sealed record ProfileTool(string Name, ToolRisk Risk);

/// <summary>
/// What a role may do within a profile. <see cref="Allow"/> lists risk classes the role may use;
/// classes in <see cref="RequireApproval"/> (a subset of Allow) additionally need an approval per call.
/// <see cref="Approve"/> lists classes the role may approve.
/// </summary>
public sealed record ProfileRole(
    string Name, IReadOnlyList<ToolRisk> Allow, IReadOnlyList<ToolRisk> RequireApproval, IReadOnlyList<ToolRisk>? Approve = null)
{
    /// <summary>Risk classes this role may approve for other (or the same) users' runs.</summary>
    public IReadOnlyList<ToolRisk> MayApprove => Approve ?? [];
}

/// <summary>A pluggable domain: MCP servers, tool risk classes, role grants and instructions.</summary>
public sealed record Profile(
    string Name,
    int Version,
    string Description,
    string Instructions,
    IReadOnlyList<McpServerConfig> Servers,
    IReadOnlyList<ProfileTool> Tools,
    IReadOnlyList<ProfileRole> Roles);

public sealed class ProfileException(IReadOnlyList<string> errors)
    : Exception("Invalid profile: " + string.Join("; ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>Parses and validates profile manifests (YAML). All problems are reported together.</summary>
public static class ProfileParser
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static Profile Parse(string yaml, string source = "profile")
    {
        ProfileDocument doc;
        try
        {
            doc = Yaml.Deserialize<ProfileDocument>(yaml) ?? new ProfileDocument();
        }
        catch (Exception ex)
        {
            throw new ProfileException([$"{source}: not valid YAML: {ex.Message}"]);
        }

        var errors = new List<string>();
        void Err(string m) => errors.Add($"{source}: {m}");

        if (string.IsNullOrWhiteSpace(doc.Name)) Err("name is required");
        if (doc.Version < 1) Err("version must be 1 or higher");

        var servers = new List<McpServerConfig>();
        foreach (var s in doc.Servers ?? [])
        {
            if (string.IsNullOrWhiteSpace(s.Name) || !Uri.TryCreate(s.Url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
                Err($"server '{s.Name}' needs a name and an absolute http(s) url");
            else
            {
                var auth = string.IsNullOrWhiteSpace(s.Auth) ? AuthStrategies.SharedServiceAccount : s.Auth.Trim().ToLowerInvariant();
                if (!AuthStrategies.All.Contains(auth))
                    Err($"server '{s.Name}' has unknown auth strategy '{s.Auth}' ({string.Join('|', AuthStrategies.All)})");
                else
                {
                    var creds = ParseCredentials(s, Err);
                    if (s.Credentials is null || creds is not null) servers.Add(new McpServerConfig(s.Name!, s.Url!, auth, creds));
                }
            }
        }
        Duplicates(servers.Select(s => s.Name), "server", Err);

        var tools = new List<ProfileTool>();
        foreach (var t in doc.Tools ?? [])
        {
            if (string.IsNullOrWhiteSpace(t.Name)) { Err("a tool has no name"); continue; }
            if (!TryRisk(t.Risk, out var risk)) { Err($"tool '{t.Name}' has unknown risk '{t.Risk}' (read|write|destructive)"); continue; }
            tools.Add(new ProfileTool(t.Name, risk));
        }
        Duplicates(tools.Select(t => t.Name), "tool", Err);

        var roles = new List<ProfileRole>();
        foreach (var r in doc.Roles ?? [])
        {
            if (string.IsNullOrWhiteSpace(r.Name)) { Err("a role has no name"); continue; }
            var allow = ParseRisks(r.Allow, $"role '{r.Name}' allow", Err);
            var approval = ParseRisks(r.RequireApproval, $"role '{r.Name}' requireApproval", Err);
            foreach (var a in approval.Where(a => !allow.Contains(a)))
                Err($"role '{r.Name}' requires approval for '{a}' but does not allow it");
            var mayApprove = ParseRisks(r.Approve, $"role '{r.Name}' approve", Err);
            roles.Add(new ProfileRole(r.Name, allow, approval, mayApprove));
        }
        Duplicates(roles.Select(r => r.Name), "role", Err);

        if (errors.Count > 0) throw new ProfileException(errors);

        return new Profile(doc.Name!, doc.Version, doc.Description ?? "", doc.Instructions?.Trim() ?? "", servers, tools, roles);
    }

    private static ServerCredentials? ParseCredentials(ServerDoc s, Action<string> err)
    {
        var c = s.Credentials;
        if (c is null) return null;
        var type = c.Type?.Trim().ToLowerInvariant();
        switch (type)
        {
            case CredentialTypes.Bearer:
                if (string.IsNullOrWhiteSpace(c.TokenEnv)) { err($"server '{s.Name}' credentials: tokenEnv is required for bearer"); return null; }
                return new ServerCredentials(type, TokenEnv: c.TokenEnv);
            case CredentialTypes.OAuthClientCredentials:
                if (!Uri.TryCreate(c.TokenUrl, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(c.ClientId))
                {
                    err($"server '{s.Name}' credentials: tokenUrl (absolute http(s)) and clientId are required");
                    return null;
                }
                return new ServerCredentials(type, TokenUrl: c.TokenUrl, ClientId: c.ClientId, Username: c.Username, PasswordEnv: c.PasswordEnv, Scope: c.Scope);
            default:
                err($"server '{s.Name}' credentials: unknown type '{c.Type}' ({CredentialTypes.Bearer}|{CredentialTypes.OAuthClientCredentials})");
                return null;
        }
    }

    private static bool TryRisk(string? s, out ToolRisk risk) =>
        Enum.TryParse(s, ignoreCase: true, out risk) && Enum.IsDefined(risk);

    private static List<ToolRisk> ParseRisks(List<string>? values, string what, Action<string> err)
    {
        var list = new List<ToolRisk>();
        foreach (var v in values ?? [])
            if (TryRisk(v, out var r)) list.Add(r);
            else err($"{what} has unknown risk '{v}'");
        return list;
    }

    private static void Duplicates(IEnumerable<string> names, string what, Action<string> err)
    {
        foreach (var d in names.GroupBy(n => n, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            err($"duplicate {what} '{d.Key}'");
    }

    private sealed class ProfileDocument
    {
        public string? Name { get; set; }
        public int Version { get; set; }
        public string? Description { get; set; }
        public string? Instructions { get; set; }
        public List<ServerDoc>? Servers { get; set; }
        public List<ToolDoc>? Tools { get; set; }
        public List<RoleDoc>? Roles { get; set; }
    }

    private sealed class ServerDoc
    {
        public string? Name { get; set; }
        public string? Url { get; set; }
        public string? Auth { get; set; }
        public CredentialsDoc? Credentials { get; set; }
    }

    private sealed class CredentialsDoc
    {
        public string? Type { get; set; }
        public string? TokenEnv { get; set; }
        public string? TokenUrl { get; set; }
        public string? ClientId { get; set; }
        public string? Username { get; set; }
        public string? PasswordEnv { get; set; }
        public string? Scope { get; set; }
    }
    private sealed class ToolDoc { public string? Name { get; set; } public string? Risk { get; set; } }

    private sealed class RoleDoc
    {
        public string? Name { get; set; }
        public List<string>? Allow { get; set; }
        public List<string>? RequireApproval { get; set; }
        public List<string>? Approve { get; set; }
    }
}

/// <summary>All profiles loaded from a directory at startup. Startup fails if any manifest is invalid.</summary>
public sealed class ProfileRegistry
{
    private readonly Dictionary<string, Profile> _profiles;

    public ProfileRegistry(IEnumerable<Profile> profiles)
    {
        _profiles = new Dictionary<string, Profile>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in profiles)
            if (!_profiles.TryAdd(p.Name, p))
                throw new ProfileException([$"duplicate profile name '{p.Name}'"]);
    }

    public IReadOnlyCollection<Profile> All => _profiles.Values;

    public Profile? Find(string name) => _profiles.GetValueOrDefault(name);

    public IReadOnlyList<McpServerConfig> Servers =>
        _profiles.Values.SelectMany(p => p.Servers).DistinctBy(s => s.Name).ToList();

    public static ProfileRegistry LoadDirectory(string path)
    {
        if (!Directory.Exists(path))
            throw new ProfileException([$"profiles directory '{path}' does not exist"]);

        var errors = new List<string>();
        var profiles = new List<Profile>();
        foreach (var file in Directory.EnumerateFiles(path, "*.y*ml").Order())
        {
            try { profiles.Add(ProfileParser.Parse(File.ReadAllText(file), Path.GetFileName(file))); }
            catch (ProfileException ex) { errors.AddRange(ex.Errors); }
        }
        if (errors.Count > 0) throw new ProfileException(errors);
        if (profiles.Count == 0) throw new ProfileException([$"no profile manifests found in '{path}'"]);
        return new ProfileRegistry(profiles);
    }
}
