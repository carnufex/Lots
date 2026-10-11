using System.Text.Json;
using Lots.Shell.Core.Meetings;
using Lots.Shell.Core.Speech;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace Lots.Shell.Tests.Features;

/// <summary>
/// Opt-in, against a running voice service (#41, #44): every <c>*.wav</c> with a <c>*-truth.json</c> next to it in LOTS_TEST_MEETINGS
/// is transcribed and diarized, merged with and without word timestamps, and scored by speaker error. Run with
/// <c>LOTS_TEST_VOICE_URL=http://localhost:8700/v1 LOTS_TEST_VOICE_KEY=... LOTS_TEST_MEETINGS=dir dotnet test --filter MeetingLive</c>.
/// </summary>
public class MeetingLiveTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Live_speaker_error_with_and_without_word_timestamps()
    {
        var url = Environment.GetEnvironmentVariable("LOTS_TEST_VOICE_URL");
        var dir = Environment.GetEnvironmentVariable("LOTS_TEST_MEETINGS");
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(dir)) return;
        var http = new HttpClient { BaseAddress = new Uri(url.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(10) };
        if (Environment.GetEnvironmentVariable("LOTS_TEST_VOICE_KEY") is { Length: > 0 } key) http.DefaultRequestHeaders.Authorization = new("Bearer", key);
        var transcriber = new VoiceMeetingTranscriber(http, Options.Create(new SpeechOptions()));

        foreach (var wav in Directory.GetFiles(dir, "*.wav").Order())
        {
            var truthFile = Path.ChangeExtension(wav, null) + "-truth.json";
            if (!File.Exists(truthFile)) continue;
            var truth = JsonDocument.Parse(await File.ReadAllTextAsync(truthFile)).RootElement.EnumerateArray()
                .Select(t => (t.GetProperty("start").GetDouble(), t.GetProperty("end").GetDouble(), t.GetProperty("speaker").GetString()!)).ToList();
            await using var audio = File.OpenRead(wav);
            var analysis = await transcriber.AnalyseAsync(audio, Path.GetFileName(wav), "audio/wav", null, null, null, words: true, default);
            var bySegment = MeetingMerge.SpeakerError(MeetingMerge.Merge(analysis, useWords: false), truth);
            var byWord = MeetingMerge.SpeakerError(MeetingMerge.Merge(analysis, useWords: true), truth);
            output.WriteLine($"{Path.GetFileName(wav)}: {analysis.Turns.Select(t => t.Speaker).Distinct().Count()} speakers found, " +
                             $"speaker error by segment {bySegment:P1}, by word {byWord:P1}");
            Assert.True(byWord <= bySegment + 0.02, "word timestamps should not make speaker attribution worse");
        }
    }
}
