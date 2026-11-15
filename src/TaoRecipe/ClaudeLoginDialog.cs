using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using TaoRecipe.Core;
using TaoRecipe.Dialogs;

namespace TaoRecipe;

/// <summary>Asks the user to sign in to their Claude subscription and waits until Claude Code reports a login.</summary>
public sealed class ClaudeLoginDialog : DialogWindow
{
    private readonly AppSettings _settings;
    private readonly TextBlock _status;
    private readonly DispatcherTimer _poll;
    private bool _checking;

    private ClaudeLoginDialog(AppSettings settings, ClaudeLoginStatus current) : base("Sign in to Claude", 500)
    {
        _settings = settings;
        _status = new TextBlock { Text = ClaudeAccount.Describe(current), TextWrapping = TextWrapping.Wrap, Classes = { "muted" } };

        var signIn = new Button { Content = "Sign in…", Classes = { "accent" } };
        signIn.Click += (_, _) =>
        {
            if (ClaudeAccount.StartLogin(_settings) == null)
            {
                _status.Text = "✘ Could not start Claude Code. Check its path in Settings.";
                return;
            }
            _status.Text = "A console window and your browser opened. Sign in with your Claude account there; this window closes by itself when you're done.";
            _poll!.Start();
        };

        var body = Column(12,
            Label("TaoRecipe writes recipes with your Claude subscription (Pro or Max) through Claude Code, at no extra cost. Sign in once with your Claude account."),
            Row(10, signIn),
            _status);
        SetBody(body, "Done", "Cancel");

        _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _poll.Tick += async (_, _) => await CheckAsync();
        Closed += (_, _) => _poll.Stop();
    }

    private async Task CheckAsync()
    {
        if (_checking) return;
        _checking = true;
        try
        {
            var st = await ClaudeAccount.GetStatusAsync(_settings, CancellationToken.None);
            if (st.IsSubscription)
            {
                _poll.Stop();
                Close(true);
            }
            else if (st.LoggedIn)
            {
                _status.Text = ClaudeAccount.Describe(st);
            }
        }
        finally
        {
            _checking = false;
        }
    }

    protected override async Task<bool> ValidateAsync()
    {
        var st = await ClaudeAccount.GetStatusAsync(_settings, CancellationToken.None);
        _status.Text = ClaudeAccount.Describe(st);
        return st.LoggedIn;
    }

    /// <summary>Returns true when Claude Code is signed in (asking the user to sign in first if needed).</summary>
    public static async Task<bool> EnsureSignedInAsync(Window owner, AppSettings settings)
    {
        var st = await ClaudeAccount.GetStatusAsync(settings, CancellationToken.None);
        if (st.LoggedIn) return true;
        if (!st.Installed)
        {
            await MessageBox.ShowAsync(owner,
                "The free recipe engine needs Claude Code (claude.com/claude-code). Install it, or set its path in Settings, or switch the engine to an Anthropic API key.",
                MessageKind.Warning, "TaoRecipe");
            return false;
        }
        return await new ClaudeLoginDialog(settings, st).ShowDialog<bool>(owner);
    }

    public static async Task ShowAsync(Window owner, AppSettings settings)
    {
        var st = await ClaudeAccount.GetStatusAsync(settings, CancellationToken.None);
        await new ClaudeLoginDialog(settings, st).ShowDialog<bool>(owner);
    }
}
