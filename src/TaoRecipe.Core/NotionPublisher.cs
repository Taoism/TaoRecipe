using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TaoRecipe.Core;

/// <summary>Creates recipe pages (with uploaded screen grabs) in a Notion database via the public REST API.</summary>
public sealed partial class NotionPublisher : IDisposable
{
    private const string ApiVersion = "2026-03-11";
    private readonly AppSettings settings;
    private readonly HttpClient http;

    public NotionPublisher(AppSettings settings)
    {
        this.settings = settings;
        var token = settings.NotionToken;
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("No Notion integration token set (Settings, or the NOTION_TOKEN environment variable).");
        http = new HttpClient { BaseAddress = new Uri("https://api.notion.com/v1/"), Timeout = TimeSpan.FromMinutes(3) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        http.DefaultRequestHeaders.Add("Notion-Version", ApiVersion);
    }

    public void Dispose() => http.Dispose();

    [GeneratedRegex("[0-9a-fA-F]{32}|[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex IdRegex();

    /// <summary>Extracts a Notion ID from an ID or a page/database URL (the last 32-hex group wins).</summary>
    public static string? ParseId(string? idOrUrl)
    {
        if (string.IsNullOrWhiteSpace(idOrUrl)) return null;
        var path = idOrUrl.Split('?')[0];
        var matches = IdRegex().Matches(path);
        if (matches.Count == 0) return null;
        var hex = matches[^1].Value.Replace("-", "").ToLowerInvariant();
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }

    // ---------------- data source ----------------

    private sealed record DataSourceInfo(string Id, Dictionary<string, string> PropertyTypes);

    private async Task<DataSourceInfo> EnsureDataSourceAsync(IProgress<string> log, CancellationToken ct)
    {
        var dbId = ParseId(settings.NotionDatabase);
        if (dbId != null)
        {
            var db = await SendAsync(HttpMethod.Get, $"databases/{dbId}", null, ct, allowNotFound: true).ConfigureAwait(false);
            string? dsId = db?["data_sources"]?.AsArray().FirstOrDefault()?["id"]?.GetValue<string>();
            dsId ??= dbId; // the user may have pasted a data source ID
            var ds = await SendAsync(HttpMethod.Get, $"data_sources/{dsId}", null, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Notion database not found. Is it shared with your integration (••• → Connections)?");
            return new DataSourceInfo(dsId, ReadPropertyTypes(ds));
        }

        var parent = ParseId(settings.NotionParentPage)
            ?? throw new InvalidOperationException("Set a Notion parent page (Settings) where the Recipes database will be created, or an existing database.");
        log.Report("Creating the 'Recipes' database in Notion…");
        var body = new JsonObject
        {
            ["parent"] = new JsonObject { ["type"] = "page_id", ["page_id"] = parent },
            ["title"] = RichText("Recipes"),
            ["icon"] = new JsonObject { ["type"] = "emoji", ["emoji"] = "🍳" },
            ["initial_data_source"] = new JsonObject { ["properties"] = SchemaProperties() },
        };
        var created = await SendAsync(HttpMethod.Post, "databases", body, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Notion parent page not found. Share it with your integration (••• → Connections).");
        var newDb = created["id"]!.GetValue<string>();
        var newDs = created["data_sources"]?.AsArray().FirstOrDefault()?["id"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Notion created the database but returned no data source.");
        settings.NotionDatabase = newDb;
        settings.Save();
        var info = await SendAsync(HttpMethod.Get, $"data_sources/{newDs}", null, ct).ConfigureAwait(false);
        return new DataSourceInfo(newDs, info is null ? [] : ReadPropertyTypes(info));
    }

    private static Dictionary<string, string> ReadPropertyTypes(JsonNode ds)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (ds["properties"] is JsonObject props)
            foreach (var (name, p) in props)
                if (p?["type"]?.GetValue<string>() is { } t) map[name] = t;
        return map;
    }

    private static JsonObject SchemaProperties()
    {
        static JsonObject Empty(string type) => new() { [type] = new JsonObject() };
        return new JsonObject
        {
            ["Name"] = Empty("title"),
            ["Original title"] = Empty("rich_text"),
            ["Source"] = Empty("url"),
            ["Channel"] = Empty("rich_text"),
            ["Cuisine"] = Empty("select"),
            ["Course"] = Empty("select"),
            ["Difficulty"] = Empty("select"),
            ["Tags"] = Empty("multi_select"),
            ["Servings"] = Empty("rich_text"),
            ["Prep time"] = Empty("rich_text"),
            ["Cook time"] = Empty("rich_text"),
            ["Total time"] = Empty("rich_text"),
            ["Language"] = Empty("select"),
            ["Imported"] = Empty("date"),
        };
    }

    // ---------------- page ----------------

    /// <summary>Publishes one recipe; returns the Notion page URL.</summary>
    public async Task<string> PublishAsync(Recipe recipe, VideoSource video, string sourceLanguage, IProgress<string> log, CancellationToken ct)
    {
        var ds = await EnsureDataSourceAsync(log, ct).ConfigureAwait(false);

        log.Report($"Uploading {recipe.Steps.Count(s => s.Image != null) + (recipe.CoverImage != null ? 1 : 0)} images to Notion…");
        string? coverUpload = recipe.CoverImage != null ? await UploadAsync(recipe.CoverImage, ct).ConfigureAwait(false) : null;
        var stepUploads = new Dictionary<RecipeStep, string>();
        foreach (var step in recipe.Steps.Where(s => s.Image != null))
            stepUploads[step] = await UploadAsync(step.Image!, ct).ConfigureAwait(false);

        var blocks = BuildBlocks(recipe, video, sourceLanguage, stepUploads, coverUpload);
        var page = new JsonObject
        {
            ["parent"] = new JsonObject { ["type"] = "data_source_id", ["data_source_id"] = ds.Id },
            ["icon"] = new JsonObject { ["type"] = "emoji", ["emoji"] = "🍳" },
            ["properties"] = BuildProperties(recipe, video, sourceLanguage, ds.PropertyTypes),
            ["children"] = new JsonArray(blocks.Take(100).Select(b => (JsonNode)b).ToArray()),
        };
        if (coverUpload != null)
            page["cover"] = new JsonObject { ["type"] = "file_upload", ["file_upload"] = new JsonObject { ["id"] = coverUpload } };

        log.Report("Creating Notion page…");
        var created = await SendAsync(HttpMethod.Post, "pages", page, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Notion rejected the page.");
        var pageId = created["id"]!.GetValue<string>();
        foreach (var chunk in blocks.Skip(100).Chunk(100))
        {
            var append = new JsonObject { ["children"] = new JsonArray(chunk.Select(b => (JsonNode)b).ToArray()) };
            await SendAsync(HttpMethod.Patch, $"blocks/{pageId}/children", append, ct).ConfigureAwait(false);
        }
        return created["url"]?.GetValue<string>() ?? $"https://www.notion.so/{pageId.Replace("-", "")}";
    }

    private static JsonObject BuildProperties(Recipe r, VideoSource video, string sourceLanguage, Dictionary<string, string> types)
    {
        var props = new JsonObject();
        var titleProp = types.FirstOrDefault(kv => kv.Value == "title").Key ?? "Name";
        props[titleProp] = new JsonObject { ["title"] = RichText(r.Title.Length > 0 ? r.Title : video.Title) };

        void Set(string name, string value)
        {
            if (string.IsNullOrWhiteSpace(value) || !types.TryGetValue(name, out var type)) return;
            value = value.Trim();
            props[name] = type switch
            {
                "rich_text" => new JsonObject { ["rich_text"] = RichText(value) },
                "select" => new JsonObject { ["select"] = new JsonObject { ["name"] = SelectName(value) } },
                "url" => new JsonObject { ["url"] = value },
                "multi_select" => new JsonObject { ["multi_select"] = new JsonArray(new JsonObject { ["name"] = SelectName(value) }) },
                _ => null,
            };
            if (props[name] is null) props.Remove(name);
        }

        Set("Original title", r.OriginalTitle);
        Set("Source", video.SourceUrl ?? "");
        Set("Channel", video.Uploader);
        Set("Cuisine", r.Cuisine);
        Set("Course", r.Course);
        Set("Difficulty", r.Difficulty);
        Set("Servings", r.Servings);
        Set("Prep time", r.PrepTime);
        Set("Cook time", r.CookTime);
        Set("Total time", r.TotalTime);
        Set("Language", sourceLanguage);
        if (types.TryGetValue("Tags", out var tagType) && tagType == "multi_select" && r.Tags.Count > 0)
        {
            var tags = r.Tags.Select(SelectName).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(20)
                .Select(t => (JsonNode)new JsonObject { ["name"] = t }).ToArray();
            props["Tags"] = new JsonObject { ["multi_select"] = new JsonArray(tags) };
        }
        if (types.TryGetValue("Imported", out var dType) && dType == "date")
            props["Imported"] = new JsonObject { ["date"] = new JsonObject { ["start"] = DateTime.Now.ToString("yyyy-MM-dd") } };
        return props;
    }

    /// <summary>Select option names may not contain commas and are limited to 100 characters.</summary>
    private static string SelectName(string s)
    {
        s = s.Replace(',', ' ').Trim();
        return s.Length > 100 ? s[..100] : s;
    }

    private static List<JsonObject> BuildBlocks(Recipe r, VideoSource video, string sourceLanguage,
        Dictionary<RecipeStep, string> stepUploads, string? coverUpload)
    {
        var b = new List<JsonObject>();

        var meta = new List<string>();
        if (r.Servings.Length > 0) meta.Add($"Serves {r.Servings}");
        if (r.PrepTime.Length > 0) meta.Add($"Prep {r.PrepTime}");
        if (r.CookTime.Length > 0) meta.Add($"Cook {r.CookTime}");
        if (r.TotalTime.Length > 0) meta.Add($"Total {r.TotalTime}");
        var summary = r.Summary;
        if (meta.Count > 0) summary = summary.Length > 0 ? summary + "\n" + string.Join(" · ", meta) : string.Join(" · ", meta);
        if (summary.Length > 0)
            b.Add(Block("callout", new JsonObject { ["rich_text"] = RichText(summary), ["icon"] = new JsonObject { ["type"] = "emoji", ["emoji"] = "🍽️" } }));

        if (video.SourceUrl != null)
        {
            var isEmbeddableVideo = video.SourceUrl.Contains("youtube.com", StringComparison.OrdinalIgnoreCase)
                || video.SourceUrl.Contains("youtu.be", StringComparison.OrdinalIgnoreCase)
                || video.SourceUrl.Contains("vimeo.com", StringComparison.OrdinalIgnoreCase);
            b.Add(isEmbeddableVideo
                ? Block("video", new JsonObject { ["type"] = "external", ["external"] = new JsonObject { ["url"] = video.SourceUrl } })
                : Block("bookmark", new JsonObject { ["url"] = video.SourceUrl }));
        }
        else if (coverUpload == null && r.CoverImage == null)
        {
            b.Add(Paragraph($"Source: {Path.GetFileName(video.MediaPath)}"));
        }

        b.Add(Heading("heading_2", "Ingredients"));
        foreach (var g in r.IngredientGroups)
        {
            if (g.Name.Length > 0 && r.IngredientGroups.Count > 1) b.Add(Heading("heading_3", g.Name));
            foreach (var ing in g.Ingredients)
                b.Add(Block("to_do", new JsonObject { ["rich_text"] = RichText(ing.ToString()), ["checked"] = false }));
        }

        if (r.Equipment.Count > 0)
        {
            b.Add(Heading("heading_2", "Equipment"));
            foreach (var e in r.Equipment) b.Add(Block("bulleted_list_item", new JsonObject { ["rich_text"] = RichText(e) }));
        }

        b.Add(Heading("heading_2", "Method"));
        foreach (var s in r.Steps)
        {
            var rt = new JsonArray();
            if (s.Title.Length > 0) rt.Add(TextRun(s.Title + ". ", bold: true));
            foreach (var part in RichText(s.Instruction)) rt.Add(part!.DeepClone());
            var stamp = " ⏱ " + Format.Clock(s.TimestampSeconds);
            rt.Add(TextRun(stamp, color: "gray", link: TimestampLink(video.SourceUrl, s.TimestampSeconds)));
            var item = new JsonObject { ["rich_text"] = rt };
            if (stepUploads.TryGetValue(s, out var upload))
                item["children"] = new JsonArray(ImageBlock(upload));
            b.Add(Block("numbered_list_item", item));
        }

        if (r.Tips.Count > 0)
        {
            b.Add(Heading("heading_2", "Tips"));
            foreach (var t in r.Tips) b.Add(Block("bulleted_list_item", new JsonObject { ["rich_text"] = RichText(t) }));
        }

        b.Add(Block("divider", new JsonObject()));
        var footer = $"Imported with TaoRecipe on {DateTime.Now:yyyy-MM-dd}";
        if (sourceLanguage.Length > 0) footer += $" · original language: {sourceLanguage}";
        if (video.Uploader.Length > 0) footer += $" · by {video.Uploader}";
        b.Add(Block("paragraph", new JsonObject { ["rich_text"] = new JsonArray(TextRun(footer, color: "gray", italic: true)) }));
        return b;
    }

    private static string? TimestampLink(string? url, double seconds)
    {
        if (url == null) return null;
        if (!url.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) && !url.Contains("youtu.be", StringComparison.OrdinalIgnoreCase))
            return null;
        var sep = url.Contains('?') ? '&' : '?';
        return $"{url}{sep}t={(int)seconds}s";
    }

    private static JsonObject Block(string type, JsonObject content) => new() { ["object"] = "block", ["type"] = type, [type] = content };
    private static JsonObject Heading(string level, string text) => Block(level, new JsonObject { ["rich_text"] = RichText(text) });
    private static JsonObject Paragraph(string text) => Block("paragraph", new JsonObject { ["rich_text"] = RichText(text) });
    private static JsonObject ImageBlock(string uploadId) =>
        Block("image", new JsonObject { ["type"] = "file_upload", ["file_upload"] = new JsonObject { ["id"] = uploadId } });

    private static JsonObject TextRun(string text, bool bold = false, bool italic = false, string? color = null, string? link = null)
    {
        var t = new JsonObject { ["content"] = text };
        if (link != null) t["link"] = new JsonObject { ["url"] = link };
        var run = new JsonObject { ["type"] = "text", ["text"] = t };
        if (bold || italic || color != null)
            run["annotations"] = new JsonObject { ["bold"] = bold, ["italic"] = italic, ["color"] = color ?? "default" };
        return run;
    }

    /// <summary>Rich text, split into 2000-character runs (Notion's per-run limit).</summary>
    private static JsonArray RichText(string text)
    {
        var arr = new JsonArray();
        for (int i = 0; i < text.Length; i += 2000)
            arr.Add(TextRun(text.Substring(i, Math.Min(2000, text.Length - i))));
        return arr;
    }

    // ---------------- uploads + HTTP ----------------

    private async Task<string> UploadAsync(string path, CancellationToken ct)
    {
        var name = Path.GetFileName(path);
        var created = await SendAsync(HttpMethod.Post, "file_uploads",
            new JsonObject { ["filename"] = name, ["content_type"] = "image/jpeg" }, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Notion file upload could not be created.");
        var id = created["id"]!.GetValue<string>();

        for (int attempt = 0; ; attempt++)
        {
            using var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false));
            file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            form.Add(file, "file", name);
            using var resp = await http.PostAsync($"file_uploads/{id}/send", form, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode) return id;
            if (attempt < 4 && IsRetryable(resp.StatusCode)) { await DelayAsync(resp, attempt, ct).ConfigureAwait(false); continue; }
            throw new InvalidOperationException($"Notion upload of {name} failed: {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)}");
        }
    }

    private async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken ct, bool allowNotFound = false)
    {
        for (int attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(method, path);
            if (body != null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode) return JsonNode.Parse(text);
            if (resp.StatusCode == HttpStatusCode.NotFound && (allowNotFound || method == HttpMethod.Get)) return null;
            if (attempt < 4 && IsRetryable(resp.StatusCode)) { await DelayAsync(resp, attempt, ct).ConfigureAwait(false); continue; }
            throw new InvalidOperationException($"Notion API {method} {path} failed: {(int)resp.StatusCode} {NotionMessage(text)}");
        }
    }

    private static bool IsRetryable(HttpStatusCode code) =>
        code == HttpStatusCode.TooManyRequests || (int)code >= 500;

    private static Task DelayAsync(HttpResponseMessage resp, int attempt, CancellationToken ct)
    {
        var wait = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
        return Task.Delay(wait, ct);
    }

    private static string NotionMessage(string body)
    {
        try
        {
            var n = JsonNode.Parse(body);
            return n?["message"]?.GetValue<string>() ?? body;
        }
        catch (JsonException)
        {
            return body;
        }
    }
}
