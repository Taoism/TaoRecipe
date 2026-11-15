using TaoRecipe.Core;

Console.OutputEncoding = System.Text.Encoding.UTF8;
var settings = AppSettings.Load();
var inputs = new List<string>();
bool publish = true, fresh = false;

for (int i = 0; i < args.Length; i++)
{
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
    switch (args[i])
    {
        case "-h" or "--help" or "/?":
            PrintHelp();
            return 0;
        case "-l" or "--lang": settings.TargetLanguage = Next(); break;
        case "--no-notion": publish = false; break;
        case "--fresh": fresh = true; break;
        case "--model": settings.Model = Next(); break;
        case "--engine":
            settings.Engine = Next().ToLowerInvariant() switch
            {
                "api" => Engines.AnthropicApi,
                "claude" or "subscription" => Engines.ClaudeCode,
                var e => throw new ArgumentException($"Unknown engine '{e}' (use claude or api)"),
            };
            break;
        case "--set-engine": settings.Engine = Next().Equals("api", StringComparison.OrdinalIgnoreCase) ? Engines.AnthropicApi : Engines.ClaudeCode; settings.Save(); Console.WriteLine($"Engine saved: {settings.Engine}."); break;
        case "--whisper": settings.WhisperModel = Next(); break;
        case "--out": settings.OutputFolder = Next(); break;
        case "--set-anthropic-key": settings.AnthropicApiKey = Next(); settings.Save(); Console.WriteLine("Anthropic key saved."); break;
        case "--set-notion-token": settings.NotionToken = Next(); settings.Save(); Console.WriteLine("Notion token saved."); break;
        case "--set-notion-parent": settings.NotionParentPage = Next(); settings.Save(); Console.WriteLine("Notion parent page saved."); break;
        case "--set-notion-database": settings.NotionDatabase = Next(); settings.Save(); Console.WriteLine("Notion database saved."); break;
        case "--set-language": settings.TargetLanguage = Next(); settings.Save(); Console.WriteLine("Default language saved."); break;
        default:
            if (args[i].StartsWith('@') && File.Exists(args[i][1..]))
                inputs.AddRange((await File.ReadAllLinesAsync(args[i][1..])).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')));
            else
                inputs.Add(args[i]);
            break;
    }
}
if (inputs.Count == 0)
{
    if (args.Length == 0) PrintHelp();
    return 0;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
var log = new Progress<string>(m => Console.WriteLine($"{DateTime.Now:HH:mm:ss}  {m}"));
var pipeline = new RecipePipeline(settings);
int failures = 0;
foreach (var input in inputs)
{
    Console.WriteLine($"=== {input}");
    try
    {
        var results = await pipeline.ProcessAsync(input, new PipelineOptions { PublishToNotion = publish, Fresh = fresh }, log, cts.Token);
        foreach (var r in results)
            Console.WriteLine($"  ✔ {r.Recipe.Title}\n    {r.Folder}" + (r.NotionUrl != null ? $"\n    {r.NotionUrl}" : ""));
    }
    catch (OperationCanceledException)
    {
        await Console.Error.WriteLineAsync("Cancelled.");
        return 2;
    }
    catch (Exception ex) when (ex is InvalidOperationException or IOException or HttpRequestException or Anthropic.Exceptions.AnthropicException)
    {
        failures++;
        await Console.Error.WriteLineAsync($"  ✘ {ex.Message}");
    }
}
return failures == 0 ? 0 : 1;

static void PrintHelp() => Console.WriteLine("""
    taorecipe-cli — turn cooking videos into translated Notion recipes with step screenshots

    Usage: taorecipe-cli [options] <url|file> [<url|file> …] [@list.txt]

      -l, --lang <language>     target language (default from settings, e.g. English)
      --no-notion               only write the local copy (JSON, Markdown, images)
      --fresh                   ignore the cached transcript / Claude result from an earlier run
      --engine claude|api       claude = your Claude subscription via Claude Code (default, no extra cost)
                                api = Anthropic API key (billed per token)
      --model <id>              Claude model (default claude-opus-5-5)
      --whisper <model>         Whisper model: LargeV3Turbo, LargeV3, Medium, Small, Base…
      --out <folder>            output folder for the local copies

    One-time setup (stored in %APPDATA%\TaoRecipe\settings.json, secrets DPAPI-encrypted):
      --set-engine claude|api      default recipe engine
      --set-anthropic-key <key>    only for --engine api; or set ANTHROPIC_API_KEY
      --set-notion-token <token>   or set NOTION_TOKEN (internal integration secret)
      --set-notion-parent <page>   page URL/ID where the Recipes database is created
      --set-notion-database <db>   use an existing database instead
      --set-language <language>    default target language
    """);
