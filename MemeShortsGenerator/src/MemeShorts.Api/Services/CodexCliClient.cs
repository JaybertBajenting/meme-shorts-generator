using System.Text.Json;
using MemeShorts.Api.Models;
using Microsoft.Extensions.Options;

namespace MemeShorts.Api.Services;

/// <summary>
/// Drives the Codex CLI for (1) the storyboard JSON and (2) each panel PNG.
/// The exact command + base args are configurable so you can wire your proven
/// `codex exec` invocation without touching code.
/// </summary>
public sealed class CodexCliClient : ICodexClient
{
    private readonly CodexOptions _opt;
    private readonly ILogger<CodexCliClient> _log;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public CodexCliClient(IOptions<CodexOptions> opt, ILogger<CodexCliClient> log)
    {
        _opt = opt.Value;
        _log = log;
    }

    public async Task<Storyboard?> GenerateStoryboardAsync(string topic, string? format, IReadOnlyList<string>? styleTitles = null, string contentType = "meme", CancellationToken ct = default)
    {
        var prompt = contentType.Equals("finance", StringComparison.OrdinalIgnoreCase)
            ? Prompts.FinanceStoryboard(topic, styleTitles)
            : Prompts.Storyboard(topic, format, styleTitles);

        for (int attempt = 1; attempt <= 2; attempt++)
        {
            var lastMsgFile = Path.Combine(Path.GetTempPath(), $"codex_sb_{Guid.NewGuid():n}.txt");
            try
            {
                var res = await RunCodexAsync(prompt, workingDir: null, extraArgs: new[] { "-o", lastMsgFile }, ct);
                string output = File.Exists(lastMsgFile)
                    ? await File.ReadAllTextAsync(lastMsgFile, ct)
                    : res.StdOut;

                var json = JsonExtract.FirstJsonObject(output);
                if (json is not null)
                {
                    var storyboard = JsonSerializer.Deserialize<Storyboard>(json, JsonOpts);
                    if (storyboard is { Beats.Count: > 0 }) return storyboard;
                }

                _log.LogWarning(
                    "Codex storyboard attempt {Attempt}/2 returned no usable JSON (exit {Code}). STDERR: {Err} STDOUT: {Out} FILE: {File}",
                    attempt, res.ExitCode, Trunc(res.StdErr), Trunc(res.StdOut), Trunc(output));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Codex storyboard attempt {Attempt}/2 failed.", attempt);
            }
            finally
            {
                try { if (File.Exists(lastMsgFile)) File.Delete(lastMsgFile); } catch { /* ignore */ }
            }

            if (attempt == 1)
                _log.LogInformation("Retrying Codex storyboard once for topic '{Topic}'.", topic);
        }

        return null;
    }

    public async Task<IReadOnlyList<string>> BrainstormTopicsAsync(int count, string contentType, IReadOnlyList<string>? styleTitles = null,
        IReadOnlyList<string>? avoidTopics = null, PerformanceSnapshot? performance = null, CancellationToken ct = default)
    {
        if (count <= 0) return Array.Empty<string>();
        var prompt = Prompts.TopicBrainstorm(count, contentType, styleTitles, avoidTopics, performance);
        var lastMsgFile = Path.Combine(Path.GetTempPath(), $"codex_topics_{Guid.NewGuid():n}.txt");
        try
        {
            var res = await RunCodexAsync(prompt, workingDir: null, extraArgs: new[] { "-o", lastMsgFile }, ct);
            string output = File.Exists(lastMsgFile) ? await File.ReadAllTextAsync(lastMsgFile, ct) : res.StdOut;

            var json = JsonExtract.FirstJsonObject(output);
            if (json is null)
            {
                _log.LogWarning("Codex topic brainstorm returned no JSON (exit {Code}).\nSTDERR: {Err}", res.ExitCode, Trunc(res.StdErr));
                return Array.Empty<string>();
            }

            var parsed = JsonSerializer.Deserialize<TopicList>(json, JsonOpts);
            var topics = (parsed?.Topics ?? new())
                .Select(t => t?.Trim() ?? "")
                .Where(t => t.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(count)
                .ToList();
            return topics;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Codex topic brainstorm failed.");
            return Array.Empty<string>();
        }
        finally
        {
            try { if (File.Exists(lastMsgFile)) File.Delete(lastMsgFile); } catch { /* ignore */ }
        }
    }

    private sealed class TopicList { public List<string>? Topics { get; set; } }

    public async Task<ListIdea?> GenerateListIdeaAsync(IReadOnlyList<string> winningTitles,
        string? seedTopic = null, IReadOnlyList<string>? avoid = null, CancellationToken ct = default)
    {
        // A random steer only when the caller didn't name a subject. Codex answers an identical
        // prompt identically, so "suggest again" needs something in the prompt to actually move.
        var steer = string.IsNullOrWhiteSpace(seedTopic) ? ListSteers[Random.Shared.Next(ListSteers.Length)] : null;
        var prompt = Prompts.ListIdea(winningTitles, seedTopic, avoid, steer);
        var lastMsgFile = Path.Combine(Path.GetTempPath(), $"codex_listidea_{Guid.NewGuid():n}.txt");
        try
        {
            var res = await RunCodexAsync(prompt, workingDir: null, extraArgs: new[] { "-o", lastMsgFile }, ct);
            string output = File.Exists(lastMsgFile) ? await File.ReadAllTextAsync(lastMsgFile, ct) : res.StdOut;

            var json = JsonExtract.FirstJsonObject(output);
            if (json is null)
            {
                _log.LogWarning("Codex list idea returned no JSON (exit {Code}).\nSTDERR: {Err}",
                    res.ExitCode, Trunc(res.StdErr));
                return null;
            }

            var idea = JsonSerializer.Deserialize<ListIdea>(json, JsonOpts);
            if (idea is null) return null;

            // A line with no picture cannot be staged, so drop it rather than fail the whole idea.
            idea.Items = idea.Items
                .Where(i => !string.IsNullOrWhiteSpace(i.Text) && !string.IsNullOrWhiteSpace(i.Image))
                .ToList();
            idea.Title = (idea.Title ?? "").Trim('"', '“', '”', ' ');
            return idea.Items.Count >= 3 ? idea : null;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Codex list idea failed.");
            return null;
        }
        finally
        {
            try { if (File.Exists(lastMsgFile)) File.Delete(lastMsgFile); } catch { /* temp */ }
        }
    }

    public async Task<string?> GenerateRepostTitleAsync(string postText, IReadOnlyList<string>? styleTitles = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(postText)) return null;
        var prompt = Prompts.RepostTitle(postText, styleTitles);
        var lastMsgFile = Path.Combine(Path.GetTempPath(), $"codex_reposttitle_{Guid.NewGuid():n}.txt");
        try
        {
            var res = await RunCodexAsync(prompt, workingDir: null, extraArgs: new[] { "-o", lastMsgFile }, ct);
            string output = File.Exists(lastMsgFile) ? await File.ReadAllTextAsync(lastMsgFile, ct) : res.StdOut;

            var json = JsonExtract.FirstJsonObject(output);
            if (json is null)
            {
                _log.LogWarning("Codex repost title returned no JSON (exit {Code}).\nSTDERR: {Err}", res.ExitCode, Trunc(res.StdErr));
                return null;
            }

            var title = JsonSerializer.Deserialize<TitleOnly>(json, JsonOpts)?.Title?.Trim();
            // Codex sometimes wraps the title in quotes despite being told not to.
            title = title?.Trim('"', '“', '”', ' ');
            return string.IsNullOrWhiteSpace(title) ? null : title;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Codex repost title failed.");
            return null;
        }
        finally
        {
            try { if (File.Exists(lastMsgFile)) File.Delete(lastMsgFile); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// Subject areas to push a fresh suggestion towards. Drawn from the kinds of thing that do
    /// well on this channel, and deliberately concrete — Wikimedia can only supply photographs,
    /// so an area like "typography" would set the picture search up to fail.
    /// </summary>
    private static readonly string[] ListSteers =
    {
        "old machines and gadgets", "animals doing something unexpected", "food and how it is made",
        "buildings and bridges", "coins, stamps and banknotes", "maps and borders",
        "flags and their mistakes", "vehicles — trains, planes, ships", "sport and its odd rules",
        "musical instruments", "tools and how they got their names", "clothing and uniforms",
        "space missions and their hardware", "the sea and what lives in it",
        "castles, ruins and monuments", "toys and games through the years",
        "weather and natural phenomena", "shops, signs and packaging",
        "bridges, tunnels and things that were moved", "plants and trees with strange histories",
    };

    private sealed class TitleOnly { public string? Title { get; set; } }

    public async Task<bool> GenerateStockImageAsync(string subject, string outputPngPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(subject)) return false;
        var dir = Path.GetDirectoryName(outputPngPath)!;
        Directory.CreateDirectory(dir);

        var prompt = Prompts.StockImage(subject);
        var startUtc = DateTime.UtcNow.AddSeconds(-3);
        try
        {
            var res = await RunCodexAsync(prompt, workingDir: dir, extraArgs: null, ct);
            if (File.Exists(outputPngPath) && new FileInfo(outputPngPath).Length > 0) return true;

            var generated = LocateGeneratedImage(res, startUtc);
            if (generated is not null)
            {
                File.Copy(generated, outputPngPath, overwrite: true);
                _log.LogInformation("Codex drew a picture for '{Subject}'.", subject);
                return true;
            }
            _log.LogWarning("Codex produced no image for '{Subject}' (exit {Code}). {Err}",
                subject, res.ExitCode, Trunc(res.StdErr));
            return false;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Codex image generation failed for '{Subject}'.", subject);
            return false;
        }
    }

    public async Task<bool> GeneratePanelAsync(Beat beat, int width, int height, string outputPngPath, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(outputPngPath)!;
        Directory.CreateDirectory(dir);

        var prompt = Prompts.PanelImage(beat, width, height);
        // image_gen writes to $CODEX_HOME/generated_images/<session>/ig_*.png. The Windows
        // sandbox blocks Codex from moving the file, so we locate it by session id and copy.
        var startUtc = DateTime.UtcNow.AddSeconds(-3);

        try
        {
            var res = await RunCodexAsync(prompt, workingDir: dir, extraArgs: null, ct);

            // Codex may have saved straight to the workspace in some setups.
            if (File.Exists(outputPngPath) && new FileInfo(outputPngPath).Length > 0)
                return true;

            var generated = LocateGeneratedImage(res, startUtc);
            if (generated is not null)
            {
                File.Copy(generated, outputPngPath, overwrite: true);
                return true;
            }

            _log.LogWarning("Codex panel: no image found for '{Caption}' (exit {Code}). {Err}",
                beat.Caption, res.ExitCode, Trunc(res.StdErr));
            return false;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Codex panel generation failed for beat '{Caption}'.", beat.Caption);
            return false;
        }
    }

    /// <summary>Find the PNG that image_gen just produced for this Codex session.</summary>
    private string? LocateGeneratedImage(ProcessResult res, DateTime startUtc)
    {
        var genRoot = GeneratedImagesRoot();
        if (!Directory.Exists(genRoot)) return null;

        // Prefer the session-specific folder (safe under concurrent panel generation).
        var combined = res.StdOut + "\n" + res.StdErr;
        var m = System.Text.RegularExpressions.Regex.Match(combined, @"session id:\s*([0-9a-fA-F-]{36})");
        if (m.Success)
        {
            var sessDir = Path.Combine(genRoot, m.Groups[1].Value);
            if (Directory.Exists(sessDir))
            {
                var inSession = new DirectoryInfo(sessDir).GetFiles("ig_*.png")
                    .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
                if (inSession is not null) return inSession.FullName;
            }
        }

        // Fallback: newest ig_*.png produced after this call started.
        return new DirectoryInfo(genRoot).GetFiles("ig_*.png", SearchOption.AllDirectories)
            .Where(f => f.LastWriteTimeUtc >= startUtc)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault()?.FullName;
    }

    private string GeneratedImagesRoot()
    {
        var home = !string.IsNullOrWhiteSpace(_opt.CodexHome)
            ? _opt.CodexHome
            : (Environment.GetEnvironmentVariable("CODEX_HOME")
               ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"));
        return Path.Combine(home, _opt.GeneratedImagesDir);
    }

    private Task<ProcessResult> RunCodexAsync(string prompt, string? workingDir, IEnumerable<string>? extraArgs, CancellationToken ct)
    {
        // ArgumentList passes the (multi-line) prompt as a single argv entry — no quoting headaches.
        var baseArgs = _opt.BaseArgs is { Length: > 0 } ? _opt.BaseArgs : CodexOptions.DefaultBaseArgs;
        var args = new List<string>(baseArgs);
        if (!string.IsNullOrWhiteSpace(_opt.Model))
        {
            args.Add("-m");
            args.Add(_opt.Model);
        }
        // Lower reasoning effort = far faster generation (config flag must precede the prompt).
        if (!string.IsNullOrWhiteSpace(_opt.ReasoningEffort))
        {
            args.Add("-c");
            args.Add($"model_reasoning_effort=\"{_opt.ReasoningEffort}\"");
        }
        if (extraArgs is not null) args.AddRange(extraArgs);
        args.Add(prompt);
        return ProcessRunner.RunAsync(
            _opt.Command, args, workingDir,
            TimeSpan.FromSeconds(_opt.TimeoutSeconds), ct);
    }

    private static string Trunc(string s, int n = 600) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n] + "…");
}
