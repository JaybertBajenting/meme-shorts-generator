using MemeShorts.Api.Models;
using Microsoft.Extensions.Options;

namespace MemeShorts.Api.Services;

public interface IVideoRenderer
{
    /// <summary>Render the composition for a job. Returns the absolute output mp4 path.</summary>
    Task<string> RenderAsync(string propsJsonPath, string publicDir, string outputMp4Path, CancellationToken ct = default);
}

/// <summary>
/// Invokes the Remotion CLI to render the final short.
///   npx remotion render &lt;entry&gt; &lt;comp&gt; &lt;out.mp4&gt; --props=props.json --public-dir=&lt;jobdir&gt;
/// The job dir is used as Remotion's public dir, so the composition references
/// panels/audio/bg by bare filename via staticFile().
/// </summary>
public sealed class RemotionRenderer : IVideoRenderer
{
    private readonly RemotionOptions _opt;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<RemotionRenderer> _log;

    public RemotionRenderer(IOptions<RemotionOptions> opt, IWebHostEnvironment env, ILogger<RemotionRenderer> log)
    {
        _opt = opt.Value;
        _env = env;
        _log = log;
    }

    public async Task<string> RenderAsync(string propsJsonPath, string publicDir, string outputMp4Path, CancellationToken ct = default)
    {
        var projectDir = Path.IsPathRooted(_opt.ProjectDir)
            ? _opt.ProjectDir
            : Path.GetFullPath(Path.Combine(_env.ContentRootPath, _opt.ProjectDir));
        if (!Directory.Exists(projectDir))
            throw new DirectoryNotFoundException($"Remotion project not found at '{projectDir}'. Run `npm install` there first.");

        Directory.CreateDirectory(Path.GetDirectoryName(outputMp4Path)!);

        // Args are rebuilt per attempt so a retry can BACK OFF. Previously the loop re-ran the
        // identical command, which meant an out-of-memory crash simply reproduced itself and
        // "retrying" only wasted several minutes of rendering.
        List<string> BuildArgs(int concurrency, string? imageFormatOverride, int cacheMb)
        {
            var format = (imageFormatOverride ?? _opt.ImageFormat)?.Trim();
            var a = new List<string>
            {
                "remotion", "render",
                _opt.Entry,
                _opt.CompositionId,
                Path.GetFullPath(outputMp4Path),
                $"--props={Path.GetFullPath(propsJsonPath)}",
                $"--public-dir={Path.GetFullPath(publicDir)}",
                $"--concurrency={concurrency}",
                // Generous delayRender timeout: the asset server can stall briefly serving
                // bg.mp4 frames / audio under load, which otherwise fails the whole render.
                $"--timeout={_opt.TimeoutMs}",
            };
            // Bounds the decoded-background-frame cache; without it the compositor OOMs on long
            // renders. See RemotionOptions.OffthreadVideoCacheMb.
            if (cacheMb > 0)
                a.Add($"--offthreadvideo-cache-size-in-bytes={(long)cacheMb * 1024 * 1024}");
            if (!string.IsNullOrWhiteSpace(format)) a.Add($"--image-format={format}");
            if (!string.IsNullOrWhiteSpace(_opt.JpegQuality) && format?.ToLowerInvariant() == "jpeg")
                a.Add($"--jpeg-quality={_opt.JpegQuality.Trim()}");
            if (!string.IsNullOrWhiteSpace(_opt.Crf)) a.Add($"--crf={_opt.Crf.Trim()}");
            return a;
        }

        var startConcurrency = int.TryParse(_opt.Concurrency, out var c) ? Math.Max(1, c) : 4;

        // Renderers alive before we start. Anything beyond this list is ours, and if an attempt
        // fails we are responsible for reaping it — see KillStrayRenderers.
        var preexisting = RendererPids();

        ProcessResult res = default!;
        var attempts = Math.Max(1, _opt.MaxAttempts);
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            // Halve concurrency each retry (4 -> 2 -> 1). Peak memory is dominated by the
            // parallel Chrome tabs plus the compositor decoding background-video frames, so
            // this is the lever that actually changes the outcome.
            var concurrency = Math.Max(1, startConcurrency >> (attempt - 1));
            // PNG frames are far heavier in memory and temp than JPEG. After the first failure,
            // drop to JPEG as well — a slight quality trade beats not producing a video.
            var format = attempt > 1 ? "jpeg" : null;
            // Shrink the decoded-background cache alongside concurrency. Holding it at a flat
            // gigabyte across every retry left nothing for the x264 encoder, which then died
            // failing to allocate SEVEN megabytes — so the retries had no chance of succeeding
            // where the first attempt had not.
            var cacheMb = Math.Max(128, _opt.OffthreadVideoCacheMb >> (attempt - 1));
            var args = BuildArgs(concurrency, format, cacheMb);

            _log.LogInformation("Remotion render (attempt {N}/{Max}, concurrency {C}, cache {Cache}MB{Fmt}).",
                attempt, attempts, concurrency, cacheMb, format is null ? "" : $", {format} frames");

            res = await ProcessRunner.RunAsync(
                _opt.NpxCommand, args, projectDir,
                TimeSpan.FromSeconds(_opt.TimeoutSeconds), ct, RenderTempEnv(projectDir));

            if (res.Ok && File.Exists(outputMp4Path))
            {
                // The CLI returns once the mp4 is written, so any compositor still up is a
                // leftover — reaped here too, or a clean render still leaves memory behind.
                KillStrayRenderers(preexisting);
                return Path.GetFullPath(outputMp4Path);
            }

            var oom = LooksLikeOutOfMemory(res.StdErr);
            _log.LogWarning("Remotion render attempt {N} failed (exit {Code}){Oom}.",
                attempt, res.ExitCode, oom ? " — out of memory; backing off concurrency" : "");
            try { if (File.Exists(outputMp4Path)) File.Delete(outputMp4Path); } catch { /* clean partial */ }

            // A failed render does not always take its compositor down with it. Each orphan
            // holds ~500MB, so on a machine already short of memory the leak from one failure
            // is what makes the NEXT attempt fail — the retries then look like a code bug
            // rather than the memory exhaustion they are.
            KillStrayRenderers(preexisting);
        }

        var hint = LooksLikeOutOfDisk(res.StdErr)
            ? " THE DISK IS FULL — this is not a fault in the video. Free space on the drive " +
              "holding render-tmp and render it again."
            : LooksLikeOutOfMemory(res.StdErr)
                ? " The compositor ran out of memory. Close other apps, lower Remotion:Concurrency, " +
                  "or set Remotion:ImageFormat=jpeg — PNG frames cost far more memory."
                : "";
        throw new InvalidOperationException(
            $"Remotion render failed after {attempts} attempt(s) (exit {res.ExitCode}).{hint}\n" +
            $"STDERR:\n{Ends(res.StdErr)}\nSTDOUT(tail):\n{Tail(res.StdOut)}");
    }

    /// <summary>
    /// Put the renderer's scratch space on the same drive as the project, when that drive has
    /// more room than the system temp does.
    ///
    /// A render writes every frame to temp before encoding. With the system drive down to its
    /// last few hundred megabytes, Chrome could not write a screenshot and the render died —
    /// reported as "ran out of memory or disk space", which sent the search off after memory
    /// while the actual shortage was disk.
    /// </summary>
    private Dictionary<string, string>? RenderTempEnv(string projectDir)
    {
        try
        {
            var systemTemp = Path.GetTempPath();
            var systemFree = FreeBytesOn(systemTemp);
            var projectFree = FreeBytesOn(projectDir);

            // Only move if it is a genuine improvement and the system drive is actually tight.
            const long tight = 3L * 1024 * 1024 * 1024;
            if (systemFree >= tight || projectFree <= systemFree) return null;

            var dir = Path.Combine(Path.GetFullPath(projectDir), ".render-tmp");
            Directory.CreateDirectory(dir);
            _log.LogInformation(
                "System temp has only {SysGB:N1} GB free; rendering via {Dir} ({DirGB:N1} GB free).",
                systemFree / 1024d / 1024 / 1024, dir, projectFree / 1024d / 1024 / 1024);

            return new Dictionary<string, string>
            {
                ["TMP"] = dir,
                ["TEMP"] = dir,
                ["TMPDIR"] = dir,
            };
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Could not redirect the render temp directory; using the default.");
            return null;
        }

        static long FreeBytesOn(string path) =>
            new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace;
    }

    /// <summary>PIDs of the Remotion compositor processes currently alive.</summary>
    private static int[] RendererPids()
    {
        try
        {
            return System.Diagnostics.Process.GetProcessesByName("remotion")
                .Select(p => { var id = p.Id; p.Dispose(); return id; })
                .ToArray();
        }
        catch
        {
            return Array.Empty<int>();
        }
    }

    /// <summary>
    /// Kill compositor processes that appeared during this render and outlived it. Only ones we
    /// started are touched — anything already running when we began is left alone, so a render
    /// running elsewhere is never pulled out from under itself.
    /// </summary>
    private void KillStrayRenderers(int[] preexisting)
    {
        System.Diagnostics.Process[] alive;
        try { alive = System.Diagnostics.Process.GetProcessesByName("remotion"); }
        catch { return; }

        var reaped = 0;
        long freedMb = 0;
        foreach (var p in alive)
        {
            try
            {
                if (preexisting.Contains(p.Id)) continue;
                freedMb += p.WorkingSet64 / (1024 * 1024);
                p.Kill(entireProcessTree: true);
                reaped++;
            }
            catch { /* already gone, or not ours to kill */ }
            finally { p.Dispose(); }
        }

        if (reaped > 0)
            _log.LogInformation("Reaped {N} orphaned renderer process(es), ~{Mb} MB.", reaped, freedMb);
    }

    /// <summary>
    /// Remotion's Rust compositor dies with exit 3221226505 (0xC0000409) and a "memory
    /// allocation of N bytes failed" line when it runs out of memory decoding background-video
    /// frames. Detected so the log says WHY rather than just showing an opaque exit code.
    /// </summary>
    private static bool LooksLikeOutOfMemory(string? stderr) =>
        !string.IsNullOrEmpty(stderr)
        && (stderr.Contains("memory allocation of", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("3221226505", StringComparison.Ordinal)
            || stderr.Contains("out of memory", StringComparison.OrdinalIgnoreCase)
            // x264 words its own allocation failures differently, so a render that died in the
            // ENCODER rather than the compositor was not being reported as a memory problem at
            // all — it just looked like an opaque exit 1.
            || stderr.Contains("malloc of size", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("cannot allocate memory", StringComparison.OrdinalIgnoreCase));

    private static string Tail(string s, int n = 1500) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : "…" + s[^n..]);

    /// <summary>
    /// Keep BOTH ends of a child process's error output.
    ///
    /// Node puts the diagnosis on the FIRST line - "ENOSPC: no space left on device, copyfile
    /// ...bg.mp4" - and a dozen stack frames after it. Keeping only the tail threw the diagnosis
    /// away and kept the frames, so a full disk was reported as an opaque "render failed" that
    /// read like a bug in the video and was nothing of the kind.
    /// </summary>
    private static string Ends(string s, int head = 700, int tail = 900) =>
        string.IsNullOrEmpty(s) ? ""
        : s.Length <= head + tail ? s
        : s[..head] + "\n…\n" + s[^tail..];

    /// <summary>
    /// A full disk, which Remotion hits at its very first file copy and reports as a bare exit 1.
    /// </summary>
    private static bool LooksLikeOutOfDisk(string stderr) =>
        !string.IsNullOrEmpty(stderr)
        && (stderr.Contains("ENOSPC", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("no space left", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("not enough space on the disk", StringComparison.OrdinalIgnoreCase));
}
