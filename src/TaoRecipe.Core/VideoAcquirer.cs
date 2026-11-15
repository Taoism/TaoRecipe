using System.Text.Json;

namespace TaoRecipe.Core;

/// <summary>Turns user input (URL of any yt-dlp supported site, or a local file path) into a local media file.</summary>
public sealed class VideoAcquirer(AppSettings settings)
{
    public static bool IsUrl(string input) =>
        Uri.TryCreate(input.Trim(), UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    public async Task<VideoSource> AcquireAsync(string input, string workDir, IProgress<string> log, CancellationToken ct)
    {
        input = input.Trim().Trim('"');
        if (!IsUrl(input))
        {
            if (!File.Exists(input)) throw new FileNotFoundException("Video file not found.", input);
            var full = Path.GetFullPath(input);
            return new VideoSource
            {
                Input = input,
                MediaPath = full,
                IsLocal = true,
                Title = Path.GetFileNameWithoutExtension(full),
                SubtitlePath = FindSidecarSubtitle(full),
            };
        }
        return await DownloadAsync(input, workDir, log, ct).ConfigureAwait(false);
    }

    private static string? FindSidecarSubtitle(string mediaPath)
    {
        var dir = Path.GetDirectoryName(mediaPath)!;
        var stem = Path.GetFileNameWithoutExtension(mediaPath);
        foreach (var ext in new[] { ".srt", ".vtt" })
        {
            var match = Directory.EnumerateFiles(dir, stem + "*" + ext).FirstOrDefault();
            if (match != null) return match;
        }
        return null;
    }

    private async Task<VideoSource> DownloadAsync(string url, string workDir, IProgress<string> log, CancellationToken ct)
    {
        Directory.CreateDirectory(workDir);
        log.Report("Downloading video with yt-dlp…");
        var extra = ProcessRunner.SplitArgs(settings.YtDlpExtraArgs);
        var args = new List<string>
        {
            "--no-playlist", "--no-progress", "--newline",
            "-f", "bv*[height<=1080]+ba/b[height<=1080]/bv*+ba/b",
            "--write-info-json",
            "-o", Path.Combine(workDir, "video.%(ext)s"),
            "--print", "after_move:filepath",
        };
        args.AddRange(extra);
        args.Add(url);

        var res = await ProcessRunner.RunAsync(settings.YtDlpPath, args, ct,
            onStdErrLine: l => { if (l.StartsWith("ERROR", StringComparison.Ordinal) || l.StartsWith("WARNING", StringComparison.Ordinal)) log.Report("  " + l); }).ConfigureAwait(false);
        if (res.ExitCode != 0)
            throw new InvalidOperationException("yt-dlp failed:\n" + Tail(res.StdErr, 12));

        var mediaPath = res.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault(File.Exists)
            ?? Directory.EnumerateFiles(workDir, "video.*").FirstOrDefault(f => !f.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("yt-dlp finished but no video file was found.");

        string title = "", uploader = "", description = "";
        string? language = null, webpage = url;
        double duration = 0;
        List<string> manualSubLangs = [];
        var infoPath = Path.Combine(workDir, "video.info.json");
        if (File.Exists(infoPath))
        {
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(infoPath, ct).ConfigureAwait(false));
            var r = doc.RootElement;
            title = Str(r, "title");
            uploader = Str(r, "uploader") is { Length: > 0 } up ? up : Str(r, "channel");
            description = Str(r, "description");
            language = Str(r, "language") is { Length: > 0 } lang ? lang : null;
            webpage = Str(r, "webpage_url") is { Length: > 0 } w ? w : url;
            if (r.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number) duration = d.GetDouble();
            if (r.TryGetProperty("subtitles", out var subs) && subs.ValueKind == JsonValueKind.Object)
                manualSubLangs = subs.EnumerateObject().Select(p => p.Name).Where(n => !n.StartsWith("live_chat", StringComparison.Ordinal)).ToList();
        }

        string? subtitlePath = null;
        if (settings.PreferSubtitles && manualSubLangs.Count > 0)
        {
            var pick = PickSubtitleLanguage(manualSubLangs, language);
            log.Report($"Uploader subtitles found ({string.Join(", ", manualSubLangs)}); using '{pick}'.");
            subtitlePath = await DownloadSubtitleAsync(url, pick, workDir, extra, ct).ConfigureAwait(false);
        }

        return new VideoSource
        {
            Input = url,
            MediaPath = mediaPath,
            SourceUrl = webpage,
            Title = title,
            Uploader = uploader,
            Description = description,
            Language = language,
            DurationSeconds = duration,
            SubtitlePath = subtitlePath,
            DownloadedByUs = true,
        };
    }

    private static string PickSubtitleLanguage(List<string> langs, string? videoLanguage)
    {
        if (videoLanguage != null)
        {
            var exact = langs.FirstOrDefault(l => l.Equals(videoLanguage, StringComparison.OrdinalIgnoreCase))
                ?? langs.FirstOrDefault(l => l.StartsWith(videoLanguage + "-", StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
        }
        return langs[0];
    }

    private async Task<string?> DownloadSubtitleAsync(string url, string lang, string workDir, List<string> extra, CancellationToken ct)
    {
        var args = new List<string>
        {
            "--no-playlist", "--skip-download", "--write-subs", "--sub-langs", lang, "--sub-format", "vtt/srt/best",
            "-o", Path.Combine(workDir, "subs.%(ext)s"),
        };
        args.AddRange(extra);
        args.Add(url);
        var res = await ProcessRunner.RunAsync(settings.YtDlpPath, args, ct).ConfigureAwait(false);
        if (res.ExitCode != 0) return null;
        return Directory.EnumerateFiles(workDir, "subs.*")
            .FirstOrDefault(f => f.EndsWith(".vtt", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".srt", StringComparison.OrdinalIgnoreCase));
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    internal static string Tail(string text, int lines) =>
        string.Join('\n', text.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(lines));
}
