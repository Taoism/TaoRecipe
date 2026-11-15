namespace TaoRecipe.Core;

/// <summary>A video ready to process: local media file plus whatever metadata the source provided.</summary>
public sealed class VideoSource
{
    public required string Input { get; init; }
    public required string MediaPath { get; init; }
    public bool IsLocal { get; init; }
    public string? SourceUrl { get; init; }
    public string Title { get; init; } = "";
    public string Uploader { get; init; } = "";
    public string Description { get; init; } = "";
    public string? Language { get; init; }
    public double DurationSeconds { get; set; }
    /// <summary>Path to a subtitle file (vtt/srt) written by the uploader, if one was downloaded.</summary>
    public string? SubtitlePath { get; init; }
    public bool DownloadedByUs { get; init; }
}

public sealed record TranscriptSegment(double Start, double End, string Text);

public sealed class Transcript
{
    public List<TranscriptSegment> Segments { get; } = [];
    public string? Language { get; set; }
    public string Origin { get; set; } = "";

    public string ToTimestampedText()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var s in Segments)
            sb.Append('[').Append(Format.Clock(s.Start)).Append("] ").AppendLine(s.Text);
        return sb.ToString();
    }
}

/// <summary>A low-resolution frame sampled for Claude to look at.</summary>
public sealed record SampledFrame(int Index, double Time, string Path);

public static class Format
{
    public static string Clock(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
    }

    public static string SafeFileName(string name, int max = 80)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var s = new string(chars).Trim().Trim('.');
        if (s.Length > max) s = s[..max].Trim();
        return s.Length == 0 ? "recipe" : s;
    }
}
