---
name: codex-cli-noninteractive-gotchas
description: Non-obvious fixes for driving Codex CLI (codex exec) from code on Windows
metadata: 
  node_type: memory
  type: reference
  originSessionId: 3a7ce298-656e-4a2b-91f1-77d9296cf5c5
  modified: 2026-09-28T04:04:06.340Z
---

Driving `codex exec` (v0.137, model gpt-5.5) programmatically on Windows hit three
non-obvious failures, all fixed in [[meme-shorts-generator-project]]'s `ProcessRunner`
and `CodexCliClient`:

1. **Hangs/exits on stdin.** `codex exec` appends stdin to the prompt and BLOCKS until
   stdin EOF. From a child process you must redirect stdin and close it immediately
   (`RedirectStandardInput=true` then `StandardInput.Close()`), or it hangs/errors.
   Manual repro: `codex exec "..." < /dev/null` works; without it, hangs.

2. **It echoes the prompt + a banner to stdout.** Don't grep stdout for the JSON answer
   (the prompt's own JSON schema gets matched first). Use `-o <file>` /
   `--output-last-message <file>` to get ONLY the final agent message, then parse that.

3. **Don't launch `.cmd`/`.exe`-less shims directly** — `npx`/`npm` are `.cmd`; .NET
   `Process.Start` can't run them, route through `cmd.exe /d /s /c`. (`codex.exe`
   resolves fine.)

4. **Image panels = Codex built-in `image_gen` tool** (NOT scripts). Matthew's working
   method. His config defaults to `sandbox: workspace-write`, but on Windows that sandbox
   throws `windows sandbox: spawn setup refresh` on ANY process spawn — so Codex can't run
   Python/Pillow, and can't move/verify the file. The built-in `image_gen` tool itself
   works fine (it's internal, no spawn) and writes a ~1MB PNG to
   `$CODEX_HOME/generated_images/<session-id>/ig_*.png` (his `$CODEX_HOME` =
   `D:\Users\M4tth3w\Documents\.codex`). So: prompt "use your built-in image_gen tool, do
   NOT run scripts", parse `session id:` from stdout, then locate+copy `ig_*.png` from that
   session folder yourself. Each panel ~60-120s; parallelize.

5. **His codex config defaults to `reasoning effort: xhigh`** → dense/strict-math prompts (the
   finance storyboard) reason for 5+ min and blow past the timeout → silent fallback to generic
   content. Override per call with `-c model_reasoning_effort="medium"` (config flag, must precede
   the prompt; flag IS accepted by `codex exec`). Dropped finance storyboard 300s+ → ~70s.

6. **Always pass `-m gpt-5.5` explicitly.** As of 2026-08-18 his `config.toml` default model is
   `gpt-5.6-luna`, which the INSTALLED CLI rejects: every request 400s with "The 'gpt-5.6-luna'
   model requires a newer version of Codex", the run still **exits 0**, and you get a log full of
   the echoed prompt and no output. The app already pins `Model: "gpt-5.5"` in appsettings, so
   this only bites hand-rolled `codex exec` calls from the shell.

7. **CLI v0.155 renamed its image files (found 2026-09-26).** image_gen output is now
   `generated_images/<session>/call_<id>.png` or `exec-<id>.png`, not `ig_*.png`, and when told
   to "save the PNG" it also drops a copy into its WORKING DIR under its own name. The app's
   `ig_*.png` filter therefore found nothing and silently threw away EVERY drawn picture as
   "Codex produced no image" (exit 0) - list shorts fell back to loose Commons matches, meme
   panels to the local fallback. Fixed in `CodexCliClient`: accept any png in the call's own
   session folder, and run each call in a private `_codex_<id>` workspace so a file it saves
   there is unambiguously its own. Do NOT widen the global "newest png" fallback: other Codex
   sessions on this machine write exec-*.png into the same root continuously.
   **Symptom to recognise:** a stray `<subject>-....png` sitting in a job folder next to a log
   line saying Codex drew nothing.

8. **Live web search is a TOP-LEVEL flag: `codex --search exec ...`** (found 2026-09-27, v0.155.1).
   `codex exec --help` does not list it; put it before `exec`. Works non-interactively with the
   app's CODEX_HOME: ~40s, returned five real gadgets from that month with working source links
   (spot-checked two). `CodexCliClient.RunCodexAsync(..., search: true)` inserts it at argv[0].
   Fetch tools here cannot read arstechnica.com - verify its claims via other coverage.

9. **Every extra agent turn resends ~14-19k tokens of context** (Codex's own ~21k-char
   instructions + tool schemas + our prompt). Measured from `$CODEX_HOME/sessions/**/rollout-*.jsonl`
   (`token_count` events; scratchpad `codex_tokens.py`): drawings were 95% of the app's use at
   ~96k tokens each, because "Save the PNG and tell me the path" made Codex read its imagegen
   SKILL.md, draw, then run ~3 shell commands to find/copy/verify - 5 turns. "Call image_gen ONCE,
   reply done, no skill file, no shell commands" -> 2 turns, **~30.6k median (13 drawings, 2026-09-28)**,
   same picture quality; the image lands in generated_images/<session>/ by itself. Plus-plan limit
   (shared by text and images) lasted ~40 drawings at the old cost. Image-tool limit errors look
   like `usage_limit_reached ... "resets_at":<unix>`, not "try again at".

Separate .NET gotcha (recurring): the config binder **appends** an appsettings array to a
non-empty C# default → duplicates (`--skip-git-repo-check` twice → error; Upload `Tags`/
`PublishTimesLocal` doubled). Default ALL such option arrays to `Array.Empty<T>()` and supply a
`Default*` fallback used in code. Bitten on BaseArgs, Tags, PublishTimesLocal so far.
