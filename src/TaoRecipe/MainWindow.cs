using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using TaoRecipe.Core;
using TaoRecipe.Dialogs;

namespace TaoRecipe;

public sealed class MainWindow : Window
{
    private static WindowIcon? _icon;
    public static WindowIcon AppIcon => _icon ??= new WindowIcon(AssetLoader.Open(new Uri("avares://TaoRecipe/Assets/taorecipe.png")));

    internal static readonly string[] Languages =
    [
        "English", "Dutch", "German", "French", "Spanish", "Italian", "Portuguese", "Swedish", "Norwegian", "Danish",
        "Finnish", "Polish", "Czech", "Hungarian", "Greek", "Turkish", "Russian", "Ukrainian", "Arabic", "Hebrew",
        "Hindi", "Thai", "Vietnamese", "Indonesian", "Chinese (Simplified)", "Chinese (Traditional)", "Japanese", "Korean",
    ];

    private static readonly string[] VideoExtensions = [".mp4", ".mkv", ".webm", ".mov", ".avi", ".m4v", ".ts", ".wmv", ".flv", ".mpg", ".mpeg"];

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly TextBox _inputs;
    private readonly ComboBox _language;
    private readonly CheckBox _publish;
    private readonly Button _start, _cancel;
    private readonly TextBox _log;
    private readonly StackPanel _results;
    private readonly Border _resultsPanel;
    private readonly TextBlock _status;
    private readonly ProgressBar _progress;
    private CancellationTokenSource? _cts;

    public MainWindow(string[] args)
    {
        Title = "TaoRecipe — video recipes to Notion";
        Icon = AppIcon;
        Width = 980;
        Height = 760;
        MinWidth = 640;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _inputs = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            Height = 120,
            Watermark = "Paste video URLs (YouTube, Instagram, TikTok, Vimeo, Facebook, … anything yt-dlp supports) or drop video files here — one per line",
        };
        _inputs.Classes.Add("mono");
        if (args.Length > 0) _inputs.Text = string.Join(Environment.NewLine, args);

        var addFiles = new Button { Content = "Add files…" };
        addFiles.Click += async (_, _) => await AddFilesAsync();
        var clear = new Button { Content = "Clear" };
        clear.Click += (_, _) => _inputs.Text = "";

        _language = new ComboBox { ItemsSource = Languages, MinWidth = 190, SelectedItem = Languages.Contains(_settings.TargetLanguage) ? _settings.TargetLanguage : "English" };
        _language.SelectionChanged += (_, _) =>
        {
            if (_language.SelectedItem is string l) { _settings.TargetLanguage = l; _settings.Save(); }
        };

        _publish = new CheckBox { Content = "Publish to Notion", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };

        _start = new Button { Content = "Create recipes", MinWidth = 130, HorizontalContentAlignment = HorizontalAlignment.Center };
        _start.Classes.Add("accent");
        _start.Click += async (_, _) => await RunAsync();
        _cancel = new Button { Content = "Cancel", IsEnabled = false };
        _cancel.Click += (_, _) => _cts?.Cancel();
        var settingsBtn = new Button { Content = "Settings…" };
        settingsBtn.Click += async (_, _) => await new SettingsDialog(_settings).ShowDialog<bool>(this);
        var openOut = new Button { Content = "Output folder" };
        openOut.Click += (_, _) => { Directory.CreateDirectory(_settings.OutputFolder); Open(_settings.OutputFolder); };

        var inputButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { addFiles, clear } };
        var optionsRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*,Auto"), ColumnSpacing = 10 };
        AddAt(optionsRow, 0, new TextBlock { Text = "Translate to", VerticalAlignment = VerticalAlignment.Center });
        AddAt(optionsRow, 1, _language);
        AddAt(optionsRow, 2, _publish);
        AddAt(optionsRow, 4, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { settingsBtn, openOut, _cancel, _start } });

        _results = new StackPanel { Spacing = 4 };
        _resultsPanel = new Border
        {
            IsVisible = false,
            Classes = { "panel" },
            Padding = new Thickness(10, 8),
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = "Recipes", Classes = { "header" } },
                    new ScrollViewer { MaxHeight = 160, Content = _results, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
                },
            },
        };

        _log = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
        _log.Classes.Add("mono");

        _status = new TextBlock { Text = "Ready.", VerticalAlignment = VerticalAlignment.Center, Classes = { "muted" } };
        _progress = new ProgressBar { IsIndeterminate = false, Width = 160, IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
        var footer = new Border
        {
            Classes = { "footer" },
            Padding = new Thickness(12, 6),
            Child = new DockPanel { Children = { Docked(_progress, Dock.Right), _status } },
        };

        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,*,Auto"),
            Margin = new Thickness(14, 12, 14, 0),
            RowSpacing = 8,
        };
        AddRow(root, 0, new TextBlock { Text = "Videos", Classes = { "header" } });
        AddRow(root, 1, _inputs);
        AddRow(root, 2, inputButtons);
        AddRow(root, 3, optionsRow);
        AddRow(root, 4, _resultsPanel);
        AddRow(root, 5, _log);
        var dock = new DockPanel();
        dock.Children.Add(Docked(footer, Dock.Bottom));
        dock.Children.Add(root);
        Content = dock;

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = DragDropEffects.Copy);
        AddHandler(DragDrop.DropEvent, OnDrop);

        Opened += async (_, _) =>
        {
            if ((_settings.Engine == Engines.AnthropicApi && string.IsNullOrWhiteSpace(_settings.AnthropicApiKey)) || string.IsNullOrWhiteSpace(_settings.NotionToken))
            {
                Log("Welcome! Open Settings to enter your Notion integration token (and choose the recipe engine) first.");
                await new SettingsDialog(_settings).ShowDialog<bool>(this);
            }
        };
    }

    private static T Docked<T>(T c, Dock d) where T : Control { DockPanel.SetDock(c, d); return c; }
    private static void AddRow(Grid g, int row, Control c) { Grid.SetRow(c, row); g.Children.Add(c); }
    private static void AddAt(Grid g, int col, Control c) { Grid.SetColumn(c, col); g.Children.Add(c); }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles();
        if (files != null)
        {
            var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
            AppendInputs(paths.SelectMany(ExpandPath));
            return;
        }
        if (e.DataTransfer.TryGetText() is { Length: > 0 } text) AppendInputs(text.Split('\n').Select(s => s.Trim()));
    }

    private static IEnumerable<string> ExpandPath(string path)
    {
        if (!Directory.Exists(path)) return [path];
        return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .Where(f => VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .Order(StringComparer.OrdinalIgnoreCase);
    }

    private void AppendInputs(IEnumerable<string> lines)
    {
        var current = (_inputs.Text ?? "").TrimEnd();
        var add = string.Join(Environment.NewLine, lines.Where(l => l.Length > 0));
        if (add.Length == 0) return;
        _inputs.Text = current.Length == 0 ? add : current + Environment.NewLine + add;
    }

    private async Task AddFilesAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add video files",
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("Videos") { Patterns = VideoExtensions.Select(x => "*" + x).ToArray() }, FilePickerFileTypes.All],
        });
        AppendInputs(files.Select(f => f.TryGetLocalPath()).OfType<string>());
    }

    private void Log(string message)
    {
        void Do()
        {
            _log.Text += $"{DateTime.Now:HH:mm:ss}  {message}{Environment.NewLine}";
            _log.CaretIndex = _log.Text.Length;
            _status.Text = message.Split('\n')[0];
        }
        if (Dispatcher.UIThread.CheckAccess()) Do(); else Dispatcher.UIThread.Post(Do);
    }

    private async Task RunAsync()
    {
        var inputs = (_inputs.Text ?? "").Split('\n').Select(s => s.Trim().Trim('"')).Where(s => s.Length > 0 && !s.StartsWith('#')).Distinct().ToList();
        if (inputs.Count == 0)
        {
            await MessageBox.ShowAsync(this, "Add at least one video URL or file.", MessageKind.Info, "TaoRecipe");
            return;
        }
        bool publish = _publish.IsChecked == true;
        bool needKey = _settings.Engine == Engines.AnthropicApi && string.IsNullOrWhiteSpace(_settings.AnthropicApiKey);
        if (needKey ||
            (publish && (string.IsNullOrWhiteSpace(_settings.NotionToken) || (NotionPublisher.ParseId(_settings.NotionParentPage) == null && NotionPublisher.ParseId(_settings.NotionDatabase) == null))))
        {
            await MessageBox.ShowAsync(this, publish
                ? "Set your Notion token and Notion database (or parent page) in Settings first" + (needKey ? ", plus your Anthropic API key (or switch the engine to your Claude subscription)." : ".")
                : "Set your Anthropic API key in Settings first, or switch the recipe engine to your Claude subscription.", MessageKind.Warning, "TaoRecipe");
            await new SettingsDialog(_settings).ShowDialog<bool>(this);
            return;
        }

        _cts = new CancellationTokenSource();
        SetBusy(true);
        var progress = new Progress<string>(Log);
        var pipeline = new RecipePipeline(_settings);
        int ok = 0, failed = 0;
        try
        {
            for (int i = 0; i < inputs.Count; i++)
            {
                Log($"=== [{i + 1}/{inputs.Count}] {inputs[i]}");
                try
                {
                    var results = await Task.Run(() => pipeline.ProcessAsync(inputs[i], new PipelineOptions { PublishToNotion = publish }, progress, _cts.Token));
                    foreach (var r in results) AddResult(r);
                    ok++;
                }
                catch (OperationCanceledException)
                {
                    Log("Cancelled.");
                    break;
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or HttpRequestException or UnauthorizedAccessException or Anthropic.Exceptions.AnthropicException)
                {
                    failed++;
                    Log("✘ " + ex.Message);
                }
            }
        }
        finally
        {
            SetBusy(false);
            _cts.Dispose();
            _cts = null;
        }
        Log($"Finished: {ok} video(s) done" + (failed > 0 ? $", {failed} failed (see log)." : "."));
    }

    private void SetBusy(bool busy)
    {
        _start.IsEnabled = !busy;
        _cancel.IsEnabled = busy;
        _inputs.IsReadOnly = busy;
        _progress.IsVisible = busy;
        _progress.IsIndeterminate = busy;
    }

    private void AddResult(RecipeResult r)
    {
        var title = new TextBlock { Text = r.Recipe.Title, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var folder = new Button { Content = "Folder", Classes = { "small" } };
        folder.Click += (_, _) => Open(r.Folder);
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 6 };
        AddAt(row, 0, title);
        AddAt(row, 1, folder);
        if (r.NotionUrl != null)
        {
            var notion = new Button { Content = "Open in Notion", Classes = { "small", "accent" } };
            notion.Click += (_, _) => Open(r.NotionUrl);
            AddAt(row, 2, notion);
        }
        _results.Children.Add(row);
        _resultsPanel.IsVisible = true;
    }

    private static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { /* no handler registered */ }
    }
}
