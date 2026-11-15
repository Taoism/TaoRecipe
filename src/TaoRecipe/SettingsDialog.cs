using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using TaoRecipe.Core;
using TaoRecipe.Dialogs;

namespace TaoRecipe;

public sealed class SettingsDialog : DialogWindow
{
    private static readonly string[] EngineNames = ["Claude subscription (Claude Code, no extra cost)", "Anthropic API key (billed per token)"];
    private static readonly string[] Models = ["claude-opus-5-5", "claude-sonnet-5-5", "claude-fable-5-2", "claude-haiku-5-5"];
    private static readonly string[] Efforts = ["low", "medium", "high", "max"];
    private static readonly string[] WhisperModels = ["LargeV3Turbo", "LargeV3", "Medium", "Small", "Base"];

    private readonly AppSettings _s;
    private readonly TextBox _anthropic, _notion, _parent, _database, _ytdlp, _ffmpeg, _extra, _output;
    private readonly ComboBox _engine, _model, _effort, _whisper;
    private readonly TextBox _claudePath;
    private readonly NumericUpDown _maxFrames;
    private readonly CheckBox _subs, _keepVideo, _keepLocal;
    private readonly TextBlock _testResult;

    public SettingsDialog(AppSettings settings) : base("TaoRecipe settings", 640)
    {
        _s = settings;
        _anthropic = Secret(settings.AnthropicApiKey, "sk-ant-…");
        _notion = Secret(settings.NotionToken, "ntn_… (internal integration secret)");
        _parent = new TextBox { Text = settings.NotionParentPage, Watermark = "Notion page URL — the Recipes database is created here" };
        _database = new TextBox { Text = settings.NotionDatabase, Watermark = "optional: existing database URL (filled in automatically)" };
        _engine = Combo(EngineNames, settings.Engine == Engines.AnthropicApi ? EngineNames[1] : EngineNames[0]);
        _claudePath = new TextBox { Text = settings.ClaudeCodePath, Watermark = "claude" };
        _model = Combo(Models, settings.Model);
        _effort = Combo(Efforts, settings.Effort);
        _whisper = Combo(WhisperModels, settings.WhisperModel);
        _maxFrames = new NumericUpDown { Minimum = 10, Maximum = 100, Increment = 10, Value = settings.MaxFrames, FormatString = "0", Width = 130, HorizontalAlignment = HorizontalAlignment.Left };
        _subs = new CheckBox { Content = "Use uploader subtitles when available (faster than Whisper)", IsChecked = settings.PreferSubtitles };
        _keepVideo = new CheckBox { Content = "Keep downloaded video (in the output folder)", IsChecked = settings.KeepVideo };
        _keepLocal = new CheckBox { Content = "Keep local copy (JSON, Markdown, screenshots) after it is in Notion", IsChecked = settings.KeepLocalCopy };
        _ytdlp = new TextBox { Text = settings.YtDlpPath };
        _ffmpeg = new TextBox { Text = settings.FfmpegPath };
        _extra = new TextBox { Text = settings.YtDlpExtraArgs, Watermark = "e.g. --cookies-from-browser firefox" };
        _output = new TextBox { Text = settings.OutputFolder };
        _testResult = new TextBlock { TextWrapping = TextWrapping.Wrap, Classes = { "muted" } };

        var test = new Button { Content = "Test connections" };
        test.Click += async (_, _) =>
        {
            test.IsEnabled = false;
            _testResult.Text = "Testing…";
            var tmp = new AppSettings();
            Apply(tmp);
            _testResult.Text = await ConnectionTester.TestAsync(tmp, CancellationToken.None);
            test.IsEnabled = true;
        };

        var form = new Grid { ColumnDefinitions = new ColumnDefinitions("170,*"), RowSpacing = 8, ColumnSpacing = 10 };
        int row = 0;
        void Add(string label, Control c)
        {
            form.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var l = Label(label);
            Grid.SetRow(l, row);
            Grid.SetRow(c, row);
            Grid.SetColumn(c, 1);
            form.Children.Add(l);
            form.Children.Add(c);
            row++;
        }
        void Section(string title)
        {
            form.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var t = new TextBlock { Text = title, Classes = { "header" }, Margin = new Thickness(0, row == 0 ? 0 : 8, 0, 0) };
            Grid.SetRow(t, row);
            Grid.SetColumnSpan(t, 2);
            form.Children.Add(t);
            row++;
        }

        Section("Claude");
        var signIn = new Button { Content = "Sign in to Claude…" };
        signIn.Click += async (_, _) =>
        {
            var tmp = new AppSettings { ClaudeCodePath = string.IsNullOrWhiteSpace(_claudePath.Text) ? "claude" : _claudePath.Text.Trim() };
            await ClaudeLoginDialog.ShowAsync(this, tmp);
        };
        Add("Recipe engine", Row(8, _engine, signIn));
        Add("Claude Code", PathRow(_claudePath, async () => { if (await PickFileAsync("claude executable") is { } p) _claudePath.Text = p; }));
        Add("Anthropic API key", _anthropic);
        Add("Model", _model);
        Add("Effort", _effort);
        Add("Frames shown to Claude", _maxFrames);
        Section("Notion");
        Add("Integration token", _notion);
        Add("Parent page", _parent);
        Add("Database", _database);
        Section("Video and transcription");
        Add("Whisper model", _whisper);
        Add("", _subs);
        Add("yt-dlp", PathRow(_ytdlp, async () => { if (await PickFileAsync("yt-dlp executable") is { } p) _ytdlp.Text = p; }));
        Add("yt-dlp extra arguments", _extra);
        Add("ffmpeg", PathRow(_ffmpeg, async () => { if (await PickFileAsync("ffmpeg executable") is { } p) _ffmpeg.Text = p; }));
        Add("Output folder", PathRow(_output, PickOutputAsync));
        Add("", _keepLocal);
        Add("", _keepVideo);

        var help = Label("Notion: create an internal integration at notion.so/profile/integrations, copy its secret, then open your parent page → ••• → Connections → add the integration.", muted: true);
        var body = Column(10, form, Rule(), help, Row(10, test), _testResult);
        SetBody(Scroll(body, 780), "Save", "Cancel");
    }

    private async Task PickOutputAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions { Title = "Output folder" });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } p) _output.Text = p;
    }

    private static TextBox Secret(string value, string watermark) => new() { Text = value, PasswordChar = '•', Watermark = watermark };

    private static ComboBox Combo(string[] items, string value) => new()
    {
        ItemsSource = items,
        SelectedItem = items.Contains(value) ? value : items[0],
        MinWidth = 200,
        MaxWidth = 420,
        HorizontalAlignment = HorizontalAlignment.Left,
    };

    private void Apply(AppSettings t)
    {
        t.Engine = _engine.SelectedIndex == 1 ? Engines.AnthropicApi : Engines.ClaudeCode;
        t.ClaudeCodePath = string.IsNullOrWhiteSpace(_claudePath.Text) ? "claude" : _claudePath.Text.Trim();
        t.AnthropicApiKey = _anthropic.Text ?? "";
        t.NotionToken = _notion.Text ?? "";
        t.NotionParentPage = (_parent.Text ?? "").Trim();
        t.NotionDatabase = (_database.Text ?? "").Trim();
        t.Model = _model.SelectedItem as string ?? Models[0];
        t.Effort = _effort.SelectedItem as string ?? "high";
        t.WhisperModel = _whisper.SelectedItem as string ?? WhisperModels[0];
        t.MaxFrames = (int)(_maxFrames.Value ?? 90);
        t.PreferSubtitles = _subs.IsChecked == true;
        t.KeepVideo = _keepVideo.IsChecked == true;
        t.KeepLocalCopy = _keepLocal.IsChecked == true;
        t.YtDlpPath = string.IsNullOrWhiteSpace(_ytdlp.Text) ? "yt-dlp" : _ytdlp.Text.Trim();
        t.FfmpegPath = string.IsNullOrWhiteSpace(_ffmpeg.Text) ? "ffmpeg" : _ffmpeg.Text.Trim();
        t.YtDlpExtraArgs = (_extra.Text ?? "").Trim();
        t.OutputFolder = string.IsNullOrWhiteSpace(_output.Text) ? AppSettings.DefaultOutputFolder : _output.Text.Trim();
    }

    protected override Task<bool> ValidateAsync()
    {
        Apply(_s);
        _s.Save();
        return Task.FromResult(true);
    }
}
