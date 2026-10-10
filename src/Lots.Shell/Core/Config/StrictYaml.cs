using System.Reflection;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Lots.Shell.Core.Config;

/// <summary>
/// YAML for policy-carrying resources (profiles, schedules, knowledge sources): an unknown key is an error, never ignored. A typo
/// such as <c>requireAproval: [write]</c> would otherwise silently drop the approval it was meant to require.
/// </summary>
public static partial class StrictYaml
{
    public static IDeserializer Deserializer { get; } = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).Build();

    /// <summary>A readable message for a parse error, naming the unknown key, its line and the closest valid key.</summary>
    public static string Explain(Exception ex, Type root)
    {
        var m = UnknownProperty().Match(ex.Message);
        if (!m.Success) return $"not valid YAML: {ex.Message}";
        var key = m.Groups["key"].Value;
        var line = ex is YamlDotNet.Core.YamlException y && y.Start.Line > 0 ? $" at line {y.Start.Line}"
            : LineOf().Match(ex.Message) is { Success: true } l ? $" at line {l.Groups[1].Value}" : "";
        var owner = Owner(root, m.Groups["type"].Value);
        var keys = owner?.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => CamelCaseNamingConvention.Instance.Apply(p.Name)).ToList() ?? [];
        var guess = keys.Select(k => (k, d: Distance(k.ToLowerInvariant(), key.ToLowerInvariant()))).Where(x => x.d <= 3).OrderBy(x => x.d).Select(x => x.k).FirstOrDefault();
        return $"unknown key '{key}'{line}" + (guess is not null ? $" (did you mean '{guess}'?)" : keys.Count > 0 ? $" (valid here: {string.Join(", ", keys)})" : "");
    }

    /// <summary>The document class the error is about: the root or one of the classes nested next to it.</summary>
    private static Type? Owner(Type root, string fullName)
    {
        var name = fullName.Split('.', '+').Last();
        if (root.Name == name) return root;
        return root.DeclaringType?.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault(t => t.Name == name)
               ?? root.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault(t => t.Name == name);
    }

    private static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }

    [GeneratedRegex(@"Property '(?<key>[^']+)' not found on type '(?<type>[^']+)'")] private static partial Regex UnknownProperty();
    [GeneratedRegex(@"\(Line: (\d+)")] private static partial Regex LineOf();
}
