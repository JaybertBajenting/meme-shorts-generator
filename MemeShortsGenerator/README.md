# 🧠 Meme Shorts Generator

Type a topic → get a vertical "rage-comic / brainrot" short, fully automated:

```
Topic ─▶ ASP.NET Core API + Web UI
           │  POST /api/generate {topic}
           ▼
       PipelineService (background job)
           1. Codex CLI ──▶ storyboard.json   (beats; deterministic fallback if Codex unavailable)
           2. Codex CLI ──▶ panel_N.png       (rage-comic cards; local fallback renderer)
           3. Windows TTS ──▶ beat_N.wav       (System.Speech; ElevenLabs ready to swap in)
           4. faster-whisper ──▶ word-level captions (real timing; even-spread fallback)
           5. props.json (BOM-less UTF-8)
           ▼
       Remotion (Node) ──▶ short.mp4   (looped bg + tilted/stacked pop-in panels
                                         + @remotion/captions word highlight + fake comment)
           ▼
       UI polls status ──▶ plays the result
```

## Layout

```
MemeShortsGenerator/
  src/MemeShorts.Api/      # ASP.NET Core (net8.0-windows) API + web UI (wwwroot/)
  remotion/                # Remotion compositor (Node) — renders the final mp4
  assets/backgrounds/      # drop gameplay .mp4 clips here (most-recent is used)
  out/<jobId>/             # per-job working dir: panels, audio, props.json, short.mp4
```

## Prerequisites

- **.NET 8 SDK** (or newer) — `dotnet --version`
- **Node 18+** + npm — for Remotion
- **Codex CLI** on PATH — `codex --version` (optional; pipeline falls back without it)
- **Windows** — System.Speech (SAPI) TTS is Windows-only

## Setup

```powershell
# 1. Install the Remotion compositor's deps (once)
cd remotion
npm install
cd ..

# 2. (optional) add a background clip
#    copy a vertical gameplay mp4 into assets/backgrounds/

# 3. Run the API + UI
dotnet run --project src/MemeShorts.Api
```

Open <http://localhost:5180>, type a topic, hit **Generate short**.

## Configuration — `src/MemeShorts.Api/appsettings.json`

| Section            | Key                 | Notes                                                        |
|--------------------|---------------------|-------------------------------------------------------------|
| `Tts`              | `Provider`          | `"Windows"` (default) or `"ElevenLabs"`                     |
| `Tts`              | `WindowsVoice`      | substring of an installed SAPI voice (e.g. `"Zira"`)        |
| `Tts`              | `WindowsRate`       | -10..10 (brainrot pacing ≈ +2..+4)                          |
| `Tts`              | `ElevenLabsApiKey` / `ElevenLabsVoiceId` | fill in when you have credits          |
| `Codex`            | `PanelSource`       | `"Codex"` (CLI generates panels) or `"Local"` (offline)    |
| `Codex`            | `Model`             | explicit model slug used for storyboards and panels          |
| `Codex`            | `BaseArgs`          | args before the prompt, e.g. `["exec","--skip-git-repo-check"]` |
| `Codex`            | `FallbackOnError`   | fall back to local/deterministic instead of failing the job |
| `Captions`         | `Source`            | `"Whisper"` (real word timing) or `"Even"` (length spread)  |
| `Captions`         | `PythonPath`        | python.exe with `faster-whisper` installed                  |
| `Captions`         | `WhisperModel`      | `"small.en"` (matches your existing workflow; cached)       |
| `Pipeline`         | `BeatGapMs`         | silence between beats; **keep > 80** (caption page boundary) |
| `Pipeline`         | `MaxConcurrentJobs` | jobs run at once; keep low — renders crash under contention (`1` = serial) |
| `Remotion`         | `Concurrency`       | render threads (default `"4"`); lower = gentler on the asset server |
| `Remotion`         | `TimeoutMs`         | delayRender timeout (default `120000`) — headroom for asset-server stalls |
| `Remotion`         | `MaxAttempts`       | retry the render this many times on a transient failure (default `2`) |

### Captions (word timing)
`Captions:Source = "Whisper"` transcribes each beat's TTS audio with **faster-whisper**
(one model load per job) to get real per-word timestamps, then highlights each caption
word exactly when it's spoken. The displayed text is always the styled caption; only the
*timing* comes from whisper. Falls back to even length-weighted spread if whisper fails.

### Visual style
Cards **tilt** and **stack** into a pile (newest on top, older dimmed behind), each with a
spring **pop-in** — see `remotion/src/components/PanelStack.tsx` and `tiltForIndex` /
`offsetForIndex` in `types.ts`. The background clip is **looped** to fill the runtime.

### Backgrounds (unique per render)
Drop full-length gameplay `.mp4`s into `assets/backgrounds/`. For each render the pipeline
picks a **random clip + random start offset** and trims only the seconds it needs (muted,
looped, center-cropped to vertical) — so no two videos share the same background, even from
one long source. Add more clips for more variety. Empty folder → animated gradient fallback.

A small **no-repeat guard** (`BackgroundHistory`) remembers recent offsets per clip and keeps
each new render's offset at least a segment-length away from the last several, so consecutive
backgrounds never reuse the same footage (best-effort spacing if the clip is too short). This
history is stored in **SQLite** (`Pipeline:StateDbPath`, default `data/state.db`) so it
**survives server restarts** — restart, generate again, and it still avoids recent sections.

Settings (`Pipeline`): `BackgroundRandomize` (true), `BackgroundMinGapSec` (0 = use segment
length), `BackgroundReencodePreset` (`veryfast`).

### Health check
`GET /api/health` → `{ tts, captions, panels, elevenLabsConfigured }`. The active
providers are also logged at startup.

### Switching to ElevenLabs later
Set `Tts:Provider = "ElevenLabs"` and supply `ElevenLabsApiKey` + `ElevenLabsVoiceId`.
It requests `pcm_44100` and wraps it in WAV so timing/captions are unchanged.

## How Codex is prompted
Both prompt templates live in `src/MemeShorts.Api/Services/Prompts.cs`:
- **Storyboard prompt** → strict JSON storyboard (the beat schema).
- **Panel prompt** → one original panel PNG per beat. Storyboards mix MS-Paint doodles,
  photorealistic generated scenes, and bold graphics, while retaining a readable card-first
  layout and selective accent colors.
- Meme jobs retry an empty Codex storyboard once, then fail before TTS. Codex-selected panel
  generation also fails before TTS when imagegen returns no PNG; placeholder faces are not used.

Tune the style there; everything visual/animation is deterministic in `remotion/`.

## YouTube auto-upload & scheduling
Finished shorts can upload to YouTube as **private**, each with a scheduled **publishAt** so
YouTube auto-publishes it later. Publishing is spread across configurable daily slots
(default **3/day**: 09:00 / 13:00 / 17:00), and each new slot is the first one *after* the
previously reserved slot — so the schedule chains forward (3 shorts go public per day),
persisted in SQLite so it survives restarts.

**Setup (one-time):** see `src/MemeShorts.Api/secrets/README.txt` — create a Google Cloud
project, enable *YouTube Data API v3*, make a *Desktop app* OAuth client, save the JSON as
`secrets/youtube_client_secret.json`, then set `Upload:Enabled = true`. The first upload
opens a browser for consent; the refresh token is cached and reused.

`Upload` config: `AutoUpload` (upload when a short finishes), `PublishTimesLocal` (slots),
`TimeZoneId`, `TitleSuffix`, `DescriptionTemplate`, `Tags`, `CategoryId`. Off by default.

> The upload path needs your own Google credentials, so it can't be exercised without them;
> the slot-scheduling logic is independently tested.

## Facebook + Instagram auto-upload
Approving a short also posts it as a **Facebook Page Reel** and an **Instagram Reel**, sharing
the same reserved slot as the YouTube upload so it goes live everywhere at once. Each
destination is independent — one failing doesn't block the others, and **Retry re-runs only the
ones that failed**, so a live post is never duplicated.

**Where to put the credentials:** either the **Settings tab in the UI** (Page ID + access token,
applied immediately with no restart, stored in the local state DB) or `.env`. The UI value wins
when both are set; "Reset to .env" clears it. The API only ever returns a masked token tail.

**Setup (one-time):** see **[docs/META_SETUP.md](docs/META_SETUP.md)**. Both platforms use one
Meta app and one long-lived **Page access token**; Instagram additionally needs a Business/
Creator account linked to that Page. No App Review is required for your own accounts. Put the
credentials in `.env` (copy `.env.example`), then check them with `POST /api/meta/verify`,
which resolves each token to its account without posting anything.

**Scheduling differs by platform**, and this is the one operational catch:

| Platform | Who holds the post until the slot |
|----------|-----------------------------------|
| YouTube  | YouTube (`publishAt`)             |
| Facebook | Facebook (`scheduled_publish_time`) |
| Instagram| **the app** — IG's API has no scheduling at all |

So an Instagram reel needs the app running at its slot. Miss it and it publishes at next
startup if it's still within `Instagram:SkipIfMissedByMinutes` (default 3h), otherwise it's
marked failed for a manual retry. `SocialPublishWorker` is the one-minute timer that does this.

Both are off by default (`Facebook:Enabled` / `Instagram:Enabled`).

## Performance-steered topics (learn from your own view counts)
Topic generation is fed the channel's **real view counts**, so new topics copy what actually
broke out instead of what merely looks clever. `ChannelPerformanceService` pulls the channel
with yt-dlp (public data — **no extra OAuth scope**), joins each video back to the topic that
generated it via the stored YouTube video id, and hands Codex a ranked winners/flops block.

Guardrails that keep the signal honest: videos younger than `MinAgeDays` are excluded (a short
posted this morning isn't a flop yet), the baseline is the **median** not the mean, and nothing
steers generation until `MinVideosForSignal` judgeable videos exist. Inspect it any time with
`GET /api/performance` (`?refresh=true` to re-fetch).

> **What the data said on this channel:** every video above 2,000 views was about a *familiar
> thing misbehaving* (wifi upstairs, magnets, mazes, self-checkout). Invented/abstract premises
> (square circle, underground clouds) never broke 1,600. The prompt and `TopicQualityGate` were
> tuned the other way and were **rejecting the channel's own best format** — `"self checkout
> experience"` and `"plugging in a usb experience"` were hard-rejected by name despite being the
> #3 and #11 videos. Don't re-add specific everyday premises to `HardRejects`.

Note YouTube **rounds** public view counts (1,300 / 1,400 / 6,700), so only large multiples are
meaningful; the code treats ratios, not small differences, as signal.

### If topics suddenly look generic
When Codex fails, both topic endpoints **fall back to a static built-in seed list**
(`AutoGenerateSeeds`), which looks like real output but is far less varied. The responses now
report `fromCodex` vs `fromFallbackSeeds`, and the log warns loudly. If you see fallback usage,
check the Codex CLI first — quota exhaustion, an outdated CLI vs. a newer default model, or a
stale login all produce it.

## Auto title & thumbnail (yt-dlp study)
To save manual labor, the generator studies a reference channel and produces a ready title
+ thumbnail per short:
- **Title:** `yt-dlp` fetches the channel's recent titles (`Titles:ReferenceChannel`, cached
  `CacheHours`); Codex writes a `youtube_title` in that style *in the same storyboard pass*
  (no extra call). Used as the YouTube title on upload.
- **Thumbnail:** meme shorts use a real settled vertical frame from the rendered video,
  preserving the reference-style Shorts grid look: Minecraft background, tilted white card,
  readable card caption, and any panel accent colors. Finance shorts use a 1280×720 composite
  with a large `thumbnail_text` hook. Toggle: `Pipeline:GenerateThumbnail`.

Music **attribution**: drop `Song.mp3` + `Song.txt` (the CC credit) in `assets/music/`; the
matching credit auto-appends to the description of any short that uses that track.

## API
| Method | Route                          | Purpose                          |
|--------|--------------------------------|----------------------------------|
| POST   | `/api/generate`                | `{ topic, format? }` → `{ id }`  |
| GET    | `/api/jobs/{id}`               | status / progress (+ upload info)|
| GET    | `/api/jobs/{id}/video`         | the finished mp4                 |
| GET    | `/api/jobs/{id}/thumbnail`     | the generated 1280×720 thumbnail |
| GET    | `/api/jobs/{id}/storyboard`    | the storyboard Codex produced    |
| POST   | `/api/jobs/{id}/upload`        | retry the destinations that failed |
| GET    | `/api/schedule`                | upcoming scheduled publish times |
| GET    | `/api/health`                  | active providers + upload status |
| POST   | `/api/meta/verify`             | check the FB/IG tokens (posts nothing) |
| GET    | `/api/performance`             | channel winners/flops steering topics |
| GET/POST | `/api/settings/meta`         | FB/IG page id + token (token returned masked) |
| POST   | `/api/settings/meta/reset`     | drop app-saved creds, fall back to `.env` |

## Storage note
The app writes its output to **D:** (`out/`, plus Codex images under `$CODEX_HOME`). Heavy
render temp (Remotion/Chrome/ffmpeg) is redirected to `render-tmp/` (also on the content-root
drive) at startup so it doesn't fill `C:`.
