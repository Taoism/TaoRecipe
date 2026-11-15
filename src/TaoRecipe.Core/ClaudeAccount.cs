using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TaoRecipe.Core;

public sealed record ClaudeLoginStatus(bool Installed, bool LoggedIn, string AuthMethod, string? Email)
{
    public bool IsSubscription => LoggedIn && AuthMethod == "claude.ai";
}

/// <summary>Sign-in state of the local Claude Code CLI, which the subscription engine runs on.</summary>
public static class ClaudeAccount
{
    private static readonly string[] StrippedEnv = ["ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN"];

    public static async Task<ClaudeLoginStatus> GetStatusAsync(AppSettings s, CancellationToken ct)
    {
        try
        {
            var res = await ProcessRunner.RunAsync(s.ClaudeCodePath, ["auth", "status"], ct, removeEnv: StrippedEnv).ConfigureAwait(false);
            var start = res.StdOut.IndexOf('{');
            if (start < 0) return new ClaudeLoginStatus(true, false, "", null);
            var json = JsonNode.Parse(res.StdOut[start..]);
            return new ClaudeLoginStatus(true,
                json?["loggedIn"]?.GetValue<bool>() == true,
                json?["authMethod"]?.GetValue<string>() ?? "",
                json?["email"]?.GetValue<string>());
        }
        catch (InvalidOperationException)
        {
            return new ClaudeLoginStatus(false, false, "", null);
        }
        catch (JsonException)
        {
            return new ClaudeLoginStatus(true, false, "", null);
        }
    }

    /// <summary>
    /// Opens a console window running <c>claude auth login --claudeai</c>; it opens the browser for the Claude sign-in.
    /// Poll <see cref="GetStatusAsync"/> to see when it is done.
    /// </summary>
    public static Process? StartLogin(AppSettings s)
    {
        ProcessStartInfo psi;
        if (OperatingSystem.IsWindows())
        {
            // cmd keeps the window open on errors so the user can read them
            // raw string: cmd does not understand the \" escaping ArgumentList would produce
            psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = false,
                Arguments = $"/c title Sign in to Claude & \"{s.ClaudeCodePath}\" auth login --claudeai || pause",
            };
        }
        else
        {
            psi = new ProcessStartInfo(s.ClaudeCodePath) { UseShellExecute = false };
            psi.ArgumentList.Add("auth");
            psi.ArgumentList.Add("login");
            psi.ArgumentList.Add("--claudeai");
        }
        foreach (var name in StrippedEnv) psi.Environment.Remove(name);
        try
        {
            return Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    public static string Describe(ClaudeLoginStatus st) => st switch
    {
        { Installed: false } => "✘ Claude Code is not installed (claude.com/claude-code) or its path is wrong.",
        { LoggedIn: false } => "✘ Not signed in to Claude.",
        { IsSubscription: true } => "✔ Signed in with your Claude subscription" + (st.Email != null ? $" ({st.Email})." : "."),
        _ => $"⚠ Signed in via {st.AuthMethod}: this bills API credit, not your subscription. Sign in again with your Claude account.",
    };
}
