using Lots.Shell.Core.Profiles;
using Lots.Shell.Core.Tools;

namespace Lots.Shell.Tests.Features;

public static class TestProfiles
{
    public const string Name = "test";

    /// <summary>A profile declaring the given tools. operator may read; admin may read and write (write needs approval).</summary>
    public static ProfileRegistry Registry(params (string Name, ToolRisk Risk)[] tools) =>
        new([new Profile(
            Name, 1, "test profile", "",
            [new McpServerConfig("sample", "http://localhost:1/mcp")],
            tools.Select(t => new ProfileTool(t.Name, t.Risk)).ToList(),
            [
                new ProfileRole("operator", [ToolRisk.Read], []),
                new ProfileRole("admin", [ToolRisk.Read, ToolRisk.Write], [ToolRisk.Write], [ToolRisk.Write]),
            ])]);
}

/// <summary>Tool results reach the model inside the untrusted-data envelope (#85); fake models that echo them want the data only.</summary>
public static class ToolText
{
    public static string? Data(string? modelText)
    {
        if (modelText is null || !modelText.StartsWith(Lots.Shell.Core.Tools.InjectionGuard.Open, StringComparison.Ordinal)) return modelText;
        var start = modelText.IndexOf('\n') + 1;
        var end = modelText.LastIndexOf("\n<<" + Lots.Shell.Core.Tools.InjectionGuard.Close, StringComparison.Ordinal);
        return end > start ? modelText[start..end] : modelText;
    }
}
