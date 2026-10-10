using System.Security.Cryptography;
using System.Text;
using Lots.Shell.Core.Knowledge;
using Lots.Shell.Core.Tools;
using UglyToad.PdfPig;

namespace Lots.Shell.Core.Attachments;

public sealed class AttachmentOptions
{
    public const string Section = "Attachments";
    public bool Enabled { get; set; } = true;
    /// <summary>Largest file accepted.</summary>
    public int MaxBytes { get; set; } = 10 * 1024 * 1024;
    /// <summary>Largest image sent to a vision model (larger ones are refused at upload).</summary>
    public int MaxImageBytes { get; set; } = 5 * 1024 * 1024;
    public int MaxPerRun { get; set; } = 5;
    /// <summary>Characters of extracted text given to the model per file; the rest is cut with a note.</summary>
    public int MaxTextChars { get; set; } = 20_000;
}

public static class AttachmentKinds
{
    public const string Image = "image";
    public const string Text = "text";
    public const string Document = "document";
}

/// <summary>An attachment as a run refers to it.</summary>
public sealed record AttachmentRef(Guid Id, string Name, string Kind);

/// <summary>What the shell keeps for an upload: kind, a normalised content type and the text a model can read.</summary>
public sealed record AttachmentContent(string Kind, string ContentType, string? Text);

/// <summary>
/// Accepts files for a run (#105): images (PNG, JPEG, GIF, WebP, checked by their magic bytes, not the name), PDFs, Word documents
/// and plain-text formats (logs, Markdown, JSON, CSV, YAML). Everything else is refused. The content type comes from the bytes,
/// never from the client, and files are only ever served back as downloads.
/// </summary>
public static class AttachmentReader
{
    private static readonly string[] TextExtensions = [".txt", ".log", ".md", ".markdown", ".json", ".csv", ".tsv", ".yaml", ".yml", ".xml", ".ini", ".conf", ".toml"];

    public static AttachmentContent Read(string fileName, byte[] data, AttachmentOptions o)
    {
        if (ImageType(data) is { } image)
        {
            if (data.Length > o.MaxImageBytes) throw new InvalidDataException($"Images can be at most {o.MaxImageBytes / (1024 * 1024)} MB.");
            return new AttachmentContent(AttachmentKinds.Image, image, null);
        }
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (data.AsSpan().StartsWith("%PDF-"u8)) return new AttachmentContent(AttachmentKinds.Document, "application/pdf", Pdf(data));
        if (ext == ".docx" && data.AsSpan().StartsWith("PK"u8))
            return new AttachmentContent(AttachmentKinds.Document, "application/vnd.openxmlformats-officedocument.wordprocessingml.document", TextExtraction.FromDocx(data));
        if (TextExtensions.Contains(ext) || ext == "")
        {
            if (data.AsSpan().IndexOf((byte)0) >= 0) throw new InvalidDataException("This looks like a binary file, not text.");
            return new AttachmentContent(AttachmentKinds.Text, "text/plain; charset=utf-8", new UTF8Encoding(false, false).GetString(data));
        }
        throw new InvalidDataException("Supported: images (PNG, JPEG, GIF, WebP), PDF, Word (.docx) and text files (logs, Markdown, JSON, CSV, YAML).");
    }

    private static string? ImageType(byte[] d)
    {
        var s = d.AsSpan();
        if (s.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return "image/png";
        if (s.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF })) return "image/jpeg";
        if (s.StartsWith("GIF87a"u8) || s.StartsWith("GIF89a"u8)) return "image/gif";
        if (s.Length > 12 && s.StartsWith("RIFF"u8) && s[8..12].SequenceEqual("WEBP"u8)) return "image/webp";
        return null;
    }

    private static string Pdf(byte[] data)
    {
        try
        {
            using var doc = PdfDocument.Open(data);
            var sb = new StringBuilder();
            foreach (var page in doc.GetPages())
            {
                if (sb.Length > 2_000_000) break; // enough for any model's context; protects against huge documents
                sb.Append("--- page ").Append(page.Number).Append(" ---\n").Append(page.Text).Append('\n');
            }
            return sb.ToString();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new InvalidDataException("The PDF could not be read.");
        }
    }

    public static string Sha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>
    /// The text the model gets for a readable file: inside the untrusted-data envelope like a tool result (ADR 0017), cut to the
    /// configured size. Returns whether it looked like an injection, so the run can be tainted.
    /// </summary>
    public static (string Text, bool Suspicious) ForModel(string fileName, string text, int maxChars)
    {
        var cut = text.Length <= maxChars ? text : text[..maxChars] + $"\n[cut: the file has {text.Length} characters, the first {maxChars} are shown]";
        var guarded = InjectionGuard.Guard("attachment " + fileName, cut);
        return (guarded.Text, guarded.Suspicious);
    }
}
