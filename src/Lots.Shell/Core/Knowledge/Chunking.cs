using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Lots.Shell.Core.Knowledge;

/// <summary>A piece of a document small enough to embed, with the heading path it sits under.</summary>
public sealed record Chunk(int Seq, string Heading, string Text);

/// <summary>Turns files into plain text that keeps the heading structure as markdown headings.</summary>
public static partial class TextExtraction
{
    public static readonly string[] SupportedExtensions = [".md", ".markdown", ".txt", ".html", ".htm", ".docx"];

    public static bool Supports(string fileName) =>
        SupportedExtensions.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase);

    public static string Extract(string fileName, byte[] content)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".html" or ".htm" => FromHtml(Encoding.UTF8.GetString(content)),
            ".docx" => FromDocx(content),
            _ => Encoding.UTF8.GetString(content).Replace("\r\n", "\n"),
        };
    }

    /// <summary>The title a document shows: its first heading, otherwise the file name.</summary>
    public static string TitleOf(string text, string fallback) =>
        HeadingLine().Match(text) is { Success: true } m ? m.Groups[2].Value.Trim() : fallback;

    public static string FromHtml(string html)
    {
        var s = ScriptOrStyle().Replace(html, " ");
        s = HtmlHeading().Replace(s, m => "\n\n" + new string('#', m.Groups[1].Value[0] - '0') + " " + StripTags(m.Groups[2].Value) + "\n\n");
        s = BlockEnd().Replace(s, "\n");
        s = StripTags(s);
        s = WebUtility.HtmlDecode(s);
        s = Spaces().Replace(s, " ");
        return BlankLines().Replace(s, "\n\n").Trim();
    }

    /// <summary>Word documents: paragraphs in order, Heading1..6 styles become markdown headings.</summary>
    public static string FromDocx(byte[] content)
    {
        using var zip = new ZipArchive(new MemoryStream(content));
        var entry = zip.GetEntry("word/document.xml") ?? throw new InvalidDataException("Not a Word document.");
        using var stream = entry.Open();
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var doc = XDocument.Load(stream);
        var sb = new StringBuilder();
        foreach (var p in doc.Descendants(w + "p"))
        {
            var text = string.Concat(p.Descendants(w + "t").Select(t => t.Value)).Trim();
            if (text.Length == 0) continue;
            var style = p.Element(w + "pPr")?.Element(w + "pStyle")?.Attribute(w + "val")?.Value ?? "";
            var level = Regex.Match(style, @"^Heading(\d)$", RegexOptions.IgnoreCase) is { Success: true } h ? int.Parse(h.Groups[1].Value) : 0;
            sb.Append(level > 0 ? new string('#', level) + " " + text + "\n\n" : text + "\n\n");
        }
        return sb.ToString().Trim();
    }

    private static string StripTags(string s) => Tags().Replace(s, "");

    [GeneratedRegex(@"<(script|style)[^>]*>.*?</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase)] private static partial Regex ScriptOrStyle();
    [GeneratedRegex(@"<h([1-6])[^>]*>(.*?)</h\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase)] private static partial Regex HtmlHeading();
    [GeneratedRegex(@"</(p|div|li|tr|br|section|article)>|<br\s*/?>", RegexOptions.IgnoreCase)] private static partial Regex BlockEnd();
    [GeneratedRegex(@"<[^>]+>")] private static partial Regex Tags();
    [GeneratedRegex(@"[ \t]+")] private static partial Regex Spaces();
    [GeneratedRegex(@"\n\s*\n\s*\n+")] private static partial Regex BlankLines();
    [GeneratedRegex(@"^(#{1,6})\s+(.+)$", RegexOptions.Multiline)] internal static partial Regex HeadingLine();
}

/// <summary>
/// Heading-aware chunking: a chunk never spans two sections, carries its heading path ("Runbook > Restart > Postgres") and
/// overlaps the previous chunk of the same section a little so a sentence cut at the edge is still found.
/// </summary>
public static class Chunker
{
    public static List<Chunk> Split(string text, int maxChars = 1200, int overlapChars = 150)
    {
        var chunks = new List<Chunk>();
        var path = new string?[7];
        var section = new StringBuilder();

        void Flush()
        {
            var body = section.ToString().Trim();
            section.Clear();
            if (body.Length == 0) return;
            var heading = string.Join(" > ", path.Where(h => h is not null));
            foreach (var piece in Pieces(body, maxChars, overlapChars))
                chunks.Add(new Chunk(chunks.Count, heading, piece));
        }

        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var m = TextExtraction.HeadingLine().Match(line);
            if (m.Success)
            {
                Flush();
                var level = m.Groups[1].Value.Length;
                path[level] = m.Groups[2].Value.Trim();
                for (var i = level + 1; i < path.Length; i++) path[i] = null;
                continue;
            }
            section.Append(line).Append('\n');
        }
        Flush();
        return chunks;
    }

    /// <summary>Splits a section at paragraph, then sentence, then word boundaries.</summary>
    private static IEnumerable<string> Pieces(string body, int max, int overlap)
    {
        if (body.Length <= max)
        {
            yield return body;
            yield break;
        }
        var start = 0;
        while (start < body.Length)
        {
            var end = Math.Min(body.Length, start + max);
            if (end < body.Length)
            {
                var window = body[start..end];
                var cut = Best(window, "\n\n", max / 2) ?? Best(window, ". ", max / 2) ?? Best(window, " ", max / 2);
                if (cut is { } c) end = start + c;
            }
            yield return body[start..end].Trim();
            if (end >= body.Length) yield break;
            var next = Math.Max(start + 1, end - overlap);
            // Start the overlap at a word boundary.
            var space = body.IndexOf(' ', next);
            start = space > 0 && space < end ? space + 1 : next;
        }
    }

    private static int? Best(string window, string separator, int minimum)
    {
        var i = window.LastIndexOf(separator, StringComparison.Ordinal);
        return i >= minimum ? i + separator.Length : null;
    }
}
