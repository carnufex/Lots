using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lots.Evals;

/// <summary>Word error rate (#123): edits (substitutions, deletions, insertions) over reference words, after normalising.</summary>
public static partial class Wer
{
    /// <summary>Lowercase, punctuation removed (letters of every language kept, so å/ä/ö stay), whitespace collapsed.</summary>
    public static string[] Words(string text) =>
        NotWord().Replace(text.ToLowerInvariant().Replace('’', '\''), " ").Split(' ', StringSplitOptions.RemoveEmptyEntries);

    public static int Edits(IReadOnlyList<string> reference, IReadOnlyList<string> hypothesis)
    {
        var prev = new int[hypothesis.Count + 1];
        var cur = new int[hypothesis.Count + 1];
        for (var j = 0; j <= hypothesis.Count; j++) prev[j] = j;
        for (var i = 1; i <= reference.Count; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= hypothesis.Count; j++)
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + (reference[i - 1] == hypothesis[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[hypothesis.Count];
    }

    public static double Of(string reference, string hypothesis)
    {
        var r = Words(reference);
        return r.Length == 0 ? (Words(hypothesis).Length == 0 ? 0 : 1) : (double)Edits(r, Words(hypothesis)) / r.Length;
    }

    [GeneratedRegex(@"[^\p{L}\p{N}']+")] private static partial Regex NotWord();
}

public sealed record Recording(string File, string Language, string Text, string? Note = null);

public sealed record LatencyStage(string Name, int Count, long P50, long P95, long? BudgetP50, long? BudgetP95)
{
    public bool WithinBudget => (BudgetP50 is null || Count == 0 || P50 <= BudgetP50) && (BudgetP95 is null || Count == 0 || P95 <= BudgetP95);
}

/// <summary>Per stage of a voice turn, from History: what one turn spent where (milliseconds; null when the turn had no such stage).</summary>
public sealed record VoiceTurnTiming(long? Stt, long Model, long Tools, long? FirstAudio, long? SpeechToAnswerAudio);

public static class VoiceLatency
{
    public static readonly string[] Stages = ["stt", "model", "tools", "firstAudio", "speechToAnswerAudio"];

    /// <summary>
    /// Timings of one turn from its History timeline. End of speech is when the transcription started (the recording was uploaded);
    /// the answer's first audio is when speech output started streaming.
    /// </summary>
    public static VoiceTurnTiming FromEvents(IReadOnlyList<(string Kind, long StartMs, long DurationMs, long? FirstAudioMs)> events)
    {
        var stt = events.FirstOrDefault(e => e.Kind == "stt");
        var tts = events.FirstOrDefault(e => e.Kind == "tts");
        var hasStt = stt.Kind is not null;
        var hasTts = tts.Kind is not null;
        return new VoiceTurnTiming(
            hasStt ? stt.DurationMs : null,
            events.Where(e => e.Kind == "llm").Sum(e => e.DurationMs),
            events.Where(e => e.Kind == "tool").Sum(e => e.DurationMs),
            hasTts ? tts.FirstAudioMs ?? tts.DurationMs : null,
            hasStt && hasTts ? Math.Max(0, tts.StartMs - stt.StartMs) : null);
    }

    public static IReadOnlyList<LatencyStage> Summarise(IReadOnlyList<VoiceTurnTiming> turns, IReadOnlyDictionary<string, (long? P50, long? P95)> budget)
    {
        IEnumerable<long> Of(string stage) => turns.Select(t => stage switch
        {
            "stt" => t.Stt,
            "model" => (long?)t.Model,
            "tools" => t.Tools > 0 ? t.Tools : null,
            "firstAudio" => t.FirstAudio,
            _ => t.SpeechToAnswerAudio,
        }).OfType<long>();
        return Stages.Select(s =>
        {
            var v = Of(s).ToList();
            var b = budget.TryGetValue(s, out var x) ? x : (null, null);
            return new LatencyStage(s, v.Count, EvalHistory.Percentile(v, 50), EvalHistory.Percentile(v, 95), b.Item1, b.Item2);
        }).ToList();
    }

    public static IReadOnlyDictionary<string, (long? P50, long? P95)> ParseBudget(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var d = new Dictionary<string, (long?, long?)>();
        foreach (var p in doc.RootElement.EnumerateObject())
            if (p.Value.ValueKind == JsonValueKind.Object)
                d[p.Name] = (p.Value.TryGetProperty("p50", out var a) ? a.GetInt64() : null, p.Value.TryGetProperty("p95", out var b) ? b.GetInt64() : null);
        return d;
    }
}

/// <summary>One blind clip of the listening test.</summary>
public sealed record ListeningClip(string Code, string Voice, string Language, string SentenceId, string Text, long FirstAudioMs, long TotalMs,
    string? RoundTrip, double? RoundTripWer, string? Fallback);

public sealed record Rating(string Code, int Naturalness, int Clarity, string? Note);

public static class Listening
{
    /// <summary>Mean opinion scores per voice and language from the rating sheet's CSV (code,naturalness,clarity,note).</summary>
    public static IReadOnlyList<Rating> ParseRatings(string csv) =>
        csv.Split('\n').Skip(1).Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).Select(l =>
        {
            var f = SplitCsv(l);
            return new Rating(f[0], int.Parse(f[1], CultureInfo.InvariantCulture), int.Parse(f[2], CultureInfo.InvariantCulture), f.Count > 3 && f[3].Length > 0 ? f[3] : null);
        }).Where(r => r.Naturalness is >= 1 and <= 5 && r.Clarity is >= 1 and <= 5).ToList();

    private static List<string> SplitCsv(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else sb.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == ',') { fields.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        fields.Add(sb.ToString());
        return fields;
    }

    public static string Report(IReadOnlyList<ListeningClip> clips, IReadOnlyList<Rating> ratings)
    {
        var byCode = ratings.GroupBy(r => r.Code).ToDictionary(g => g.Key, g => g.ToList());
        var sb = new StringBuilder("# Speech output: listening test\n\n");
        sb.AppendLine(ratings.Count == 0
            ? "No ratings yet: open `sheet.html` in this folder, listen and rate, then score with `--mode voice-tts-score`.\n"
            : $"{ratings.Count} ratings. MOS = mean opinion score, 1 (bad) to 5 (excellent).\n");
        sb.AppendLine("| Voice | Language | Clips | Naturalness (MOS) | Clarity (MOS) | Round-trip WER | First audio p50 (ms) | p95 (ms) | Fell back |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
        foreach (var g in clips.GroupBy(c => (c.Voice, c.Language)).OrderBy(g => g.Key.Language).ThenBy(g => g.Key.Voice))
        {
            var rs = g.SelectMany(c => byCode.GetValueOrDefault(c.Code) ?? []).ToList();
            var wers = g.Select(c => c.RoundTripWer).OfType<double>().ToList();
            sb.AppendLine($"| {g.Key.Voice} | {g.Key.Language} | {g.Count()} | {Mos(rs.Select(r => r.Naturalness))} | {Mos(rs.Select(r => r.Clarity))} | " +
                          $"{(wers.Count == 0 ? "–" : wers.Average().ToString("P0", CultureInfo.InvariantCulture))} | " +
                          $"{EvalHistory.Percentile(g.Select(c => c.FirstAudioMs), 50)} | {EvalHistory.Percentile(g.Select(c => c.FirstAudioMs), 95)} | " +
                          $"{g.Count(c => c.Fallback is not null)} |");
        }
        var notes = clips.SelectMany(c => (byCode.GetValueOrDefault(c.Code) ?? []).Where(r => r.Note is not null).Select(r => (c, r.Note))).ToList();
        if (notes.Count > 0)
        {
            sb.AppendLine().AppendLine("## Notes").AppendLine();
            foreach (var (c, note) in notes) sb.AppendLine($"- **{c.Voice}** ({c.Language}, {c.SentenceId}): {note}");
        }
        var misheard = clips.Where(c => c.RoundTripWer > 0.2).ToList();
        if (misheard.Count > 0)
        {
            sb.AppendLine().AppendLine("## Hard to understand by the speech recogniser (round-trip WER above 20 %)").AppendLine();
            foreach (var c in misheard) sb.AppendLine($"- {c.Voice} ({c.Language}): \"{c.Text}\" was heard as \"{c.RoundTrip}\"");
        }
        return sb.ToString();
    }

    private static string Mos(IEnumerable<int> xs)
    {
        var l = xs.ToList();
        return l.Count == 0 ? "–" : l.Average().ToString("0.0", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// A self-contained rating page: clips in a shuffled order under neutral codes (no voice names), two 1-5 scales and a note per clip,
    /// kept in the browser while rating and downloaded as CSV.
    /// </summary>
    public static string Sheet(IReadOnlyList<ListeningClip> clips, int seed)
    {
        var order = clips.OrderBy(c => HashCode.Combine(seed, c.Code)).ToList();
        var rows = new StringBuilder();
        foreach (var c in order)
            rows.Append($"<li data-code=\"{c.Code}\"><div class=\"head\"><b>{c.Code}</b> <span class=\"lang\">{c.Language}</span> <span class=\"text\">{Html(c.Text)}</span></div>")
                .Append($"<audio controls preload=\"none\" src=\"clips/{c.Code}.wav\"></audio>")
                .Append("<div class=\"scales\">").Append(Scale("naturalness", "Natural")).Append(Scale("clarity", "Clear")).Append("</div>")
                .Append("<input class=\"note\" placeholder=\"Note: mispronounced words, odd pauses, robotic...\"></li>\n");
        return Template.Replace("{{ROWS}}", rows.ToString()).Replace("{{COUNT}}", clips.Count.ToString(CultureInfo.InvariantCulture));

        static string Scale(string name, string label) =>
            $"<fieldset><legend>{label}</legend>" + string.Concat(Enumerable.Range(1, 5).Select(i => $"<label><input type=\"radio\" name=\"{name}\" value=\"{i}\">{i}</label>")) + "</fieldset>";
    }

    private static string Html(string s) => System.Net.WebUtility.HtmlEncode(s);

    private const string Template = """
        <!doctype html>
        <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
        <title>Listening test</title>
        <style>
        :root { color-scheme: dark; --bg:#0c0e11; --surface:#12151a; --border:#232830; --text:#e6e9ee; --muted:#8b93a1; --accent:#2dd4bf; }
        body { margin:0; padding:24px 16px; background:var(--bg); color:var(--text); font:14px/1.5 system-ui, sans-serif; }
        main { max-width:760px; margin:0 auto; }
        ol { list-style:none; padding:0; display:flex; flex-direction:column; gap:12px; }
        li { background:var(--surface); border:1px solid var(--border); border-radius:8px; padding:12px; display:flex; flex-direction:column; gap:8px; }
        .lang { color:var(--muted); text-transform:uppercase; font-size:11px; } .text { color:var(--muted); }
        audio { width:100%; } .scales { display:flex; flex-wrap:wrap; gap:16px; }
        fieldset { border:0; padding:0; margin:0; display:flex; gap:10px; align-items:center; } legend { float:left; margin-right:6px; color:var(--muted); }
        input.note { background:var(--bg); color:var(--text); border:1px solid var(--border); border-radius:6px; padding:6px 8px; font:inherit; }
        button { background:var(--accent); color:#07211c; border:0; border-radius:6px; padding:8px 14px; font-weight:600; cursor:pointer; }
        p.muted { color:var(--muted); }
        </style></head>
        <body><main>
        <h1>Listening test</h1>
        <p class="muted">{{COUNT}} clips in a random order. The voice behind each code is hidden until scoring. Rate how natural it sounds and how
        clear it is to understand (1 bad, 5 excellent); headphones help. Your ratings stay in this browser until you download them.</p>
        <ol id="clips">
        {{ROWS}}
        </ol>
        <p><button id="dl">Download ratings (CSV)</button> <span id="done" class="muted"></span></p>
        </main>
        <script>
        const KEY = 'listening:' + location.pathname
        const load = () => { try { return JSON.parse(localStorage.getItem(KEY) || '{}') } catch { return {} } }
        const save = (s) => { try { localStorage.setItem(KEY, JSON.stringify(s)) } catch {} }
        const state = load()
        const items = [...document.querySelectorAll('li[data-code]')]
        const count = () => { document.getElementById('done').textContent = items.filter((li) => state[li.dataset.code]?.naturalness && state[li.dataset.code]?.clarity).length + ' of ' + items.length + ' rated' }
        for (const li of items) {
          const code = li.dataset.code
          li.querySelectorAll('input[type=radio]').forEach((r) => { r.name = r.name + '-' + code; if (state[code]?.[r.name.split('-')[0]] == r.value) r.checked = true
            r.addEventListener('change', () => { state[code] = { ...state[code], [r.name.split('-')[0]]: r.value }; save(state); count() }) })
          const note = li.querySelector('.note'); note.value = state[code]?.note || ''
          note.addEventListener('input', () => { state[code] = { ...state[code], note: note.value }; save(state) })
        }
        count()
        document.getElementById('dl').onclick = () => {
          const q = (s) => '"' + String(s ?? '').replace(/"/g, '""') + '"'
          const lines = ['code,naturalness,clarity,note', ...items.map((li) => { const s = state[li.dataset.code] || {}; return [li.dataset.code, s.naturalness || '', s.clarity || '', q(s.note)].join(',') }).filter((l) => !/^[^,]+,,/.test(l))]
          const a = document.createElement('a'); a.href = URL.createObjectURL(new Blob([lines.join('\n') + '\n'], { type: 'text/csv' })); a.download = 'ratings.csv'; a.click()
        }
        </script></body></html>
        """;
}

/// <summary>
/// Voice evals (#123, continues #36). <c>--mode voice-stt</c>: recognition on real recordings through the shell's own transcription
/// endpoint (vocabulary included), hinted and with language detection. <c>--mode voice-latency</c>: per-stage p50/p95 of recent voice
/// turns from History against <c>evals/voice/budget.json</c>. <c>--mode voice-tts</c>: a blind listening test (clips, sheet, automatic
/// round-trip intelligibility and first-audio time) straight from the voice service; <c>--mode voice-tts-score</c> scores the ratings.
/// </summary>
public static class VoiceCli
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> SttAsync(HttpClient http, Func<string, string?> arg, string historyRoot, bool saveHistory)
    {
        var dir = arg("--recordings") ?? "evals/voice/recordings";
        var manifest = Path.Combine(dir, "manifest.json");
        if (!File.Exists(manifest))
        {
            Console.Error.WriteLine($"No {manifest}: see evals/voice/README.md");
            return 2;
        }
        var items = JsonSerializer.Deserialize<List<Recording>>(await File.ReadAllTextAsync(manifest), Json) ?? [];
        var maxWer = Number(arg("--max-wer"), 0.2);
        var cases = new List<EvalCaseRecord>();
        var attempts = new List<EvalResult>();
        var sb = new StringBuilder("# Speech recognition on real recordings\n\n| File | Language | WER (hinted) | WER (auto) | Detected | Latency (ms) | Heard (hinted) |\n|---|---|---|---|---|---|---|\n");
        var edits = new Dictionary<string, (int Edits, int Words)>();
        int detected = 0, total = 0;
        foreach (var it in items)
        {
            var path = Path.Combine(dir, it.File);
            if (!File.Exists(path)) { Console.WriteLine($"missing: {it.File}"); continue; }
            var hinted = await TranscribeAsync(http, path, it.Language);
            var auto = await TranscribeAsync(http, path, null);
            var wer = Wer.Of(it.Text, hinted.Text);
            var autoWer = Wer.Of(it.Text, auto.Text);
            var langOk = string.Equals(auto.Language, it.Language, StringComparison.OrdinalIgnoreCase);
            total++;
            if (langOk) detected++;
            var e = edits.GetValueOrDefault(it.Language);
            var refWords = Wer.Words(it.Text);
            edits[it.Language] = (e.Edits + Wer.Edits(refWords, Wer.Words(hinted.Text)), e.Words + refWords.Length);
            Console.WriteLine($"{it.File}: WER {wer:P0} (auto {autoWer:P0}, detected {auto.Language ?? "-"}) {hinted.Ms} ms");
            sb.AppendLine($"| {it.File} | {it.Language} | {wer:P0} | {autoWer:P0} | {(langOk ? "yes" : $"no ({auto.Language})")} | {hinted.Ms} | {hinted.Text.ReplaceLineEndings(" ")} |");
            var failures = new List<string>();
            if (hinted.Error is not null) failures.Add(hinted.Error);
            if (wer > maxWer) failures.Add($"WER {wer:P0} above {maxWer:P0}: heard \"{hinted.Text}\"");
            if (!langOk) failures.Add($"language detected as {auto.Language ?? "nothing"}");
            var c = new EvalCase(it.File, it.Text);
            var checks = new Dictionary<string, bool> { ["wer"] = wer <= maxWer, ["language"] = langOk };
            attempts.Add(new EvalResult(c, failures.Count == 0, failures, new RunOutcome(hinted.Error is null ? "Completed" : "Failed", hinted.Text, hinted.Error, [], hinted.Ms, 0, hinted.Ms), checks));
        }
        if (total == 0)
        {
            Console.Error.WriteLine("No recordings found.");
            return 2;
        }

        var set = new EvalDataset("voice-stt", 1, null, attempts.Select(a => a.Case).ToList());
        var record = EvalHistory.Build(set, attempts, DateTimeOffset.UtcNow, http.BaseAddress?.ToString() ?? "", 1, 1.0, arg("--label"));
        var metrics = new Dictionary<string, double>(record.Summary.Metrics ?? new Dictionary<string, double>());
        foreach (var (lang, (n, words)) in edits) metrics["wer_" + lang] = words == 0 ? 0 : Math.Round((double)n / words, 4);
        record = record with { Summary = record.Summary with { Metrics = metrics } };
        var previous = EvalHistory.Load(historyRoot, set.Name).LastOrDefault();
        if (saveHistory) Console.WriteLine($"stored {EvalHistory.Save(historyRoot, record)}");

        sb.AppendLine().AppendLine("Corpus WER (hinted): " + string.Join(" · ", edits.Select(x => $"{x.Key} {(double)x.Value.Edits / Math.Max(1, x.Value.Words):P1} over {x.Value.Words} words")) +
                                   $" · language detected right {detected}/{total} · latency p50 {record.Summary.P50Ms} ms, p95 {record.Summary.P95Ms} ms");
        if (previous?.Summary.Metrics is { } before)
            foreach (var (k, v) in metrics.Where(m => m.Key.StartsWith("wer_", StringComparison.Ordinal) && before.ContainsKey(m.Key)))
                sb.AppendLine($"- {k}: {before[k]:P1} → {v:P1} since {previous.StartedAt:u}");
        var report = sb.ToString();
        await File.WriteAllTextAsync(arg("--report") ?? "evals/report-voice-stt.md", report);
        Console.WriteLine();
        Console.WriteLine(report);
        var corpus = edits.Values.Sum(x => x.Edits) / (double)Math.Max(1, edits.Values.Sum(x => x.Words));
        if (corpus > maxWer) Console.Error.WriteLine($"GATE: corpus WER {corpus:P1} is above {maxWer:P0}");
        return corpus <= maxWer ? 0 : 1;
    }

    private sealed record Heard(string Text, string? Language, long Ms, string? Error);

    private static async Task<Heard> TranscribeAsync(HttpClient http, string path, string? language)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(await File.ReadAllBytesAsync(path));
        file.Headers.ContentType = new MediaTypeHeaderValue(Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".wav" => "audio/wav", ".mp3" => "audio/mpeg", ".m4a" => "audio/mp4", ".webm" => "audio/webm", ".ogg" => "audio/ogg", _ => "application/octet-stream",
        });
        form.Add(file, "audio", Path.GetFileName(path));
        if (language is not null) form.Add(new StringContent(language), "language");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var res = await http.PostAsync("/voice/transcribe", form);
        sw.Stop();
        if (!res.IsSuccessStatusCode) return new("", null, sw.ElapsedMilliseconds, $"transcribe answered {(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        return new(body.GetProperty("text").GetString() ?? "", body.TryGetProperty("language", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null,
            sw.ElapsedMilliseconds, null);
    }

    public static async Task<int> LatencyAsync(HttpClient http, Func<string, string?> arg, string historyRoot, bool saveHistory)
    {
        var days = (int)Number(arg("--days"), 7);
        var budget = VoiceLatency.ParseBudget(await File.ReadAllTextAsync(arg("--budget") ?? "evals/voice/budget.json"));
        var from = DateTimeOffset.UtcNow.AddDays(-days).ToString("o", CultureInfo.InvariantCulture);
        var list = await http.GetFromJsonAsync<JsonElement>($"/conversations?from={Uri.EscapeDataString(from)}&limit=500" + (arg("--user") is { } user ? $"&user={Uri.EscapeDataString(user)}" : ""));
        var timings = new List<VoiceTurnTiming>();
        foreach (var c in list.GetProperty("conversations").EnumerateArray())
        {
            var detail = await http.GetFromJsonAsync<JsonElement>($"/conversations/{c.GetProperty("id").GetGuid()}");
            foreach (var turn in detail.GetProperty("turns").EnumerateArray())
            {
                if (!(turn.TryGetProperty("voice", out var v) && v.ValueKind == JsonValueKind.True) || turn.GetProperty("status").GetString() != "Completed") continue;
                var events = turn.GetProperty("events").EnumerateArray().Select(e => (
                    e.GetProperty("kind").GetString()!, e.GetProperty("startMs").GetInt64(), e.GetProperty("durationMs").GetInt64(),
                    e.TryGetProperty("firstAudioMs", out var f) && f.ValueKind == JsonValueKind.Number ? f.GetInt64() : (long?)null)).ToList();
                timings.Add(VoiceLatency.FromEvents(events));
            }
        }

        var stages = VoiceLatency.Summarise(timings, budget);
        var sb = new StringBuilder($"# Voice latency budget: last {days} days\n\n{timings.Count} spoken turns.\n\n| Stage | Turns | p50 (ms) | p95 (ms) | Budget p50 / p95 | |\n|---|---|---|---|---|---|\n");
        foreach (var s in stages)
            sb.AppendLine($"| {Describe(s.Name)} | {s.Count} | {s.P50} | {s.P95} | {s.BudgetP50?.ToString() ?? "–"} / {s.BudgetP95?.ToString() ?? "–"} | {(s.Count == 0 ? "no data" : s.WithinBudget ? "ok" : "**over**")} |");
        var report = sb.ToString();
        await File.WriteAllTextAsync(arg("--report") ?? "evals/report-voice-latency.md", report);
        Console.WriteLine(report);

        // Stored like other evals: a stage is a case that passes when within budget, so History trends and regressions work the same way.
        var set = new EvalDataset("voice-latency", 1, null, stages.Select(s => new EvalCase(s.Name, Describe(s.Name))).ToList());
        var results = stages.Select(s => new EvalResult(set.Cases.Single(c => c.Id == s.Name), s.WithinBudget, s.WithinBudget ? [] : [$"p50 {s.P50} / p95 {s.P95} over budget"],
            new RunOutcome("Completed", null, null, [], s.P50, 0, s.P50))).ToList();
        var record = EvalHistory.Build(set, results, DateTimeOffset.UtcNow, http.BaseAddress?.ToString() ?? "", 1, 1.0, arg("--label"));
        var end = stages.Single(s => s.Name == "speechToAnswerAudio");
        record = record with
        {
            Summary = record.Summary with
            {
                P50Ms = end.P50, P95Ms = end.P95,
                Metrics = stages.Where(s => s.Count > 0).SelectMany(s => new[] { ($"{s.Name}_p50", (double)s.P50), ($"{s.Name}_p95", (double)s.P95) }).ToDictionary(x => x.Item1, x => x.Item2),
            },
        };
        if (saveHistory && timings.Count > 0) Console.WriteLine($"stored {EvalHistory.Save(historyRoot, record)}");
        var over = stages.Where(s => !s.WithinBudget).ToList();
        foreach (var s in over) Console.Error.WriteLine($"GATE: {Describe(s.Name)} p50 {s.P50} / p95 {s.P95} ms is over the budget {s.BudgetP50} / {s.BudgetP95}");
        if (timings.Count == 0) Console.Error.WriteLine("No spoken turns in the period: have a conversation first (Runs page, Start conversation).");
        return timings.Count > 0 && over.Count == 0 ? 0 : 1;
    }

    private static string Describe(string stage) => stage switch
    {
        "stt" => "Speech to text (end of speech to transcript)",
        "model" => "Model, all calls of the turn",
        "tools" => "Tools, all calls of the turn",
        "firstAudio" => "Speech output, time to first audio",
        _ => "End of speech to first audio of the answer",
    };

    public static async Task<int> TtsAsync(Func<string, string?> arg)
    {
        var url = arg("--voice-url") ?? Environment.GetEnvironmentVariable("LOTS_VOICE_URL") ?? "http://127.0.0.1:8700/v1";
        var key = arg("--voice-key") ?? Environment.GetEnvironmentVariable("VOICE_API_KEY") ?? KeyFromEnvFile();
        using var http = new HttpClient { BaseAddress = new Uri(url.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(3) };
        if (!string.IsNullOrEmpty(key)) http.DefaultRequestHeaders.Authorization = new("Bearer", key);

        var sentences = JsonSerializer.Deserialize<List<Sentence>>(await File.ReadAllTextAsync(arg("--sentences") ?? "evals/voice/tts-sentences.json"), Json) ?? [];
        // Voices per language: --voices-sv sv-nst,cb-default,<own voice id> and --voices-en en-ljspeech,cb-default.
        var voices = new Dictionary<string, List<string>>
        {
            ["sv"] = Csv(arg("--voices-sv") ?? "sv-nst,cb-default"),
            ["en"] = Csv(arg("--voices-en") ?? "en-ljspeech,cb-default"),
        };
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var outDir = Path.Combine(arg("--out") ?? "evals/voice/listening", stamp);
        Directory.CreateDirectory(Path.Combine(outDir, "clips"));
        var clips = new List<ListeningClip>();
        var n = 0;
        foreach (var s in sentences)
            foreach (var voice in voices.GetValueOrDefault(s.Language) ?? [])
            {
                var code = $"C{++n:00}";
                var sw = System.Diagnostics.Stopwatch.StartNew();
                using var req = new HttpRequestMessage(HttpMethod.Post, "audio/speech") { Content = JsonContent.Create(new { input = s.Text, voice, language = s.Language, response_format = "wav" }) };
                using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                if (!res.IsSuccessStatusCode)
                {
                    Console.WriteLine($"{voice} {s.Id}: {(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
                    n--;
                    continue;
                }
                await using var body = await res.Content.ReadAsStreamAsync();
                using var buffer = new MemoryStream();
                var chunk = new byte[16384];
                long? first = null;
                int read;
                while ((read = await body.ReadAsync(chunk)) > 0)
                {
                    if (first is null && buffer.Length + read > 44) first = sw.ElapsedMilliseconds; // the first audio after the WAV header
                    buffer.Write(chunk, 0, read);
                }
                var totalMs = sw.ElapsedMilliseconds;
                var wav = WavFix.WithSizes(buffer.ToArray()); // a streamed WAV has no final sizes in its header; players want them
                var clipPath = Path.Combine(outDir, "clips", code + ".wav");
                await File.WriteAllBytesAsync(clipPath, wav);
                var heard = await RoundTripAsync(http, clipPath, s.Language);
                var fallback = res.Headers.TryGetValues("X-Voice-Fallback", out var fb) ? fb.FirstOrDefault() : null;
                clips.Add(new ListeningClip(code, voice, s.Language, s.Id, s.Text, first ?? totalMs, totalMs, heard, heard is null ? null : Wer.Of(s.Text, heard), fallback));
                Console.WriteLine($"{code} {voice} {s.Id}: first audio {first ?? totalMs} ms, total {totalMs} ms" + (heard is null ? "" : $", heard \"{heard}\""));
            }

        await File.WriteAllTextAsync(Path.Combine(outDir, "key.json"), JsonSerializer.Serialize(clips, Json));
        await File.WriteAllTextAsync(Path.Combine(outDir, "sheet.html"), Listening.Sheet(clips, stamp.GetHashCode()));
        var report = Listening.Report(clips, []);
        await File.WriteAllTextAsync(Path.Combine(outDir, "report.md"), report);
        Console.WriteLine();
        Console.WriteLine(report);
        Console.WriteLine($"Listening test ready: open {Path.Combine(outDir, "sheet.html")}, rate, download ratings.csv into that folder, then run --mode voice-tts-score --dir {outDir}");
        return clips.Count > 0 ? 0 : 1;
    }

    public static async Task<int> TtsScoreAsync(Func<string, string?> arg)
    {
        var dir = arg("--dir") ?? throw new InvalidOperationException("--dir <listening folder> is required");
        var clips = JsonSerializer.Deserialize<List<ListeningClip>>(await File.ReadAllTextAsync(Path.Combine(dir, "key.json")), Json) ?? [];
        var ratingsPath = arg("--ratings") ?? Path.Combine(dir, "ratings.csv");
        var ratings = File.Exists(ratingsPath) ? Listening.ParseRatings(await File.ReadAllTextAsync(ratingsPath)) : [];
        var report = Listening.Report(clips, ratings);
        await File.WriteAllTextAsync(Path.Combine(dir, "report.md"), report);
        Console.WriteLine(report);
        return ratings.Count > 0 ? 0 : 1;
    }

    private static async Task<string?> RoundTripAsync(HttpClient voice, string wavPath, string language)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(await File.ReadAllBytesAsync(wavPath));
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", Path.GetFileName(wavPath));
        form.Add(new StringContent(language), "language");
        using var res = await voice.PostAsync("audio/transcriptions", form);
        if (!res.IsSuccessStatusCode) return null;
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        return body.TryGetProperty("text", out var t) ? t.GetString()?.Trim() : null;
    }

    private sealed record Sentence(string Id, string Language, string Text, string? Why = null);

    private static List<string> Csv(string s) => s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static double Number(string? s, double fallback) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static string? KeyFromEnvFile()
    {
        var f = Path.Combine("services", "voice", ".env");
        if (!File.Exists(f)) return null;
        return File.ReadAllLines(f).FirstOrDefault(l => l.StartsWith("VOICE_API_KEY=", StringComparison.Ordinal))?["VOICE_API_KEY=".Length..].Trim();
    }
}

/// <summary>A WAV streamed by the voice service carries placeholder sizes (it does not know the length in advance).</summary>
public static class WavFix
{
    public static byte[] WithSizes(byte[] wav)
    {
        if (wav.Length < 44 || Encoding.ASCII.GetString(wav, 0, 4) != "RIFF" || Encoding.ASCII.GetString(wav, 36, 4) != "data") return wav;
        BitConverter.TryWriteBytes(wav.AsSpan(4, 4), wav.Length - 8);
        BitConverter.TryWriteBytes(wav.AsSpan(40, 4), wav.Length - 44);
        return wav;
    }
}
