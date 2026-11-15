using System.Diagnostics;
using System.Text;

namespace TaoRecipe.Core;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);

internal static class ProcessRunner
{
    /// <summary>Runs a process with an argument list (no shell), collecting output. Optional per-line callback for stderr.</summary>
    public static async Task<ProcessResult> RunAsync(string exe, IEnumerable<string> args, CancellationToken ct,
        Action<string>? onStdErrLine = null, Action<string>? onStdOutLine = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";

        using var p = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is null) return; stdout.AppendLine(e.Data); onStdOutLine?.Invoke(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is null) return; stderr.AppendLine(e.Data); onStdErrLine?.Invoke(e.Data); };
        try
        {
            p.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException($"Could not start '{exe}'. Is it installed and on PATH? ({ex.Message})", ex);
        }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try
        {
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already exited */ }
            throw;
        }
        return new ProcessResult(p.ExitCode, stdout.ToString(), stderr.ToString());
    }

    /// <summary>Runs a process and returns raw stdout bytes (used for ffmpeg rawvideo output).</summary>
    public static async Task<byte[]> RunBytesAsync(string exe, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = new Process { StartInfo = psi };
        p.Start();
        p.ErrorDataReceived += (_, _) => { };
        p.BeginErrorReadLine();
        using var ms = new MemoryStream();
        try
        {
            await p.StandardOutput.BaseStream.CopyToAsync(ms, ct).ConfigureAwait(false);
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already exited */ }
            throw;
        }
        return ms.ToArray();
    }

    /// <summary>Splits a user-entered extra-arguments string, honouring double quotes.</summary>
    public static List<string> SplitArgs(string s)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(s)) return list;
        var cur = new StringBuilder();
        bool quoted = false;
        foreach (var c in s)
        {
            if (c == '"') { quoted = !quoted; continue; }
            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (cur.Length > 0) { list.Add(cur.ToString()); cur.Clear(); }
                continue;
            }
            cur.Append(c);
        }
        if (cur.Length > 0) list.Add(cur.ToString());
        return list;
    }
}
