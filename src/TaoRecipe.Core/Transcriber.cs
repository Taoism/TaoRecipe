using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace TaoRecipe.Core;

/// <summary>Produces a timestamped transcript: uploader subtitles when available, otherwise local Whisper.</summary>
public sealed partial class Transcriber(AppSettings settings)
{
    public async Task<Transcript> TranscribeAsync(VideoSource video, string workDir, IProgress<string> log, CancellationToken ct)
    {
        if (video.SubtitlePath != null && settings.PreferSubtitles)
        {
            var subs = ParseSubtitles(await File.ReadAllTextAsync(video.SubtitlePath, ct).ConfigureAwait(false));
            if (subs.Segments.Count > 5)
            {
                subs.Language = video.Language;
                subs.Origin = "uploader subtitles";
                log.Report($"Using subtitles ({subs.Segments.Count} cues).");
                return subs;
            }
        }
        return await WhisperAsync(video, workDir, log, ct).ConfigureAwait(false);
    }

    private async Task<Transcript> WhisperAsync(VideoSource video, string workDir, IProgress<string> log, CancellationToken ct)
    {
        var wav = Path.Combine(workDir, "audio16k.wav");
        log.Report("Extracting audio…");
        var res = await ProcessRunner.RunAsync(settings.FfmpegPath,
            ["-y", "-v", "error", "-i", video.MediaPath, "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", wav], ct).ConfigureAwait(false);
        if (res.ExitCode != 0 || !File.Exists(wav))
            throw new InvalidOperationException("ffmpeg could not extract audio:\n" + VideoAcquirer.Tail(res.StdErr, 8));

        var modelPath = await EnsureModelAsync(settings.WhisperModel, log, ct).ConfigureAwait(false);
        log.Report($"Transcribing with Whisper ({settings.WhisperModel})…");

        // CUDA needs the CUDA 13 runtime (cublas64_13.dll); Vulkan works with a plain GPU driver; CPU is the last resort.
        RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Cuda, RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu];
        using var factory = WhisperFactory.FromPath(modelPath);
        log.Report($"Whisper runtime: {RuntimeOptions.LoadedLibrary?.ToString() ?? "unknown"}");
        using var processor = factory.CreateBuilder()
            .WithLanguage("auto")
            .Build();

        var transcript = new Transcript { Origin = "Whisper " + settings.WhisperModel };
        double lastReport = 0;
        await using (var fs = File.OpenRead(wav))
        {
            await foreach (var seg in processor.ProcessAsync(fs, ct).ConfigureAwait(false))
            {
                var text = seg.Text.Trim();
                if (text.Length == 0) continue;
                transcript.Language ??= seg.Language;
                transcript.Segments.Add(new TranscriptSegment(seg.Start.TotalSeconds, seg.End.TotalSeconds, text));
                if (seg.End.TotalSeconds - lastReport >= 120)
                {
                    lastReport = seg.End.TotalSeconds;
                    log.Report($"  transcribed {Format.Clock(lastReport)}" +
                        (video.DurationSeconds > 0 ? $" / {Format.Clock(video.DurationSeconds)}" : ""));
                }
            }
        }
        TryDelete(wav);
        log.Report($"Transcript: {transcript.Segments.Count} segments, language '{transcript.Language ?? "?"}'.");
        return transcript;
    }

    /// <summary>whisper.cpp model files on Hugging Face (large models quantised to keep VRAM/RAM use reasonable).</summary>
    private static string ModelFileName(string model) => model.ToLowerInvariant() switch
    {
        "largev3" => "ggml-large-v3-q5_0.bin",
        "medium" => "ggml-medium.bin",
        "small" => "ggml-small.bin",
        "base" => "ggml-base.bin",
        _ => "ggml-large-v3-turbo-q8_0.bin",
    };

    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private static async Task<string> EnsureModelAsync(string model, IProgress<string> log, CancellationToken ct)
    {
        var file = ModelFileName(model);
        Directory.CreateDirectory(AppSettings.ModelsFolder);
        var path = Path.Combine(AppSettings.ModelsFolder, file);
        if (File.Exists(path) && new FileInfo(path).Length > 1_000_000) return path;

        var url = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/" + file;
        log.Report($"Downloading Whisper model {file} (one-time)…");
        var tmp = path + ".part";
        using (var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? 0;
            await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dst = File.Create(tmp);
            var buffer = new byte[1 << 20];
            long done = 0, nextReport = 100L << 20;
            int n;
            while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                done += n;
                if (done >= nextReport)
                {
                    nextReport += 100L << 20;
                    log.Report(total > 0 ? $"  {done >> 20} / {total >> 20} MB" : $"  {done >> 20} MB");
                }
            }
        }
        File.Move(tmp, path, overwrite: true);
        return path;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { /* in use, leave it */ }
    }

    // ---- subtitle parsing (WebVTT / SRT) ----

    [GeneratedRegex(@"(?:(\d+):)?(\d{1,2}):(\d{2})[.,](\d{1,3})\s*-->\s*(?:(\d+):)?(\d{1,2}):(\d{2})[.,](\d{1,3})")]
    private static partial Regex CueTimeRegex();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagRegex();

    public static Transcript ParseSubtitles(string text)
    {
        var t = new Transcript();
        var lines = text.Replace("\r", "").Split('\n');
        string? lastText = null;
        for (int i = 0; i < lines.Length; i++)
        {
            var m = CueTimeRegex().Match(lines[i]);
            if (!m.Success) continue;
            double start = ToSeconds(m, 1), end = ToSeconds(m, 5);
            var body = new List<string>();
            while (i + 1 < lines.Length && lines[i + 1].Trim().Length > 0) body.Add(lines[++i]);
            var cue = WebUtility.HtmlDecode(TagRegex().Replace(string.Join(' ', body), "")).Trim();
            if (cue.Length == 0 || cue == lastText) continue;
            // auto-generated captions repeat the previous line as a prefix; keep only the new part
            if (lastText != null && cue.StartsWith(lastText, StringComparison.Ordinal)) cue = cue[lastText.Length..].Trim();
            if (cue.Length == 0) continue;
            t.Segments.Add(new TranscriptSegment(start, end, cue));
            lastText = cue;
        }
        return t;
    }

    private static double ToSeconds(Match m, int g)
    {
        double h = m.Groups[g].Success ? double.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture) : 0;
        double min = double.Parse(m.Groups[g + 1].Value, CultureInfo.InvariantCulture);
        double s = double.Parse(m.Groups[g + 2].Value, CultureInfo.InvariantCulture);
        double ms = double.Parse(m.Groups[g + 3].Value.PadRight(3, '0'), CultureInfo.InvariantCulture);
        return h * 3600 + min * 60 + s + ms / 1000;
    }
}
