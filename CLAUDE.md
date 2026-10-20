# CLAUDE.md — Meme Shorts Generator

Type a topic, get a finished vertical "rage-comic / brainrot" Short. An ASP.NET Core API runs the
pipeline as a background job: Codex CLI writes a storyboard and draws the panels, TTS voices each beat,
faster-whisper times the captions, and Remotion renders the video. Finished Shorts can be scheduled to
YouTube, Facebook Reels and Instagram Reels. Everything is under `MemeShortsGenerator/`; its `README.md`
documents every setting.

## Commands

Run from `MemeShortsGenerator/`.

```powershell
npm --prefix remotion install                       # once
dotnet run --project src/MemeShorts.Api             # API + built UI on http://localhost:5180
npm --prefix frontend install; npm --prefix frontend run dev   # UI dev server on 5173, proxies /api
npm --prefix frontend run build                     # builds the UI into src/MemeShorts.Api/wwwroot
npm --prefix remotion run typecheck                 # tsc for the compositor
python tools/corpus_check.py                        # regression check for the screenshot slicer
./launch.ps1                                        # start the API if needed and open the browser
```

There is no C# test project; `tools/corpus_check.py` compares the slicer against a committed baseline
and exits non-zero on regressions (`--write` accepts new numbers).

## Layout

```
MemeShortsGenerator/
  src/MemeShorts.Api/            .NET 8 (net8.0-windows) API; serves wwwroot
    Program.cs                   endpoints and DI
    Services/PipelineService     storyboard -> panels -> TTS -> captions -> props.json -> Remotion
    Services/Prompts.cs          the storyboard and panel prompt templates
    Services/CodexCliClient, CodexQuota          Codex CLI calls and quota handling
    Services/WindowsTtsService, ElevenLabsTtsService, WhisperCaptionService
    Services/RemotionRenderer, RenderValidator   render with retries, then check the mp4
    Services/UploadScheduler, YouTubeUploadService, FacebookReelsUploader, InstagramReelsUploader,
             SocialPublishWorker                 slot scheduling and publishing
    Services/TopicQualityGate, ChannelPerformanceService   topic filtering steered by real view counts
  frontend/                      React + Vite + TanStack Query UI (Generate, Review, Calendar, Settings)
  remotion/                      Remotion compositor: panels, captions, background loop
  tools/                         Python regression and sweep scripts
  out/<jobId>/                   per-job working folder (gitignored)
```

## Invariants

- `props.json` handed to Remotion is BOM-less UTF-8.
- `Pipeline:BeatGapMs` stays above 80 ms (caption page boundary).
- Keep `Pipeline:MaxConcurrentJobs` low; renders crash under contention.
- Visuals and animation are deterministic in `remotion/`; only the storyboard and panels come from Codex.
- A failed Codex call falls back to built-in seeds or local panels where allowed, and the API reports
  `fromCodex` vs `fromFallbackSeeds` so the fallback is never silent.
- Publishing destinations are independent; Retry re-runs only the failed ones so a live post is never
  duplicated. Instagram has no scheduling API, so the app itself posts at the slot.

## Conventions

- Document the reason and the evidence for tuning decisions (the README records what the channel's own
  view counts showed; do not re-add everyday premises to `TopicQualityGate.HardRejects`).
- Settings live in `appsettings.json`; secrets live in `.env` (copy `.env.example`) or the Settings tab,
  and the API only ever returns a masked token tail.

## Never

- Commit `.env`, `secrets/` (YouTube OAuth client and cached token), Page or Instagram tokens, the
  SQLite state (`state.db`), rendered jobs (`out/`), or the gameplay backgrounds and music under
  `assets/backgrounds` and `assets/music` (all covered by `.gitignore`; keep it that way).
