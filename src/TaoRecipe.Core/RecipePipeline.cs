using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TaoRecipe.Core;

/// <summary>One finished recipe. <see cref="Folder"/> is null when the local copy was removed after publishing.</summary>
public sealed record RecipeResult(Recipe Recipe, string? Folder, string? NotionUrl);

public sealed class PipelineOptions
{
    public bool PublishToNotion { get; init; } = true;
    /// <summary>Ignore cached transcript/extraction from an earlier failed run.</summary>
    public bool Fresh { get; init; }
}

/// <summary>Video in, recipes out: download → transcript → frames → Claude → screen grabs → local copy → Notion.</summary>
public sealed class RecipePipeline(AppSettings settings)
{
    private static readonly JsonSerializerOptions JsonOut = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public async Task<List<RecipeResult>> ProcessAsync(string input, PipelineOptions options, IProgress<string> log, CancellationToken ct)
    {
        Directory.CreateDirectory(settings.OutputFolder);
        var workDir = Path.Combine(settings.OutputFolder, "_work", Hash(input.Trim()));
        if (options.Fresh && Directory.Exists(workDir)) Directory.Delete(workDir, true);
        Directory.CreateDirectory(workDir);

        // 1. video
        var video = await new VideoAcquirer(settings).AcquireAsync(input, workDir, log, ct).ConfigureAwait(false);
        var frameTools = new FrameTools(settings);
        if (video.DurationSeconds <= 0) video.DurationSeconds = await frameTools.GetDurationAsync(video.MediaPath, ct).ConfigureAwait(false);
        log.Report($"Video: {(video.Title.Length > 0 ? video.Title : Path.GetFileName(video.MediaPath))} ({Format.Clock(video.DurationSeconds)})");

        // 2. transcript (cached per input)
        var transcriptCache = Path.Combine(workDir, "transcript.json");
        Transcript transcript;
        if (File.Exists(transcriptCache))
        {
            transcript = LoadTranscript(transcriptCache);
            log.Report($"Reusing transcript from an earlier run ({transcript.Segments.Count} segments).");
        }
        else
        {
            transcript = await new Transcriber(settings).TranscribeAsync(video, workDir, log, ct).ConfigureAwait(false);
            SaveTranscript(transcript, transcriptCache);
        }

        // 3. Claude (cached per input + language)
        var extractionCache = Path.Combine(workDir, $"extraction.{Format.SafeFileName(settings.TargetLanguage)}.json");
        RecipeExtraction extraction;
        if (File.Exists(extractionCache))
        {
            extraction = JsonSerializer.Deserialize<RecipeExtraction>(await File.ReadAllTextAsync(extractionCache, ct).ConfigureAwait(false))!;
            log.Report("Reusing Claude's recipe from an earlier run.");
        }
        else
        {
            var frames = await frameTools.SampleAsync(video.MediaPath, workDir, log, ct).ConfigureAwait(false);
            extraction = await new RecipeExtractor(settings).ExtractAsync(video, transcript, frames, workDir, log, ct).ConfigureAwait(false);
            await File.WriteAllTextAsync(extractionCache, JsonSerializer.Serialize(extraction, JsonOut), ct).ConfigureAwait(false);
        }
        if (extraction.Recipes.Count == 0)
            throw new InvalidOperationException("No recipe found in this video." + (extraction.Notes.Length > 0 ? " " + extraction.Notes : ""));
        log.Report($"Found {extraction.Recipes.Count} recipe(s): {string.Join(", ", extraction.Recipes.Select(r => r.Title))}");

        // 4. screen grabs + local copy
        var results = new List<RecipeResult>();
        foreach (var recipe in extraction.Recipes)
        {
            var folder = UniqueFolder(Path.Combine(settings.OutputFolder, Format.SafeFileName(recipe.Title.Length > 0 ? recipe.Title : video.Title)));
            Directory.CreateDirectory(folder);
            log.Report($"Grabbing step images for '{recipe.Title}'…");
            // the finished dish is nearly always shown at the end; use that when Claude gives no cover time
            // exact frame Claude looked at, so its dish position matches; without one, the end of the video
            bool haveCover = recipe.CoverFrame > 0 && recipe.CoverTimestampSeconds >= 0; // 0:00 is valid: many videos open on the finished dish
            var coverTime = haveCover ? recipe.CoverTimestampSeconds : Math.Max(0, video.DurationSeconds - 20);
            recipe.CoverImage = await frameTools.GrabBestAsync(video.MediaPath, coverTime, video.DurationSeconds, Path.Combine(folder, "cover.jpg"), ct, exact: haveCover).ConfigureAwait(false);
            recipe.IconImage = await frameTools.MakeIconAsync(recipe.CoverImage, recipe.DishCenterX, recipe.DishCenterY, recipe.DishSize,
                Path.Combine(folder, "icon.jpg"), ct).ConfigureAwait(false);
            for (int i = 0; i < recipe.Steps.Count; i++)
            {
                var s = recipe.Steps[i];
                if (!s.HasVisual) continue;
                s.Image = await frameTools.GrabBestAsync(video.MediaPath, s.TimestampSeconds, video.DurationSeconds,
                    Path.Combine(folder, $"step{i + 1:00}.jpg"), ct).ConfigureAwait(false);
            }
            await File.WriteAllTextAsync(Path.Combine(folder, "recipe.json"), JsonSerializer.Serialize(recipe, JsonOut), ct).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(folder, "recipe.md"), ToMarkdown(recipe, video), ct).ConfigureAwait(false);
            results.Add(new RecipeResult(recipe, folder, null));
        }

        // 5. Notion
        if (options.PublishToNotion)
        {
            using var notion = new NotionPublisher(settings);
            for (int i = 0; i < results.Count; i++)
            {
                var url = await notion.PublishAsync(results[i].Recipe, video, extraction.SourceLanguage, log, ct).ConfigureAwait(false);
                log.Report($"Notion: {url}");
                results[i] = results[i] with { NotionUrl = url };
                if (settings.KeepLocalCopy && results[i].Folder is { } dir)
                    await File.WriteAllTextAsync(Path.Combine(dir, "notion.url"), $"[InternetShortcut]\r\nURL={url}\r\n", ct).ConfigureAwait(false);
            }
        }

        // 6. clean up (a failed run keeps the work folder so a retry is cheap)
        bool deleteLocal = options.PublishToNotion && !settings.KeepLocalCopy;
        if (video.DownloadedByUs && settings.KeepVideo)
        {
            var name = deleteLocal ? Format.SafeFileName(results[0].Recipe.Title.Length > 0 ? results[0].Recipe.Title : video.Title) : "video";
            var keep = Path.Combine(deleteLocal ? settings.OutputFolder : results[0].Folder ?? settings.OutputFolder, name + Path.GetExtension(video.MediaPath));
            if (!File.Exists(keep)) File.Move(video.MediaPath, keep);
        }
        if (deleteLocal)
        {
            for (int i = 0; i < results.Count; i++)
            {
                TryDeleteDirectory(results[i].Folder);
                results[i] = results[i] with { Folder = null };
            }
            log.Report("Local copy removed (it is in Notion now).");
        }
        TryDeleteDirectory(workDir);
        return results;
    }

    private static void TryDeleteDirectory(string? path)
    {
        if (path == null || !Directory.Exists(path)) return;
        try { Directory.Delete(path, true); }
        catch (IOException) { /* still in use; harmless */ }
        catch (UnauthorizedAccessException) { /* read-only or locked; harmless */ }
    }

    private static string UniqueFolder(string path)
    {
        if (!Directory.Exists(path)) return path;
        for (int i = 2; ; i++)
            if (!Directory.Exists($"{path} ({i})")) return $"{path} ({i})";
    }

    private static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..16].ToLowerInvariant();

    private sealed record TranscriptDto(string? Language, string Origin, List<TranscriptSegment> Segments);

    private static void SaveTranscript(Transcript t, string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(new TranscriptDto(t.Language, t.Origin, t.Segments), JsonOut));

    private static Transcript LoadTranscript(string path)
    {
        var dto = JsonSerializer.Deserialize<TranscriptDto>(File.ReadAllText(path))!;
        var t = new Transcript { Language = dto.Language, Origin = dto.Origin };
        t.Segments.AddRange(dto.Segments);
        return t;
    }

    public static string ToMarkdown(Recipe r, VideoSource video)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {r.Title}");
        if (r.OriginalTitle.Length > 0) sb.AppendLine($"*{r.OriginalTitle}*");
        sb.AppendLine();
        if (r.CoverImage != null) sb.AppendLine($"![{r.Title}]({Path.GetFileName(r.CoverImage)})").AppendLine();
        if (r.Summary.Length > 0) sb.AppendLine(r.Summary).AppendLine();
        var meta = new[] { ("Serves", r.Servings), ("Prep", r.PrepTime), ("Cook", r.CookTime), ("Total", r.TotalTime), ("Cuisine", r.Cuisine), ("Course", r.Course) }
            .Where(m => m.Item2.Length > 0).Select(m => $"**{m.Item1}:** {m.Item2}");
        var metaLine = string.Join(" · ", meta);
        if (metaLine.Length > 0) sb.AppendLine(metaLine).AppendLine();
        if (video.SourceUrl != null) sb.AppendLine($"Source: <{video.SourceUrl}>").AppendLine();

        sb.AppendLine("## Ingredients");
        foreach (var g in r.IngredientGroups)
        {
            if (g.Name.Length > 0 && r.IngredientGroups.Count > 1) sb.AppendLine().AppendLine($"### {g.Name}");
            foreach (var i in g.Ingredients) sb.AppendLine($"- [ ] {i}");
        }
        if (r.Equipment.Count > 0)
        {
            sb.AppendLine().AppendLine("## Equipment");
            foreach (var e in r.Equipment) sb.AppendLine($"- {e}");
        }
        sb.AppendLine().AppendLine("## Method");
        for (int n = 0; n < r.Steps.Count; n++)
        {
            var s = r.Steps[n];
            sb.AppendLine($"{n + 1}. **{s.Title}.** {s.Instruction} _({Format.Clock(s.TimestampSeconds)})_");
            if (s.Image != null) sb.AppendLine().AppendLine($"   ![Step {n + 1}]({Path.GetFileName(s.Image)})").AppendLine();
        }
        if (r.Tips.Count > 0)
        {
            sb.AppendLine().AppendLine("## Tips");
            foreach (var t in r.Tips) sb.AppendLine($"- {t}");
        }
        return sb.ToString();
    }
}
