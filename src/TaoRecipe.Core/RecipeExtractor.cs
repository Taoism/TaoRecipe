using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;
using Anthropic.Models.Beta.Messages;

namespace TaoRecipe.Core;

/// <summary>Sends transcript + sampled frames to Claude and gets back structured, translated recipes.</summary>
public sealed class RecipeExtractor(AppSettings settings)
{
    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    private sealed record Part(string? Text, string? ImagePath);

    public Task<RecipeExtraction> ExtractAsync(VideoSource video, Transcript transcript, IReadOnlyList<SampledFrame> frames,
        string workDir, IProgress<string> log, CancellationToken ct)
    {
        var parts = BuildParts(video, transcript, frames);
        log.Report($"Asking {settings.Model} ({(settings.Engine == Engines.AnthropicApi ? "API key" : "Claude subscription")}) to write the recipe in {settings.TargetLanguage} ({frames.Count} frames, {transcript.Segments.Count} transcript lines)…");
        return WithCoverTimesAsync(settings.Engine == Engines.AnthropicApi
            ? ExtractViaApiAsync(parts, log, ct)
            : ExtractViaClaudeCodeAsync(parts, workDir, log, ct), frames);
    }

    /// <summary>Turns each recipe's chosen cover frame number into a video time.</summary>
    private static async Task<RecipeExtraction> WithCoverTimesAsync(Task<RecipeExtraction> extraction, IReadOnlyList<SampledFrame> frames)
    {
        var result = await extraction.ConfigureAwait(false);
        foreach (var r in result.Recipes)
        {
            var frame = frames.FirstOrDefault(f => f.Index == r.CoverFrame);
            r.CoverTimestampSeconds = frame?.Time ?? -1;
            if (frame == null) r.CoverFrame = 0;
        }
        return result;
    }

    // ---------------- Anthropic API (pay per token) ----------------

    private async Task<RecipeExtraction> ExtractViaApiAsync(List<Part> parts, IProgress<string> log, CancellationToken ct)
    {
        var apiKey = settings.AnthropicApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("No Anthropic API key set (Settings, or the ANTHROPIC_API_KEY environment variable). Or switch the recipe engine to your Claude subscription.");

        var client = new AnthropicClient { ApiKey = apiKey };
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
            Messages = [new BetaUserMessageParam { Content = ToApiBlocks(parts) }],
        };

        var text = new StringBuilder();
        string? stopReason = null;
        long inTokens = 0, outTokens = 0;
        try
        {
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
        }
        catch (Anthropic.Exceptions.AnthropicException ex)
        {
            throw new InvalidOperationException("Anthropic API: " + ApiErrorMessage(ex.Message), ex);
        }
        log.Report($"Claude done: {inTokens:N0} input / {outTokens:N0} output tokens.");

        if (stopReason == "refusal") throw new InvalidOperationException("Claude declined to process this video.");
        if (stopReason == "max_tokens") throw new InvalidOperationException("Claude's answer was cut off (max_tokens).");
        return Parse(text.ToString());
    }

    /// <summary>Pulls the human-readable message out of an API error body, with a hint for the common billing case.</summary>
    private static string ApiErrorMessage(string raw)
    {
        var msg = raw;
        var start = raw.IndexOf('{');
        if (start >= 0)
        {
            try
            {
                using var doc = JsonDocument.Parse(raw[start..]);
                if (doc.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m))
                    msg = m.GetString() ?? raw;
            }
            catch (JsonException)
            {
                // not JSON; keep the raw text
            }
        }
        if (msg.Contains("credit balance", StringComparison.OrdinalIgnoreCase))
            msg += " Add credits at console.anthropic.com → Billing, or switch the recipe engine to your Claude subscription in Settings.";
        return msg;
    }

    private static List<BetaContentBlockParam> ToApiBlocks(List<Part> parts) => parts.Select(p => p.ImagePath == null
        ? (BetaContentBlockParam)new BetaTextBlockParam { Text = p.Text! }
        : new BetaImageBlockParam
        {
            Source = new BetaBase64ImageSource
            {
                Data = Convert.ToBase64String(File.ReadAllBytes(p.ImagePath)),
                MediaType = MediaType.ImageJpeg,
            },
        }).ToList();

    // ---------------- Claude Code CLI (Claude subscription) ----------------

    private async Task<RecipeExtraction> ExtractViaClaudeCodeAsync(List<Part> parts, string workDir, IProgress<string> log, CancellationToken ct)
    {
        var content = new JsonArray();
        foreach (var p in parts)
        {
            content.Add(p.ImagePath == null
                ? new JsonObject { ["type"] = "text", ["text"] = p.Text }
                : new JsonObject
                {
                    ["type"] = "image",
                    ["source"] = new JsonObject
                    {
                        ["type"] = "base64",
                        ["media_type"] = "image/jpeg",
                        ["data"] = Convert.ToBase64String(await File.ReadAllBytesAsync(p.ImagePath, ct).ConfigureAwait(false)),
                    },
                });
        }
        var message = new JsonObject
        {
            ["type"] = "user",
            ["message"] = new JsonObject { ["role"] = "user", ["content"] = content },
        };

        var schema = JsonSerializer.Serialize(Schema());
        string[] args =
        [
            "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose",
            "--model", settings.Model, "--effort", settings.Effort,
            "--tools", "", "--strict-mcp-config", "--setting-sources", "project", "--no-session-persistence",
            "--system-prompt", SystemPrompt(settings.TargetLanguage),
            "--json-schema", schema,
        ];

        // Without ANTHROPIC_API_KEY in its environment the CLI uses the user's Claude login (subscription), not API credit.
        ProcessResult res;
        try
        {
            res = await ProcessRunner.RunAsync(settings.ClaudeCodePath, args, ct,
                stdin: message.ToJsonString() + "\n", workingDirectory: workDir,
                removeEnv: ["ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN"]).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException("Claude Code is not installed or not on PATH (needed for the Claude subscription engine). " +
                "Install it from claude.com/claude-code and sign in once with `claude`, or set its path in Settings.", ex);
        }

        JsonNode? result = null;
        foreach (var line in res.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith('{')) continue;
            JsonNode? node;
            try { node = JsonNode.Parse(line); } catch (JsonException) { continue; }
            if (node?["type"]?.GetValue<string>() == "result") result = node;
        }
        if (result == null)
            throw new InvalidOperationException("Claude Code returned no result:\n" + VideoAcquirer.Tail(res.StdErr.Length > 0 ? res.StdErr : res.StdOut, 8));

        var isError = result["is_error"]?.GetValue<bool>() == true || result["subtype"]?.GetValue<string>() != "success";
        var resultText = result["result"]?.ToString() ?? "";
        if (isError)
        {
            var hint = resultText.Contains("login", StringComparison.OrdinalIgnoreCase) || resultText.Contains("auth", StringComparison.OrdinalIgnoreCase)
                ? " Run `claude` once in a terminal and sign in with your Claude account."
                : "";
            throw new InvalidOperationException($"Claude Code: {(resultText.Length > 0 ? resultText : result["subtype"]?.ToString())}{hint}");
        }

        var usage = result["usage"];
        log.Report($"Claude done in {result["duration_ms"]?.GetValue<double>() / 1000:0}s: " +
            $"{(usage?["input_tokens"]?.GetValue<long>() ?? 0) + (usage?["cache_creation_input_tokens"]?.GetValue<long>() ?? 0) + (usage?["cache_read_input_tokens"]?.GetValue<long>() ?? 0):N0} input / " +
            $"{usage?["output_tokens"]?.GetValue<long>() ?? 0:N0} output tokens (subscription).");

        var structured = result["structured_output"];
        return structured is JsonObject
            ? structured.Deserialize<RecipeExtraction>(ReadOptions) ?? throw new InvalidOperationException("Claude returned no recipe data.")
            : Parse(resultText);
    }

    private static RecipeExtraction Parse(string json)
    {
        json = json.Trim();
        if (json.StartsWith("```", StringComparison.Ordinal))
            json = json[(json.IndexOf('\n') + 1)..].TrimEnd('`').Trim();
        return JsonSerializer.Deserialize<RecipeExtraction>(json, ReadOptions)
            ?? throw new InvalidOperationException("Claude returned no recipe data.");
    }

    // ---------------- prompt ----------------

    private static List<Part> BuildParts(VideoSource video, Transcript transcript, IReadOnlyList<SampledFrame> frames)
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

        var parts = new List<Part> { new(sb.ToString(), null) };
        foreach (var f in frames)
        {
            parts.Add(new($"Frame {f.Index} at {Format.Clock(f.Time)} ({f.Time:0.0} s):", null));
            parts.Add(new(null, f.Path));
        }
        parts.Add(new("Write the recipe(s) from this video now.", null));
        return parts;
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
        cover_frame is the number of the frame (from the "Frame N" labels) that best shows the FINISHED dish, plated or
        sliced; usually near the end. Never pick a frame of raw ingredients or work in progress. For that frame also give
        dish_center_x and dish_center_y (where the centre of the dish is, as a fraction 0–1 of the frame width and
        height, measured from the top-left) and dish_size (how tall the dish appears, as a fraction 0–1 of the frame
        height); a square crop around it becomes the recipe's icon.

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
            ("cover_frame", new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Frame number showing the finished dish best" }),
            ("dish_center_x", Num("Horizontal centre of the dish in that shot, 0-1 of frame width")),
            ("dish_center_y", Num("Vertical centre of the dish in that shot, 0-1 of frame height")),
            ("dish_size", Num("Height of the dish in that shot, 0-1 of frame height")));
        var root = Obj(
            ("source_language", Str("Language spoken in the video")),
            ("recipes", Arr(recipe)),
            ("notes", Str("Anything the reader should know, e.g. uncertain quantities")));

        return root.ToDictionary(kv => kv.Key, kv => JsonSerializer.SerializeToElement(kv.Value));
    }
}
