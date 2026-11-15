using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Beta.Messages;

namespace TaoRecipe.Core;

/// <summary>Sends transcript + sampled frames to Claude and gets back structured, translated recipes.</summary>
public sealed class RecipeExtractor(AppSettings settings)
{
    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<RecipeExtraction> ExtractAsync(VideoSource video, Transcript transcript, IReadOnlyList<SampledFrame> frames,
        IProgress<string> log, CancellationToken ct)
    {
        var apiKey = settings.AnthropicApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("No Anthropic API key set (Settings, or the ANTHROPIC_API_KEY environment variable).");

        var client = new AnthropicClient { ApiKey = apiKey };
        var content = BuildContent(video, transcript, frames);

        var parameters = new MessageCreateParams
        {
            Model = settings.Model,
            MaxTokens = 64000,
            System = SystemPrompt(settings.TargetLanguage),
            Thinking = new BetaThinkingConfigAdaptive(),
            OutputConfig = new BetaOutputConfig
            {
                Effort = ParseEffort(settings.Effort),
                Format = new BetaJsonOutputFormat { Schema = Schema() },
            },
            Betas = ["server-side-fallback-2026-07-01"],
            Fallbacks = new Default(),
            Messages = [new BetaUserMessageParam { Content = content }],
        };

        log.Report($"Asking {settings.Model} to write the recipe in {settings.TargetLanguage} ({frames.Count} frames, {transcript.Segments.Count} transcript lines)…");
        var text = new StringBuilder();
        string? stopReason = null;
        long inTokens = 0, outTokens = 0;
        await foreach (var ev in client.Beta.Messages.CreateStreaming(parameters, ct).ConfigureAwait(false))
        {
            if (ev.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var t))
                text.Append(t.Text);
            else if (ev.TryPickStart(out var start))
                inTokens = start.Message.Usage.InputTokens;
            else if (ev.TryPickDelta(out var md))
            {
                stopReason = md.Delta.StopReason?.Raw();
                outTokens = md.Usage.OutputTokens;
            }
        }
        log.Report($"Claude done: {inTokens:N0} input / {outTokens:N0} output tokens.");

        if (stopReason == "refusal") throw new InvalidOperationException("Claude declined to process this video.");
        if (stopReason == "max_tokens") throw new InvalidOperationException("Claude's answer was cut off (max_tokens).");

        var result = JsonSerializer.Deserialize<RecipeExtraction>(text.ToString(), ReadOptions)
            ?? throw new InvalidOperationException("Claude returned no recipe data.");
        return result;
    }

    private static List<BetaContentBlockParam> BuildContent(VideoSource video, Transcript transcript, IReadOnlyList<SampledFrame> frames)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<video>");
        if (video.Title.Length > 0) sb.AppendLine($"Title: {video.Title}");
        if (video.Uploader.Length > 0) sb.AppendLine($"Channel: {video.Uploader}");
        if (video.SourceUrl != null) sb.AppendLine($"URL: {video.SourceUrl}");
        if (video.Language != null) sb.AppendLine($"Declared language: {video.Language}");
        sb.AppendLine($"Duration: {Format.Clock(video.DurationSeconds)}");
        sb.AppendLine("</video>");
        if (video.Description.Length > 0)
        {
            sb.AppendLine("<description>");
            sb.AppendLine(video.Description);
            sb.AppendLine("</description>");
        }
        sb.AppendLine($"<transcript source=\"{transcript.Origin}\" language=\"{transcript.Language ?? "unknown"}\">");
        sb.Append(transcript.ToTimestampedText());
        sb.AppendLine("</transcript>");
        sb.AppendLine($"Below are {frames.Count} frames sampled from the video, each labelled with its timestamp.");

        var blocks = new List<BetaContentBlockParam> { new BetaTextBlockParam { Text = sb.ToString() } };
        foreach (var f in frames)
        {
            blocks.Add(new BetaTextBlockParam { Text = $"Frame {f.Index} at {Format.Clock(f.Time)} ({f.Time:0.0} s):" });
            blocks.Add(new BetaImageBlockParam
            {
                Source = new BetaBase64ImageSource
                {
                    Data = Convert.ToBase64String(File.ReadAllBytes(f.Path)),
                    MediaType = MediaType.ImageJpeg,
                },
            });
        }
        blocks.Add(new BetaTextBlockParam { Text = "Write the recipe(s) from this video now." });
        return blocks;
    }

    private static string SystemPrompt(string language) => $"""
        You turn cooking videos into clear, complete written recipes for a personal recipe collection.

        You get the video's metadata, its description, a timestamped transcript (which may be in any language and
        may contain speech-recognition errors) and frames sampled from the video. Combine all of them: the
        description often lists exact quantities; the frames show ingredients, amounts on screen, textures,
        and the order of work; the transcript explains the method.

        Write every text field in {language}, translating faithfully from the source, including ingredient names,
        steps, tips and the title. Put the original-language title in original_title (empty if it was already
        {language}). Keep the quantities and units as given in the video; if the video gives none, estimate
        sensible ones from what you see and mark them with "approx." in the ingredient note.

        Steps: one step per distinct stage of work, in cooking order, imperative voice, specific (temperatures,
        times, visual cues for doneness). Give each step a short title. For timestamp_seconds pick the moment
        in the video where the step's result is shown most clearly (use the frame timestamps as a guide);
        set has_visual false when nothing useful is visible for that step (e.g. talking head only).
        cover_timestamp_seconds is the best-looking shot of the finished dish.

        If the video contains several separate recipes (e.g. a sauce and a main dish that are each made
        from scratch), return each as its own recipe. Ignore sponsor segments and channel promotion. If the
        video contains no recipe at all, return an empty recipes list and explain why in notes.
        Use empty strings for unknown text fields and empty lists for unknown lists.
        """;

    private static Effort ParseEffort(string effort) => effort.ToLowerInvariant() switch
    {
        "low" => Effort.Low,
        "medium" => Effort.Medium,
        "max" => Effort.Max,
        _ => Effort.High,
    };

    // ---- JSON schema for structured output ----

    private static Dictionary<string, object> Obj(params (string Name, object Schema)[] props) => new()
    {
        ["type"] = "object",
        ["properties"] = props.ToDictionary(p => p.Name, p => p.Schema),
        ["required"] = props.Select(p => p.Name).ToArray(),
        ["additionalProperties"] = false,
    };

    private static Dictionary<string, object> Str(string description) => new() { ["type"] = "string", ["description"] = description };
    private static Dictionary<string, object> Num(string description) => new() { ["type"] = "number", ["description"] = description };
    private static Dictionary<string, object> Arr(object items) => new() { ["type"] = "array", ["items"] = items };

    private static Dictionary<string, JsonElement> Schema()
    {
        var ingredient = Obj(
            ("quantity", Str("Amount, e.g. \"200\", \"1/2\", \"a pinch\"")),
            ("unit", Str("Unit, e.g. g, ml, tbsp, cup")),
            ("name", Str("Ingredient name")),
            ("note", Str("Preparation or remark, e.g. finely chopped")));
        var step = Obj(
            ("title", Str("Short step title")),
            ("instruction", Str("Full instruction for this step")),
            ("timestamp_seconds", Num("Video time where this step is shown best")),
            ("has_visual", new Dictionary<string, object> { ["type"] = "boolean" }));
        var recipe = Obj(
            ("title", Str("Recipe title")),
            ("original_title", Str("Title in the video's language, empty if same as title")),
            ("summary", Str("Two or three sentences describing the dish")),
            ("cuisine", Str("e.g. Italian, Japanese")),
            ("course", Str("e.g. Main, Dessert, Side, Breakfast, Snack, Drink")),
            ("servings", Str("e.g. 4")),
            ("prep_time", Str("e.g. 15 min")),
            ("cook_time", Str("e.g. 40 min")),
            ("total_time", Str("e.g. 55 min")),
            ("difficulty", Str("Easy, Medium or Hard")),
            ("tags", Arr(new Dictionary<string, object> { ["type"] = "string" })),
            ("ingredient_groups", Arr(Obj(("name", Str("Group name, empty for a single list")), ("ingredients", Arr(ingredient))))),
            ("equipment", Arr(new Dictionary<string, object> { ["type"] = "string" })),
            ("steps", Arr(step)),
            ("tips", Arr(new Dictionary<string, object> { ["type"] = "string" })),
            ("cover_timestamp_seconds", Num("Video time of the best shot of the finished dish")));
        var root = Obj(
            ("source_language", Str("Language spoken in the video")),
            ("recipes", Arr(recipe)),
            ("notes", Str("Anything the reader should know, e.g. uncertain quantities")));

        return root.ToDictionary(kv => kv.Key, kv => JsonSerializer.SerializeToElement(kv.Value));
    }
}
