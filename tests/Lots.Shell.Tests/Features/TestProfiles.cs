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
                new ProfileRole("admin", [ToolRisk.Read, ToolRisk.Write], [ToolRisk.Write]),
            ])]);
}
