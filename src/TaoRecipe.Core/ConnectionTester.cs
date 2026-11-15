using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Anthropic;

namespace TaoRecipe.Core;

/// <summary>Quick checks for the Settings dialog: are the keys valid and can the integration see the page/database?</summary>
public static class ConnectionTester
{
    public static async Task<string> TestAsync(AppSettings s, CancellationToken ct)
    {
        var lines = new List<string>();

        if (s.Engine == Engines.ClaudeCode) lines.Add(await TestClaudeCodeAsync(s, ct).ConfigureAwait(false));
        else if (string.IsNullOrWhiteSpace(s.AnthropicApiKey)) lines.Add("✘ Anthropic: no API key.");
        else
        {
            try
            {
                var client = new AnthropicClient { ApiKey = s.AnthropicApiKey };
                var m = await client.Models.Retrieve(s.Model, cancellationToken: ct).ConfigureAwait(false);
                lines.Add($"✔ Anthropic: key works, model {m.DisplayName} available.");
            }
            catch (Exception ex) when (ex is Anthropic.Exceptions.AnthropicException or HttpRequestException)
            {
                lines.Add("✘ Anthropic: " + ex.Message);
            }
        }

        if (string.IsNullOrWhiteSpace(s.NotionToken)) lines.Add("✘ Notion: no integration token.");
        else
        {
            using var http = new HttpClient { BaseAddress = new Uri("https://api.notion.com/v1/") };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.NotionToken.Trim());
            http.DefaultRequestHeaders.Add("Notion-Version", "2026-03-11");
            try
            {
                using var me = await http.GetAsync("users/me", ct).ConfigureAwait(false);
                if (!me.IsSuccessStatusCode) lines.Add($"✘ Notion: token rejected ({(int)me.StatusCode}).");
                else
                {
                    var bot = JsonNode.Parse(await me.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                    lines.Add($"✔ Notion: token works (integration \"{bot?["name"]?.GetValue<string>()}\").");
                    var db = NotionPublisher.ParseId(s.NotionDatabase);
                    var page = NotionPublisher.ParseId(s.NotionParentPage);
                    if (db != null) lines.Add(await CheckAsync(http, $"databases/{db}", "database", ct).ConfigureAwait(false));
                    else if (page != null) lines.Add(await CheckAsync(http, $"pages/{page}", "parent page", ct).ConfigureAwait(false));
                    else lines.Add("✘ Notion: set a parent page (a Recipes database is created there) or an existing database.");
                }
            }
            catch (HttpRequestException ex)
            {
                lines.Add("✘ Notion: " + ex.Message);
            }
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static async Task<string> TestClaudeCodeAsync(AppSettings s, CancellationToken ct)
    {
        try
        {
            var res = await ProcessRunner.RunAsync(s.ClaudeCodePath, ["auth", "status"], ct,
                removeEnv: ["ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN"]).ConfigureAwait(false);
            var json = JsonNode.Parse(res.StdOut[Math.Max(0, res.StdOut.IndexOf('{'))..]);
            if (json?["loggedIn"]?.GetValue<bool>() != true)
                return "✘ Claude Code: not signed in. Run `claude` in a terminal once and log in with your Claude account.";
            var method = json["authMethod"]?.GetValue<string>() ?? "";
            return method == "claude.ai"
                ? "✔ Claude Code: signed in with your Claude subscription."
                : $"✔ Claude Code: signed in ({method}). Note: this may bill API credit rather than your subscription.";
        }
        catch (InvalidOperationException)
        {
            return "✘ Claude Code: not found. Install it (claude.com/claude-code) or set its path.";
        }
        catch (System.Text.Json.JsonException)
        {
            return "✘ Claude Code: could not read `claude auth status`. Is it up to date?";
        }
    }

    private static async Task<string> CheckAsync(HttpClient http, string path, string what, CancellationToken ct)
    {
        using var r = await http.GetAsync(path, ct).ConfigureAwait(false);
        return r.IsSuccessStatusCode
            ? $"✔ Notion: {what} is shared with the integration."
            : $"✘ Notion: {what} not reachable ({(int)r.StatusCode}). In Notion open it → ••• → Connections → add your integration.";
    }
}
