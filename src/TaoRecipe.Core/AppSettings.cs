using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TaoRecipe.Core;

public static class Engines
{
    public const string ClaudeCode = "ClaudeCode";
    public const string AnthropicApi = "AnthropicApi";
}

/// <summary>User settings, stored in %APPDATA%\TaoRecipe\settings.json. Secrets are DPAPI-protected on Windows.</summary>
public sealed class AppSettings
{
    public string TargetLanguage { get; set; } = "English";
    /// <summary>"ClaudeCode" = the installed Claude Code CLI on the user's Claude subscription; "AnthropicApi" = API key (billed per token).</summary>
    public string Engine { get; set; } = Engines.ClaudeCode;
    public string ClaudeCodePath { get; set; } = "claude";
    public string Model { get; set; } = "claude-opus-5-5";
    public string Effort { get; set; } = "high";
    public string WhisperModel { get; set; } = "LargeV3Turbo";
    public bool PreferSubtitles { get; set; } = true;
    public int MaxFrames { get; set; } = 90;
    public string YtDlpPath { get; set; } = "yt-dlp";
    public string FfmpegPath { get; set; } = "ffmpeg";
    public string YtDlpExtraArgs { get; set; } = "";
    public string OutputFolder { get; set; } = DefaultOutputFolder;
    public bool KeepVideo { get; set; }

    /// <summary>Notion page (ID or URL) that the Recipes database is created under.</summary>
    public string NotionParentPage { get; set; } = "";
    /// <summary>Existing Notion database (ID or URL). Filled automatically after the first import.</summary>
    public string NotionDatabase { get; set; } = "";

    [JsonPropertyName("anthropicKey")] public string AnthropicKeyStored { get; set; } = "";
    [JsonPropertyName("notionToken")] public string NotionTokenStored { get; set; } = "";

    [JsonIgnore]
    public string AnthropicApiKey
    {
        get => Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") is { Length: > 0 } env ? env : Unprotect(AnthropicKeyStored);
        set => AnthropicKeyStored = Protect(value);
    }

    [JsonIgnore]
    public string NotionToken
    {
        get => Environment.GetEnvironmentVariable("NOTION_TOKEN") is { Length: > 0 } env ? env : Unprotect(NotionTokenStored);
        set => NotionTokenStored = Protect(value);
    }

    public static string AppDataFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TaoRecipe");

    public static string ModelsFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaoRecipe", "models");

    public static string DefaultOutputFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TaoRecipe");

    private static string SettingsPath => Path.Combine(AppDataFolder, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOptions) ?? new AppSettings();
        }
        catch (JsonException)
        {
            // corrupt file: fall back to defaults
        }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(AppDataFolder);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
    }

    private const string DpapiPrefix = "dpapi:";

    private static string Protect(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        value = value.Trim();
        if (!OperatingSystem.IsWindows()) return value;
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
        return DpapiPrefix + Convert.ToBase64String(bytes);
    }

    private static string Unprotect(string stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        if (!stored.StartsWith(DpapiPrefix, StringComparison.Ordinal) || !OperatingSystem.IsWindows()) return stored;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(stored[DpapiPrefix.Length..]), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (CryptographicException)
        {
            return "";
        }
    }
}
