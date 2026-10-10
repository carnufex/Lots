using System.Text.RegularExpressions;

namespace Lots.Shell.Core.Security;

public enum PiiKind { Email, Phone, Personnummer, PaymentCard, Token }

/// <summary>Masks a finished run's stored prompt, conversation and answer when its profile has <c>pii.scope: all</c> (#90).</summary>
public static class PiiMasking
{
    public static void MaskConversation(Persistence.RunRecord run, Profiles.Profile? profile)
    {
        if (profile?.Pii is not { All: true } pii) return;
        run.Prompt = PiiRedactor.Redact(run.Prompt, pii.Kinds);
        run.FinalAnswer = PiiRedactor.RedactOrNull(run.FinalAnswer, pii.Kinds);
        foreach (var m in run.Messages)
        {
            m.Content = PiiRedactor.RedactOrNull(m.Content, pii.Kinds);
            m.ToolCallsJson = PiiRedactor.RedactOrNull(m.ToolCallsJson, pii.Kinds);
        }
    }
}

/// <summary>
/// Finds and masks personal data in text that Lots stores (#90): e-mail addresses, phone numbers (Swedish and international),
/// Swedish personal identity and coordination numbers (checked with the Luhn digit, so dates and order numbers are left alone),
/// payment card numbers (Luhn) and credentials (<see cref="SecretRedactor"/>). Which kinds apply is chosen per profile.
/// </summary>
public static partial class PiiRedactor
{
    public static readonly IReadOnlyDictionary<PiiKind, string> Masks = new Dictionary<PiiKind, string>
    {
        [PiiKind.Email] = "[email]",
        [PiiKind.Phone] = "[phone]",
        [PiiKind.Personnummer] = "[personnummer]",
        [PiiKind.PaymentCard] = "[card]",
        [PiiKind.Token] = SecretRedactor.Mask,
    };

    /// <summary>Kinds masked in every log line, whatever the profile (<c>Privacy:RedactLogs</c>).</summary>
    public static IReadOnlyCollection<PiiKind> LogKinds { get; set; } = [];

    public static bool TryParse(string? value, out PiiKind kind)
    {
        kind = default;
        var v = value?.Trim().ToLowerInvariant().Replace("-", "").Replace("_", "");
        switch (v)
        {
            case "email" or "emails": kind = PiiKind.Email; return true;
            case "phone" or "phones" or "phonenumber" or "phonenumbers": kind = PiiKind.Phone; return true;
            case "personnummer" or "personalnumber" or "ssn" or "samordningsnummer": kind = PiiKind.Personnummer; return true;
            case "card" or "cards" or "paymentcard" or "creditcard": kind = PiiKind.PaymentCard; return true;
            case "token" or "tokens" or "secrets" or "credentials": kind = PiiKind.Token; return true;
            default: return false;
        }
    }

    public const string Choices = "email|phone|personnummer|card|tokens";

    public static string Redact(string text, IReadOnlyCollection<PiiKind> kinds)
    {
        if (string.IsNullOrEmpty(text) || kinds.Count == 0) return text;
        if (kinds.Contains(PiiKind.Token)) text = SecretRedactor.Redact(text);
        if (kinds.Contains(PiiKind.Email)) text = Email().Replace(text, Masks[PiiKind.Email]);
        // Identity and card numbers before phone numbers: a personnummer must not be half-matched as a phone number.
        if (kinds.Contains(PiiKind.Personnummer)) text = Personnummer().Replace(text, m => IsPersonnummer(m.Value) ? Masks[PiiKind.Personnummer] : m.Value);
        if (kinds.Contains(PiiKind.PaymentCard)) text = Card().Replace(text, m => Luhn(Digits(m.Value)) ? Masks[PiiKind.PaymentCard] : m.Value);
        if (kinds.Contains(PiiKind.Phone)) text = Phone().Replace(text, m => PlausiblePhone(m.Value) ? m.Groups["pre"].Value + Masks[PiiKind.Phone] : m.Value);
        return text;
    }

    public static string? RedactOrNull(string? text, IReadOnlyCollection<PiiKind> kinds) => text is null ? null : Redact(text, kinds);

    /// <summary>YYMMDD-NNNN, YYMMDD+NNNN (over 100), YYYYMMDD-NNNN or without separator; day + 60 for coordination numbers.</summary>
    public static bool IsPersonnummer(string candidate)
    {
        var digits = Digits(candidate);
        if (digits.Length == 12) digits = digits[2..];
        if (digits.Length != 10) return false;
        var month = int.Parse(digits[2..4]);
        var day = int.Parse(digits[4..6]);
        if (day > 60) day -= 60; // samordningsnummer
        return month is >= 1 and <= 12 && day is >= 1 and <= 31 && Luhn(digits);
    }

    private static bool Luhn(string digits)
    {
        if (digits.Length < 10) return false;
        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            var d = digits[digits.Length - 1 - i] - '0';
            if (i % 2 == 1) { d *= 2; if (d > 9) d -= 9; }
            sum += d;
        }
        return sum % 10 == 0;
    }

    private static string Digits(string s) => new(s.Where(char.IsAsciiDigit).ToArray());

    /// <summary>Enough digits to be a phone number, not so many that it is something else, and not an ISO date or a version.</summary>
    private static bool PlausiblePhone(string match)
    {
        var digits = Digits(match);
        return digits.Length is >= 8 and <= 15 && !IsoDate().IsMatch(match.Trim());
    }

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")]
    private static partial Regex Email();

    [GeneratedRegex(@"(?<![\d-])(?:19|20)?\d{6}[-+ ]?\d{4}(?![\d-])")]
    private static partial Regex Personnummer();

    [GeneratedRegex(@"(?<!\d)(?:\d[ -]?){12,18}\d(?!\d)")]
    private static partial Regex Card();

    // +46 70 123 45 67, 0046 8 123 456 78, 070-123 45 67, 08-123 456 78, (08) 123 45 67, +1 415 555 0100. A number has to start
    // like a phone number (+, 00 or a Swedish trunk 0): plain digit runs are order numbers, ids and amounts far more often.
    [GeneratedRegex(@"(?<pre>^|[\s:(,;=])(?:(?:\+|00)\d{1,3}|\(?0\d{1,3}\)?)(?:[ -]?\d{1,4}){1,5}(?![\d.:/-])", RegexOptions.Multiline)]
    private static partial Regex Phone();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$")]
    private static partial Regex IsoDate();
}
