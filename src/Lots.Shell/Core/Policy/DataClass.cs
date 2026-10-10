namespace Lots.Shell.Core.Policy;

/// <summary>
/// How sensitive data is (#89), lowest to highest. Tools, knowledge sources and profiles carry one; a model endpoint has a
/// clearance, the highest class it may see. A run's class is the highest class of anything it has read so far.
/// </summary>
public enum DataClass { Public = 0, Internal = 1, Confidential = 2, Restricted = 3 }

public static class DataClasses
{
    public static bool TryParse(string? value, out DataClass result)
    {
        result = DataClass.Internal;
        return !string.IsNullOrWhiteSpace(value) && Enum.TryParse(value.Trim(), ignoreCase: true, out result) && Enum.IsDefined(result);
    }

    public static DataClass Parse(string? value, DataClass fallback) => TryParse(value, out var c) ? c : fallback;

    public static DataClass Max(DataClass a, DataClass b) => a >= b ? a : b;

    public static string Name(this DataClass c) => c.ToString().ToLowerInvariant();

    public const string Choices = "public|internal|confidential|restricted";
}

/// <summary>No configured model endpoint may see data of this class: the call is blocked rather than sent (#89).</summary>
public sealed class DataClassificationException(string message) : Exception(message);
