using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;

namespace Lots.Mcp.Toolpack;

/// <summary>
/// Read-only files within one root directory (a mounted volume). Paths are resolved, symbolic links followed to their real target,
/// and anything that ends up outside the root (../, absolute paths, links pointing out) is refused. Hidden files are not listed.
/// </summary>
public sealed class FileSandbox(string root)
{
    public string Root { get; } = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);

    public string Resolve(string relative)
    {
        var path = Path.GetFullPath(Path.Combine(Root, relative.TrimStart('/', '\\')));
        Inside(path);
        // Follow links (file or any parent directory) to where they really point.
        var real = RealPath(path);
        Inside(real);
        return real;
    }

    private void Inside(string path)
    {
        if (path != Root && !path.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("outside the allowed directory");
    }

    private static string RealPath(string path)
    {
        var parts = new List<string>();
        var current = path;
        while (current is not null)
        {
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
            {
                parts.Reverse();
                return Path.Combine([target.FullName, .. parts]);
            }
            var parent = Path.GetDirectoryName(current);
            if (parent is null) break;
            parts.Add(Path.GetFileName(current));
            current = parent;
        }
        return path;
    }
}

[McpServerToolType]
public sealed class FileTools(FileSandbox sandbox)
{
    [McpServerTool(Name = "list_files", ReadOnly = true, Destructive = false),
     Description("Lists files and folders in a directory of the shared file area (relative path, '' for the top).")]
    public string ListFiles([Description("Directory, relative to the file area")] string path = "")
    {
        try
        {
            var dir = sandbox.Resolve(path);
            if (!Directory.Exists(dir)) return $"Error: no directory '{path}'.";
            var sb = new StringBuilder();
            foreach (var d in Directory.EnumerateDirectories(dir).Where(d => !Path.GetFileName(d).StartsWith('.')).Order().Take(500))
                sb.AppendLine($"{Path.GetRelativePath(sandbox.Root, d).Replace('\\', '/')}/");
            foreach (var f in Directory.EnumerateFiles(dir).Where(f => !Path.GetFileName(f).StartsWith('.')).Order().Take(500))
                sb.AppendLine($"{Path.GetRelativePath(sandbox.Root, f).Replace('\\', '/')} | size={new FileInfo(f).Length}");
            return sb.Length == 0 ? "(empty)" : sb.ToString().TrimEnd();
        }
        catch (UnauthorizedAccessException) { return "Error: that path is outside the file area."; }
    }

    [McpServerTool(Name = "read_file", ReadOnly = true, Destructive = false),
     Description("Reads a text file from the shared file area. Long files are returned in parts: use offset to continue.")]
    public string ReadFile(
        [Description("File, relative to the file area")] string path,
        [Description("Character offset to start at (default 0)")] int offset = 0,
        [Description("Maximum characters (default 8000)")] int maxChars = 8000)
    {
        try
        {
            var file = sandbox.Resolve(path);
            if (!File.Exists(file)) return $"Error: no file '{path}'.";
            if (Path.GetFileName(file).StartsWith('.')) return "Error: hidden files are not available.";
            var text = File.ReadAllText(file);
            if (text.Contains('\0')) return "Error: binary file.";
            offset = Math.Clamp(offset, 0, text.Length);
            var part = text.Substring(offset, Math.Min(Math.Clamp(maxChars, 1, 50_000), text.Length - offset));
            return offset + part.Length < text.Length ? part + $"\n[continues: offset={offset + part.Length} of {text.Length}]" : part;
        }
        catch (UnauthorizedAccessException) { return "Error: that path is outside the file area."; }
    }
}
