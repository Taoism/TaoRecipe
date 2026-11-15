using System.Globalization;
using System.Text.RegularExpressions;

namespace TaoRecipe.Core;

/// <summary>ffmpeg-based frame sampling (for Claude to look at) and best-frame screen grabs (for the recipe).</summary>
public sealed partial class FrameTools(AppSettings settings)
{
    private string FfprobePath
    {
        get
        {
            var ff = settings.FfmpegPath;
            var dir = Path.GetDirectoryName(ff);
            var ext = Path.GetExtension(ff);
            return string.IsNullOrEmpty(dir) ? "ffprobe" + ext : Path.Combine(dir, "ffprobe" + ext);
        }
    }

    public async Task<double> GetDurationAsync(string media, CancellationToken ct)
    {
        var res = await ProcessRunner.RunAsync(FfprobePath,
            ["-v", "error", "-show_entries", "format=duration", "-of", "default=nw=1:nk=1", media], ct).ConfigureAwait(false);
        return double.TryParse(res.StdOut.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
    }

    [GeneratedRegex(@"pts_time:\s*([0-9.]+)")]
    private static partial Regex PtsTimeRegex();

    /// <summary>Samples frames on scene changes (at least every 20 s, at most every 3 s), scaled to 640 px wide.</summary>
    public async Task<List<SampledFrame>> SampleAsync(string media, string workDir, IProgress<string> log, CancellationToken ct)
    {
        var dir = Path.Combine(workDir, "samples");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        log.Report("Sampling frames (scene changes)…");

        var times = new List<double>();
        const string filter = @"fps=2,scale=640:-2,select=isnan(prev_selected_t)+gte(t-prev_selected_t\,20)+gt(scene\,0.3)*gte(t-prev_selected_t\,3),showinfo";
        var res = await ProcessRunner.RunAsync(settings.FfmpegPath,
            ["-y", "-hide_banner", "-hwaccel", "auto", "-i", media, "-an", "-sn", "-vf", filter, "-fps_mode", "vfr", "-q:v", "4",
             Path.Combine(dir, "%05d.jpg")], ct,
            onStdErrLine: l =>
            {
                if (!l.Contains("Parsed_showinfo", StringComparison.Ordinal)) return;
                var m = PtsTimeRegex().Match(l);
                if (m.Success) times.Add(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
            }).ConfigureAwait(false);
        if (res.ExitCode != 0)
            throw new InvalidOperationException("ffmpeg frame sampling failed:\n" + VideoAcquirer.Tail(res.StdErr, 8));

        var files = Directory.GetFiles(dir, "*.jpg").Order(StringComparer.Ordinal).ToList();
        var count = Math.Min(files.Count, times.Count);
        var frames = new List<SampledFrame>(count);
        for (int i = 0; i < count; i++) frames.Add(new SampledFrame(0, times[i], files[i]));

        if (frames.Count > settings.MaxFrames)
        {
            var step = (double)frames.Count / settings.MaxFrames;
            frames = Enumerable.Range(0, settings.MaxFrames).Select(i => frames[(int)(i * step)]).ToList();
        }
        frames = frames.Select((f, i) => f with { Index = i + 1 }).ToList();
        log.Report($"Sampled {frames.Count} frames.");
        return frames;
    }

    /// <summary>Grabs the sharpest full-resolution frame near <paramref name="time"/> and writes it as JPEG.</summary>
    public async Task<string> GrabBestAsync(string media, double time, double duration, string outPath, CancellationToken ct, bool exact = false)
    {
        double[] offsets = exact ? [0] : [0, -0.6, 0.6, -1.2, 1.2];
        double best = time, bestScore = double.MinValue;
        foreach (var off in offsets)
        {
            var t = Math.Clamp(time + off, 0, Math.Max(0, duration - 0.2));
            var score = offsets.Length == 1 ? 0 : await SharpnessAsync(media, t, ct).ConfigureAwait(false);
            if (score > bestScore) { bestScore = score; best = t; }
        }
        var res = await ProcessRunner.RunAsync(settings.FfmpegPath,
            ["-y", "-v", "error", "-ss", Sec(best), "-i", media, "-frames:v", "1", "-vf", "scale='min(1600,iw)':-2", "-q:v", "3", outPath], ct).ConfigureAwait(false);
        if (res.ExitCode != 0 || !File.Exists(outPath))
            throw new InvalidOperationException($"ffmpeg could not grab frame at {Format.Clock(time)}:\n" + VideoAcquirer.Tail(res.StdErr, 5));
        return outPath;
    }

    /// <summary>
    /// Crops a square around the dish (centre and size as fractions of the frame) out of the cover image and scales it to
    /// a 256 px page icon. Out-of-range values fall back to the frame centre / 80 % of the height.
    /// </summary>
    public async Task<string> MakeIconAsync(string coverPath, double centerX, double centerY, double size, string outPath, CancellationToken ct)
    {
        static double Valid(double v, double fallback) => v > 0 && v <= 1 ? v : fallback;
        var cx = Sec(Valid(centerX, 0.5));
        var cy = Sec(Valid(centerY, 0.5));
        // a little margin around the dish; never smaller than 35 % of the height, never larger than the frame
        var s = Sec(Math.Clamp(Valid(size, 0.8) * 1.15, 0.35, 1.0));
        var crop = $@"crop=w=min(min(iw\,ih)\,{s}*ih):h=min(min(iw\,ih)\,{s}*ih):x=max(0\,min(iw-ow\,{cx}*iw-ow/2)):y=max(0\,min(ih-oh\,{cy}*ih-oh/2)),scale=256:256";
        var res = await ProcessRunner.RunAsync(settings.FfmpegPath,
            ["-y", "-v", "error", "-i", coverPath, "-vf", crop, "-q:v", "2", outPath], ct).ConfigureAwait(false);
        if (res.ExitCode != 0 || !File.Exists(outPath))
            throw new InvalidOperationException("ffmpeg could not make the recipe icon:\n" + VideoAcquirer.Tail(res.StdErr, 5));
        return outPath;
    }

    private const int W = 256, H = 144;

    /// <summary>Variance of the Laplacian on a small greyscale copy: higher = sharper, low for blur and black frames.</summary>
    private async Task<double> SharpnessAsync(string media, double t, CancellationToken ct)
    {
        var px = await ProcessRunner.RunBytesAsync(settings.FfmpegPath,
            ["-v", "error", "-ss", Sec(t), "-i", media, "-frames:v", "1", "-vf", $"scale={W}:{H},format=gray", "-f", "rawvideo", "-"], ct).ConfigureAwait(false);
        if (px.Length < W * H) return double.MinValue;
        double sum = 0, sumSq = 0;
        int n = 0;
        for (int y = 1; y < H - 1; y++)
        {
            for (int x = 1; x < W - 1; x++)
            {
                int i = y * W + x;
                double lap = px[i - 1] + px[i + 1] + px[i - W] + px[i + W] - 4.0 * px[i];
                sum += lap; sumSq += lap * lap; n++;
            }
        }
        var mean = sum / n;
        return sumSq / n - mean * mean;
    }

    private static string Sec(double t) => t.ToString("0.###", CultureInfo.InvariantCulture);
}
