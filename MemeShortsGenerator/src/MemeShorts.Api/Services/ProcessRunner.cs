using System.Diagnostics;
using System.Text;

namespace MemeShorts.Api.Services;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;
}

/// <summary>Runs an external process, capturing stdout/stderr with a timeout.</summary>
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> args,
        string? workingDir,
        TimeSpan timeout,
        CancellationToken ct = default,
        IReadOnlyDictionary<string, string>? env = null)
    {
        var argList = args.ToList();

        // Windows: npm/npx/yarn/pnpm are .cmd shims, and .NET's CreateProcess can't
        // launch a .cmd directly — route them through cmd.exe.
        if (OperatingSystem.IsWindows() && NeedsCmdShim(fileName))
        {
            argList = new List<string> { "/d", "/s", "/c", fileName }.Concat(argList).ToList();
            fileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        }

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardInput = true, // closed immediately so tools that read stdin (codex exec) get EOF instead of hanging
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDir ?? Directory.GetCurrentDirectory(),
        };
        foreach (var a in argList) psi.ArgumentList.Add(a);
        if (env is not null)
            foreach (var (k, v) in env) psi.Environment[k] = v;

        using var proc = new Process { StartInfo = psi };
        var outSb = new StringBuilder();
        var errSb = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) outSb.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) errSb.AppendLine(e.Data); };

        if (!proc.Start())
            throw new InvalidOperationException($"Failed to start process: {fileName}");

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        try { proc.StandardInput.Close(); } catch { /* EOF to child's stdin */ }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new TimeoutException($"'{fileName}' timed out after {timeout.TotalSeconds:0}s.");
        }

        return new ProcessResult(proc.ExitCode, outSb.ToString(), errSb.ToString());
    }

    private static readonly HashSet<string> ShimNames =
        new(StringComparer.OrdinalIgnoreCase) { "npx", "npm", "yarn", "pnpm" };

    private static bool NeedsCmdShim(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        if (ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".bat", StringComparison.OrdinalIgnoreCase))
            return true;
        // bare "npx" etc. (no extension) — resolves to npx.cmd on PATH
        return string.IsNullOrEmpty(ext) && ShimNames.Contains(Path.GetFileName(fileName));
    }
}
