---
name: meme-shorts-generator-project
description: "Matthew's app that auto-generates rage-comic/brainrot meme YouTube Shorts from a topic"
metadata: 
  node_type: memory
  type: project
  originSessionId: 3a7ce298-656e-4a2b-91f1-77d9296cf5c5
  modified: 2026-09-26T12:28:47.733Z
---

App at `D:\Users\M4tth3w\Downloads\Meme Generator Project\MemeShortsGenerator\` that
turns a typed topic into a vertical "rage-comic / brainrot" Short — modeled on 5
reference mp4s in the parent folder (e.g. "How would he wear a hat?", "Microwaving
food at night"). Reference format = white MS-Paint rage-comic cards popping in over
Minecraft-parkour gameplay, TTS reading each caption, ending on a fake viewer comment.

Stack: ASP.NET Core (`net8.0-windows`) API + web UI on :5180 → pipeline:
Codex CLI storyboard JSON → Codex CLI panel PNGs (local System.Drawing fallback) →
Windows TTS (System.Speech) → @remotion/captions word captions → Remotion renders mp4.

Decisions Matthew confirmed: **Codex CLI generates the panel images** (he's done this in
other projects, says it works); **Storyboard JSON only** as Codex's structured output.
See [[codex-cli-noninteractive-gotchas]].

**TTS now = ElevenLabs (working end-to-end as of 2026-06-25).** Config is via a `.env` file
at `src/MemeShorts.Api/.env` (ASP.NET doesn't read .env natively — custom `DotEnv.cs` loader
wired in Program.cs, AddInMemoryCollection so it overrides appsettings; friendly keys
ELEVENLABS_API_KEY / ELEVENLABS_VOICE_ID / ELEVENLABS_MODEL_ID / ELEVENLABS_OUTPUT_FORMAT /
TTS_PROVIDER). Provider auto-selects ElevenLabs only when key+voice present (else Windows), so
a stray TTS_PROVIDER can't break renders. **His ElevenLabs account is Creator tier (NOT Pro)**:
raw `pcm_*` 403s ("only available on Pro tier and above"), but `mp3_44100_192` is allowed (the
max MP3) — so he's set on highest quality: **`mp3_44100_192` + model `eleven_multilingual_v2`**
(highest fidelity; turbo_v2_5 is the faster/cheaper alt). The service transcodes MP3→16-bit mono
44.1k WAV via ffmpeg (rest of pipeline unchanged). His scoped key 401s on /v1/user and /v1/voices
(TTS scope only) — expected, not a bad key. **Distortion guard** (he asked for "highest quality,
re-render if distorted"): each beat WAV is scanned for hard clipping (flat run ≥8 samples or
>0.2% clipped, `WavUtils.AnalyzePeaks`); a distorted take is re-requested with a fresh `seed` up
to ELEVENLABS_MAX_RERENDERS (2) times, keeping the cleanest. Verified renders are clean (beats
peak −1.5..−6 dB, 0 clipped) so it rarely fires — won't burn credits.
**Emotion (he asked "can it show emotion, he's just reading it"):** switched model to **`eleven_v3`**
(accessible on his key) which PERFORMS inline audio tags. Each storyboard Beat now has an
`emotion` field (deadpan/whispering/panicked/screaming/…); `ElevenLabsTtsService` prepends it as
`[tag] ` to the text ONLY for the v3 model family (other models read brackets literally). Tags are
performed, NOT spoken (verified via whisper: `[deadpan] who hasn't done this` → audio says just
"who hasn't done this"); captions/timing stay clean since the tag isn't in the displayed `caption`.
Expressive voice_settings via .env (ELEVENLABS_STABILITY=0.4/STYLE=0.5/SIMILARITY_BOOST=0.85/
SPEAKER_BOOST=true). Confirmed working: whisper beat peaked −7.6 dB vs scream beat −0.6 dB — the
volume swing proves emotion is rendering. Do NOT per-beat normalize (would flatten that dynamic).
The meme prompt instructs Codex to set emotion per beat and swing it for comedy.

Backgrounds: drop FULL-length gameplay mp4s in `assets/backgrounds/` (currently the full
`Minecraft.mp4`, 18min, copied from his Downloads). Per render the pipeline picks a RANDOM
clip + RANDOM start offset and ffmpeg-trims only the needed ~seconds (muted, looped,
center-cropped to vertical) so every video's background is unique — he wanted this. He
prefers curating **daytime-only** source clips over auto night-detection (we built a
brightness re-roll, then removed it at his request) — so the bg folder should only contain
day-safe clips (the original Minecraft.mp4 has night sections; replace it). A
no-repeat guard (`BackgroundHistory`) keeps offsets spaced apart and persists them in
**SQLite** (`data/state.db`) so the spacing survives server restarts (he asked for DB-backed
state). Panel prompt was fixed so each beat DRAWS the literal scene/object (like the
reference vids) instead of defaulting to a big rage face — faces now only at the climax. His CS2
"gameplay" in `D:\SteamLibrary\...\csgo\replays\` is `.dem` demos (NOT video, can't
auto-convert). Env quirk: **no ffprobe**, only ffmpeg at `D:\Tools\ffmpeg\` (pipeline
probes/trims via `ffmpeg -i`). BackgroundDir = `../../assets/backgrounds` (relative to the
API content root, src/MemeShorts.Api).

Captions use **faster-whisper** (`small.en`, his cached model) for real per-word timing —
displayed text stays the styled caption, only timing comes from whisper (`Captions:Source`,
even-spread fallback). Cards **tilt + stack** into a pile with spring pop-in; background
clip is looped via ffprobe-measured length. `GET /api/health` shows active providers.

**Multi-channel**: `ChannelRegistry` + `Channels:Profiles` in appsettings = named profiles (currently
"meme" + "finance") selectable per generation (UI dropdown, `channel` field on /api/generate,
`/api/channels`). A profile overrides contentType, title-ref, bg/music, voice, panel source, and
upload (per-channel YouTube creds = deferred, meme uses globals). Resolved via `EffectiveChannel`.
Finance channel: `ContentType=finance` → finance Codex prompt → storyboard beats carry a `visual`
(stat|chart|comparison|tip) → Remotion renders them natively in the card (`components/finance/*`),
NO image_gen (faster/cheaper). Verified: all 4 finance visuals render; meme path unchanged. The
Remotion `Panel` branches `beat.visual ? FinanceVisual : Img`. One known content-quality nit:
Codex's finance numbers can be internally inconsistent — prompt-tunable, not a render bug.

Reference channel for title/thumbnail style = **@YosherYaps** (which is the channel his 5
original reference shorts came from). `yt-dlp` (at his Python313 Scripts) fetches its recent
titles; Codex writes a matching `youtube_title` + short `thumbnail_text` in the same storyboard
pass. Thumbnails are a 1280x720 composite (panel + bold outlined hook over a gradient) via
System.Drawing, set on upload. Music tracks carry a sidecar `<name>.txt` with the CC
attribution, auto-appended to the description. OAuth is DONE: his Desktop client secret is at
`secrets/youtube_client_secret.json`, he's a verified test user, refresh token cached — uploads
work hands-off (but he wants nothing on the channel yet; `Upload:Enabled=true`, AutoUpload on).

YouTube auto-upload (Google.Apis.YouTube.v3): finished shorts upload as PRIVATE with a
scheduled `publishAt`; slots are 3/day (09:00/13:00/17:00 configurable), each chained to be
the first slot AFTER the previous reservation (he wanted next-depends-on-previous), persisted
in the same SQLite db (`publish_schedule` table). Needs HIS Google OAuth (Desktop client
JSON at `src/MemeShorts.Api/secrets/`, `Upload:Enabled=true`); can't be tested without it,
but the slot-chaining logic is verified. Storage: app output is on D: (`out/`, `$CODEX_HOME`);
heavy render temp was filling C: — now redirected to `render-tmp/` on D: via TEMP/TMP env at
startup (his C: only had ~1GB free; freed ~1.2GB of stale Remotion temp).

The meme comedy prompt (`Prompts.Storyboard`) was enriched from a 14-video @YosherYaps study
into 6 formats (relatable_chaos, relatable_list, absurd_debate, chart_gag,
escalating_corrections, internal_monologue) + a device list (self-aware narrator, hyperbole,
mundane-as-life-or-death, one-word/dumb-reveal climaxes); output is noticeably funnier.

**Dedicated React frontend (added 2026-07-04).** There's now a proper SPA at
`MemeShortsGenerator/frontend/` (Vite 6 + React 19 + TS + Tailwind v4 + shadcn-style UI,
TanStack Query + axios; no Radix — Tabs are local-state). Three tabs: **Generate** (manual
topic OR hands-off "Auto-generate N" with a count 1–20), **Review** (approve/reject queue with
inline 9:16 video preview + History), **Calendar** (month grid of scheduled/uploaded shorts +
upcoming list). `vite.config.ts` dev-proxies `/api`→:5180 and `build.outDir`→ the API's
`wwwroot` (so `npm run build` makes the .NET app serve the SPA on :5180; the old vanilla
wwwroot UI is thereby replaced). Run for dev: backend `dotnet run` in `src/MemeShorts.Api`,
frontend `npm run dev` in `frontend/` (:5173). NOTE the "frontend is Remotion" trap — Remotion
is only the video compositor, NOT a UI.

New backend pieces backing those features: **review gate** — every finished render now sets
`Review=Pending` (ReviewState enum on GenerationJob) and waits; `POST /api/jobs/{id}/approve`
(→ upload if configured, else just marks approved) and `/reject`. AutoUpload still works as a
standing pre-approval. **Batch auto-gen** — `POST /api/generate/auto {count,channel,format}`
calls new `ICodexClient.BrainstormTopicsAsync` (Codex `{"topics":[…]}`, `Prompts.TopicBrainstorm`,
style-title seeded) then queues a job per topic; tops up from yt-dlp titles / `AutoGenerateSeeds`
if Codex under-delivers so it never returns empty. **Calendar** — `GET /api/calendar` returns
jobs with a known `PublishAtUtc`. `JobStatusDto` gained topic/channelId/autoGenerated/createdAt/
review. `/api/health` gained `autoUpload`. Verified 2026-07-04: both build clean, all 3 tabs
render with no console errors, endpoints return correct codes; a full generation run was NOT
triggered (avoids burning Codex/ElevenLabs credits) — the user clicks Generate for that.

**OAuth token expiry (recurring gotcha, learned 2026-07-04):** his Google app is in "Testing"
publishing status, so YouTube refresh tokens **die after ~7 days** → an approved upload fails with
`invalid_grant "Token has been expired or revoked."` and the video sits at `upload=failed`. Symptom
he'll report: "I approved but it didn't upload." Fix = re-consent: `POST /api/youtube/authorize`
now **force-clears** the cached service + stored token (FileDataStore.ClearAsync) so the browser
prompts fresh (previously it silently reused the dead token and kept failing). The Review tab shows
a **"YouTube sign-in expired → Reconnect YouTube"** banner (triggered when any job's uploadError
matches invalid_grant/expired/revoked) + a **Retry upload** button on failed History items. Note the
uploader caches `_service` as a singleton, so a stale token needs either the force-clear path or a
backend restart — plain restart alone won't help because the broker reuses the on-disk token unless
it's deleted. To stop the 7-day cycle entirely he'd publish the OAuth app (or accept periodic
reconnects). Also: the enriched Review card shows the exact YouTube post (thumbnail + title +
description + tags) above Approve/Reject; each finished render sets `Review=Pending` regardless of
upload config (approve just no-ops the upload when unconfigured).

**Three tweaks he asked for (2026-07-04, all verified):** (1) **Constant energy voice** — new
`Tts:ForceEmotion` (appsettings, default `"shouting"`) overrides EVERY beat's storyboard emotion
with one high-energy ElevenLabs-v3 tag, instead of the calm→scream swing. Dial excited/shouting/
screaming; empty = per-beat. Applied in ElevenLabsTtsService (effectiveEmotion). (2) **Thumbnail =
a real card frame** — for meme he wanted the thumb to be just one of the actual cards over gameplay
(like @YosherYaps' shelf), NOT the old composite with an added hook-text overlay. PipelineService
now ffmpeg-grabs a frame from the finished mp4 at a strong settled beat (rage-face→drawing, avoids
the final fake-comment beat, ~65% into the beat) → 1080x1920 portrait thumb.jpg; composite kept as
fallback. Finance thumb unchanged. ThumbnailService still exists (fallback only). (3) **No duplicate
topics** — new `TopicHistory` SQLite service (topics table in state.db, UNIQUE normalized key:
lowercase/trim/strip-punct/collapse-ws). JobManager reserves the topic on Create, releases on
failure. `/api/generate` 409s on a repeat; `/api/generate/auto` passes RecentTopics(60) to Codex as
"already made — avoid", then filters dupes (durable + within-batch) and re-asks (count*2) then
seeds, 409 only if nothing new. Frontend surfaces the 409 message on both Generate buttons.

**Upload disclosure + render quality (2026-07-04):** (a) YouTube "altered content"/AI-use
disclosure — he wanted it answered **No**. `VideoStatus.ContainsSyntheticMedia = false` set on every
upload (verified the field exists in his Google.Apis.YouTube.v3 1.75.0 DLL). Honest for crude
doodles (that disclosure is only for realistic synthetic media). (b) **60 fps + high quality**:
`Pipeline:Fps` 30→**60**; resolution now configurable via `Pipeline:Width`/`Height` (default
1080x1920 — kept, it's already 1080p/Shorts-native; can supersample to 1440x2560). Remotion render
now uses **PNG frames** (`Remotion:ImageFormat=png` — no jpeg ringing on the black line-art) + **CRF
16** (`Remotion:Crf`) via new `--image-format`/`--crf` flags in RemotionRenderer. Verified output:
1080x1920, 60fps, ~13.5 Mbps. Trade-off: renders ~2x slower (he chose to keep 60fps). To speed up:
ImageFormat=jpeg or Fps=30.

**Job persistence (2026-07-04 — he lost a 20-video batch on refresh).** Root cause: JobManager
was in-memory ONLY, and the backend was tied to the agent session (died on session teardown), so
jobs vanished. Fix (1): new `JobStore` service (SQLite `jobs` table in state.db) — every job is
`Persist()`ed at Create + each state change (done/pending, failed, approve/reject, upload). On
startup `JobManager.Restore()` reloads all jobs; unfinished (Queued/Running) ones are re-queued
to run again so a batch completes. GenerationJob.CreatedAt changed `get;`→`get;init;` so it can be
restored; JobStore maps via a flat PersistedJob DTO (GenerationJob's [JsonIgnore] hides VideoPath/
OutputDir/ThumbnailPath, so a DTO is required). Verified: killed backend mid-render → job restored
& resumed. Fix (2): run the backend **detached** so it survives session cycles —
`Start-Process powershell -ArgumentList "-NoExit","-Command","$env:ASPNETCORE_ENVIRONMENT='Development'; Set-Location '<api dir>'; dotnet run --no-build" -WindowStyle Minimized`
(a tool `run_in_background` dotnet dies when the agent session ends; Start-Process is independent).
For a truly permanent server he'd use a Windows Service / NSSM, but detached window + persistence is
enough day-to-day.

**Disk-full incident + footprint (2026-07-04).** A 20-video batch failed en masse — every error
("No space left on device", "not enough space on the disk", GDI+ generic, Remotion render fail) was
a **D: full** cascade (D: hit 0 free; it holds the project, render-tmp, out/, and $CODEX_HOME).
The 60fps + **PNG frames** change I'd made writes huge transient temp under render-tmp — it
accelerated the fill. He freed D: himself and wants to KEEP high quality, so PNG + CRF 16 stay
(`Remotion:ImageFormat=png`, `Crf=16`); a `Remotion:JpegQuality` knob exists for if he ever switches
to jpeg. Added **auto-cleanup**: `Pipeline:CleanupIntermediates=true` → after a successful render,
`PipelineService.CleanupJobScratch` deletes the per-job scratch (bg.mp4, panels, beat wavs, props,
music) keeping only short.mp4 + thumb.jpg, so out/ stays lean. Also cleared 1.42 GB of orphaned
`$CODEX_HOME\generated_images` (old panel PNGs, already baked into videos — safe; needs explicit OK
since it's outside project scope). **ffmpeg cannot replace Remotion** (he asked): Remotion renders
the animated React composition (card springs, word captions, gameplay compositing, finance charts)
to frames; ffmpeg is only the encoder/filter step (bg trim, audio transcode, thumb frame-grab, and
Remotion's internal encode). Failed jobs release their topic (TopicHistory), so he can regenerate them.

**Review UX overhaul + auto-approve (2026-07-04).** He complained the Review page scrolled
sideways (raw Remotion/ffmpeg stderr forced width), wanted pagination, review-as-cards, and an
auto-approve toggle. Done: (1) `lib/errors.ts:friendlyError` maps raw errors → one short line
("Ran out of disk space", "Video render failed", etc.); raw kept in a title= hover — kills the
horizontal overflow. (2) `ui/pagination.tsx` (Pagination + paginate helper): History 8/page, pending
6/page. (3) Review pending is now a responsive card GRID (video capped max-h-[340px], title, topic,
collapsible <details> YouTube desc+tags, approve/reject) — replaced the master-detail. (4)
**Auto-approve toggle**: `ui/switch.tsx` + a persisted runtime flag. Backend: `JobStore` gained a
`settings` table (GetSetting/SetSetting); `JobManager._autoApprove` seeded from `Upload:AutoUpload`,
`SetAutoApprove(enabled)` persists + approves all currently-pending when turned on; review gate uses
`_autoApprove` (approve→upload if configured, else just approve). Endpoints `GET /api/settings`,
`POST /api/settings/auto-approve {enabled}`. Verified via DOM: 0 horizontal overflow, switch present,
"Page 1 of 2". (Preview screenshots time out under render load — the machine is pegged by 60fps PNG
renders; DOM checks are the reliable verification then.)

**Topic quality fix (2026-07-04 — he called out weak auto-topics).** yt-dlp DOES fetch @YosherYaps
titles fine (verified: "What a Giraffe Centaur Looks Like", "Parallel Lines Are The Saddest Lines",
"Wifi Signal Strength be like", "If Columbus had Twitter", "The Truth About Working", "Most Evil Life
Hack Ever"…). The bug was `Prompts.TopicBrainstorm`: it framed the channel as "mundane relatable
everyday situations", so Codex only produced generic annoyance topics (folding sheets, untangling
headphones). Rewrote it to spell out his REAL range (absurd visual/geometry/wordplay gags, fake
charts/meters/tier-lists, "what X looks like"/"X be like", absurd hypotheticals "if [figure] had
[modern thing]", parody how-tos, hot takes/anti-motivational) and to explicitly CUT generic
annoyances (max ~2 relatable-with-a-twist). Also replaced `AutoGenerateSeeds.Meme` with varied/clever
seeds. New sample was on-brand ("what a square circle looks like", "why circles are secretly lazy",
"wifi password strength as animals", "if dinosaurs invented office jobs"). Added
`GET /api/topics/suggest?count&channel` — brainstorms topics WITHOUT creating jobs (no renders);
could power a "preview & pick topics" UI on the Generate tab (offered, not yet built).

**Prompt-example leakage (2026-07-04, fixed in two passes).** `Prompts.Storyboard` was full of
verbatim example dialogue that Codex pasted into real scripts instead of imitating: first "one dorito"
(the SPECIFICITY example → "one dorito is worth prison" in ~6 videos), then "who hasn't done this"
(narrator example) and "MOTHER OF-"/"YES" (climax + relatable_chaos example, appeared TWICE). Fix:
rewrote the narrator line, climax line, and ALL SIX format examples to describe STRUCTURE ONLY (no
quotable dialogue), and added a banned-verbatim fence naming the offenders. **Lesson: any quoted
phrase/object/line in an LLM prompt gets echoed into output — describe the style abstractly, never
give copyable sample dialogue.** Verified clean via a cheap trick: generate a job, then GET
`/api/jobs/{id}/storyboard` (populated right after step-1 Codex call, BEFORE the full render) and
grep the captions — no need to wait for the multi-minute render. Sample after fix ("stubborn shopping
cart"): "so we begin diplomacy / left wheel refuses the treaty / … / betrayal" — on-topic, original,
one-word climax invented not recycled.

**Facebook + Instagram auto-upload (2026-07-20, he asked for it).** Approving now fans out to
YouTube + FB Page Reel + IG Reel, all sharing the job's ONE reserved slot; destinations are
independent (one failing doesn't block others) and **Retry only re-runs the non-Uploaded ones**,
so a live post is never duplicated. New: `ISocialUploader` (+`MetaGraphClient` raw-HTTP Graph
wrapper, no SDK), `FacebookReelsUploader`, `InstagramReelsUploader`, `SocialPublishWorker`,
`PlatformPublish` state per platform on GenerationJob, `UploadState.Scheduled`, `POST /api/meta/verify`.
Setup guide he should follow: **`MemeShortsGenerator/docs/META_SETUP.md`**.

Non-obvious constraints that drove the design (worth not re-deriving):
- **Instagram's API cannot schedule** — `media_publish` goes live on call. So IG sits at
  `Scheduled` and `SocialPublishWorker` (1-min timer) publishes at the slot → **the app must be
  running then**. Missed slots publish late only within `Instagram:SkipIfMissedByMinutes` (180).
  YouTube (`publishAt`) and Facebook (`scheduled_publish_time`) schedule server-side and never
  touch the worker.
- **IG needs the resumable upload path** (`upload_type=resumable` → rupload.facebook.com), because
  the classic flow demands a PUBLIC `video_url` and he renders to a local disk.
- **One long-lived PAGE token serves both** FB and IG. Must exchange the short-lived user token for
  a long-lived one FIRST (Step 3) — a Page token derived from a long-lived user token never expires.
- **No App Review needed** for his own Page/IG while the Meta app is in Development mode.
- IG requires a **Business/Creator** account **linked to the Page**; `INSTAGRAM_USER_ID` is the IG
  Business id, NOT the Page id (a very easy mix-up — `/api/meta/verify` catches it).
- **FB Reels cap at 90 seconds**; IG allows 15 min. A long short posts fine to YT/IG but not FB.
- Fixed in passing: **`.env` was NOT gitignored** despite holding his ElevenLabs key — added
  `.env`/`.env.*` to .gitignore plus a committed `.env.example`.

Verified 2026-07-20: both build clean; dummy tokens produce a real unwrapped Graph error
("OAuthException 190"), proving the HTTP + error path; worker correctly starts only when a
platform needs app-side scheduling; UI verified via DOM against a seeded scratch DB (live links,
scheduled-with-slot-time, and failed-with-error all render). No real credentials existed, so
an actual post was NOT made — he does that after following META_SETUP.md.

**Performance-steered topics + what his view data ACTUALLY says (2026-07-20).** He asked to pull
his most-viewed videos and generate future topics like them. His channel = **GameGoblin**
(`@TinyChaos-g6w`, `youtube.com/channel/UCAGyJMv_d2RPumkgKakJ_RA`), 64 shorts, **median ~1,350
views**. Built `ChannelPerformanceService` (yt-dlp view counts — public, no OAuth scope needed;
joins video→topic via the stored `YouTubeVideoId`; median-based; excludes videos < MinAgeDays;
persists to a `video_performance` table) → feeds a winners/flops block into `Prompts.TopicBrainstorm`.
`GET /api/performance` exposes it. He chose **"lean hard into winners."**

**THE KEY FINDING (do not re-litigate — it's measured, from same-day cohorts so age is controlled):**
Almost every video sits at a ~1,000-1,400 floor; the question isn't the median, it's *what escapes
the floor*. **Every single video above 2,000 views is a FAMILIAR THING MISBEHAVING** — wifi upstairs
(6,700), magnets (6,300), self-checkout (5,900), mazes (5,300), traffic cone (3,800), board games
(3,200), Tetris (2,600). **Invented/abstract premises never broke 1,600** — square circle (829),
underground clouds (774), cup holder evolution (21), waving at wrong person (6). Title *shape*
barely matters (all formats within ±19% = noise). This **reverses** the 2026-07-04 tuning that cut
"generic annoyances" in favour of absurdist gags — that tuning produced the flat 07-04 batch.
Worse, three layers were **blocking his own best format**: the prompt banned `"self checkout
experience"` + `"plugging in a usb experience"` as BAD examples, and `TopicQualityGate.HardRejects`
hard-rejected those exact strings — they are his **#3 and #11** videos. Also removed: the
`" experience"` scoring penalty, and the **hard requirement** that a topic contain a word from the
hand-maintained `FamiliarSubjects` list (it silently killed any everyday subject not on the list —
velcro, zipper, escalator — i.e. exactly the winning category). Score threshold dropped 4→2 to
recompensate, +2 for a ≥5-word descriptive premise (his #5 "the emotional support traffic cone"
has no hook word at all). Verified with a standalone test project compiling the real
`TopicQualityGate.cs`: 14/14 cases correct (it caught 3 genuine regressions first).
Caveat: YouTube ROUNDS public view counts (all those identical 1,300/1,400s), so only large
multiples are signal; exact figures would need a `youtube.readonly` scope + re-consent.

**Codex quota exhaustion is silent + looks like "bad topics" (2026-07-20).** Could NOT run the
end-to-end topic test: `codex exec` fails for every model with *"You've hit your usage limit …
try again at Jul 29th, 2026"* (`codex login status` = logged in; also saw a separate stale-config
error, `models cache: unknown variant 'max'`, and his Codex default model `gpt-5.6-sol` requiring
a newer CLI than the installed v0.137.0). **When Codex fails, `/api/generate/auto` and
`/api/topics/suggest` silently fall back to the STATIC `AutoGenerateSeeds` list** — output looks
like real brainstorming but is a fixed list, a likely contributor to his "random ass topics"
complaint. (Several uploads match seeds verbatim; his #1 video "why wifi disappears upstairs" is
itself a seed entry.) Added `fromCodex` / `fromFallbackSeeds` to both responses + a loud warning
log. **When he reports weak/repetitive topics, CHECK CODEX QUOTA FIRST.** See
[[codex-cli-noninteractive-gotchas]].

**Prompt rewrite VERIFIED end-to-end 2026-07-27** (quota had reset; note the machine clock read
Jul 20 mid-session, so don't trust a remembered "blocked until" date — just re-probe `codex exec`).
`GET /api/topics/suggest?count=10&channel=meme` → `fromCodex=10, fromFallbackSeeds=0,
performanceSteered=True`, and every topic was a familiar-thing-misbehaving premise: "why escalators
suddenly become stairs", "the broken soap dispenser experience", "how hotel key cards give up",
"which public toilet lock lies", "why plastic wrap hates bowls", "real life vs fitted sheet logic".
Three of those prove specific fixes: the *"experience"* one (old scoring penalty would have killed
it), the *escalator* one ("escalator" isn't in `FamiliarSubjects`, so the old hard-requirement would
have rejected it), and *fitted sheet* (was literally the prompt's own "bad topic" example). Format
spread was healthy — no runaway repetition of one template.

**FAILURE AUDIT (2026-07-31) — 67 of 206 jobs failed (33%).** He asked why so many fail. Breakdown:
**53 (79%) = "Codex returned no storyboard"**, nearly all timestamped 2026-07-28T06:20:14 — the
batch that ran while his Codex auth token was invalidated. Historical, fixed by `codex login`.
7 = disk full (all 2026-07-04). 2 = Remotion render. **Only 1 came from any validator I added**,
and ZERO from the new cliche/tic/ending checks. So the validators are NOT the failure source —
check the auth/quota story first when he reports mass failures.

**REMOTION COMPOSITOR OOM — partly fixed, NOT fully (2026-07-31).** Live failure was
`Compositor exited with code 3221226505: memory allocation of 11059254 bytes failed`.
Three genuine defects found and fixed:
1. **Background was never scaled.** `TryStageBackgroundAsync` trimmed but had NO `-vf`, so the
   staged `bg.mp4` stayed at the source **2560x1440** and Remotion decoded an 11 MB frame
   (2560*1440*3) for every frame. Added
   `scale=W:H:force_original_aspect_ratio=increase,crop=W:H,setsar=1` — now 1080x1920, frame
   allocation dropped to 6.2 MB, file 30 MB -> 18 MB. (`setsar=1` matters: without it the scale
   left a fractional SAR 10240:10239.) Remotion was already object-fit cropping this in the
   browser, so the visual result is identical.
2. **Remotion version mismatch**: everything on 4.0.481 except `@remotion/google-fonts` pinned
   `^4.0.482`. Remotion warns this causes "failed renders and unclear errors" — and those exact
   lines were in the failure stderr all along, easy to mistake for noise. Fixed by pinning all
   Remotion packages to exact 4.0.482 (`npm i --save-exact`). `npx remotion versions` now exits 0.
3. **The render retry re-ran the IDENTICAL command**, so an OOM just reproduced itself. Now each
   attempt halves concurrency (3->1) and switches to jpeg frames after the first failure, with
   `LooksLikeOutOfMemory()` detection and an actionable message. `Remotion:Concurrency` 4->3,
   `MaxAttempts` 2->3.
Also reclaimed **4.7 GB** of stale `render-tmp` (entries dating to 2026-07-04).

**delayRender STALL — SOLVED 2026-08-08 (a SECOND, different render failure).** Symptom:
*"waiting for the page to render the React component at frame N failed: timeout 123000ms"* at a
mid-render frame (observed 662 and 716), across different formats, failing all 3 attempts. NOT
memory — no allocation error.
**Root cause: sparse keyframes in the staged `bg.mp4`.** The trim re-encoded with
`-preset veryfast` and no GOP control, so x264 placed keyframes ~1.94s apart (max 2.90s) = up to
**174 frames to decode per seek at 60fps**. Remotion's OffthreadVideo seeks to arbitrary
timestamps, and the 512MB frame cache (~82 frames at 1080x1920) could not span a GOP, so it
re-decoded constantly and blew the 123s delayRender budget. This is why stalls only appeared
AFTER the OOM cache-cap was added: unbounded cache never re-decoded, it just grew until it died.
**Fix: `-g 30 -keyint_min 30 -sc_threshold 0` on the background trim** -> keyframe every 0.50s
exactly, max 30 frames decoded per seek, for +0.9 MB per clip. Also raised
`Remotion:OffthreadVideoCacheMb` 512 -> 1024 for headroom.
Verified by regenerating the two exact topics that stalled ("worst gas pump handle behavior",
"secret behind jammed staplers"): both completed on **attempt 1/3 at full concurrency 3**.
**Diagnostic recipe worth reusing:** count keyframes with
`ffmpeg -i clip.mp4 -vf "select=eq(pict_type\,I),showinfo" -f null -` (do NOT pass
`-loglevel error`, it suppresses showinfo output).

**COMPOSITOR OOM — SOLVED 2026-08-03.** Root cause was Remotion's **OffthreadVideo frame cache
growing without bound**, which is why short renders passed and long ones died (frame 71, then
364, while 201 frames succeeded). Fix = pass **`--offthreadvideo-cache-size-in-bytes`**
(new `Remotion:OffthreadVideoCacheMb`, default **512**). The full 1095-frame render then finished
exit 0 at **60fps, full quality, no downgrade** — so do NOT drop Fps to 30 or lower resolution;
those were the wrong levers. Verified end-to-end through the app on "evolution of the cereal box",
the exact topic that had failed 3x: Done, 14.9 MB.
NOTE the flag list is discoverable via `grep -roh "offthreadvideo[a-z-]*" node_modules/@remotion/
renderer/dist` — `remotion render --help` did not show it.

**Superseded notes on that failure:** A 201-frame render at concurrency 1 now SUCCEEDS (5.2 MB out),
but the full 1101-frame job still OOMs at concurrency 3 and again at 1. It previously died at
frame 71, then 364 — i.e. it **accumulates memory over frames**, so short renders pass and long
ones don't. Only ~5 GB of 15.9 GB RAM was free (Brave holding ~3.3 GB).
**Two untried levers, in order:** (a) close Brave/other apps to roughly double free RAM;
(b) drop `Pipeline:Fps` 60->30, which halves frame count 1101->550 and lands near the length that
already succeeds — memory says 60fps was a deliberate quality choice, so ask before changing it.
Root cause of the accumulation is NOT established; do not claim it is fixed.

**How to apply:** when he revisits this, the whole pipeline + Remotion render (whisper
captions, tilt/stack, bg looping, ElevenLabs voice) is validated end-to-end; the one
externally-dependent piece is whether his Codex setup actually emits PNGs for the panel
prompt (local fallback covers it otherwise).

## Picture sourcing for list shorts — CHANGED 2026-09-26, Matthew chose Codex-first

Each script line now carries a `kind` (`PictureKind`): **object** -> Codex draws it isolated on
white (cuts out onto a white sticker, ~40% of CobbleCat's cards); **scene** -> Codex draws a
full photo; **real** (a specific named machine/building/bridge) -> exact Commons first, drawn
only if none, because a drawing of a specific real thing is a plausible wrong guess. Why:
Commons matches file NAMES - "potassium sample" gave permanganate, "kitchens" a Kitchener road
sign, "granite block" wooden fish drums; 1 in 14 pictures cut out. Auto-generate and the draft
page both draw 3 at a time (~1 min per batch). Old kindless lines keep the order below.

### (superseded) 2026-08-20 order
Order was: **exact Commons term -> Codex draws it -> broad Commons**. Implemented in
`FallbackImageService`, which wraps `WikimediaImageService` + `ICodexClient`.

Why that order, measured: Commons also accepts progressively SHORTER versions of a phrase, and
those nearly always match something loosely related — "a single blue sock drying on a radiator"
degraded to "a single blue" and returned a water lily. So the common failure is a WRONG photo,
not an empty result, and a fallback that only fires on "nothing found" would almost never fire.
Trying the exact term first, then generating, then degrading, is what makes the fallback useful.

`Prompts.StockImage` asks for a believable PHOTOGRAPH (not an illustration) because the drawn
picture has to sit on the same card as real Commons photos. Verified: the sock/radiator test
returned a convincing photo. Cost ~70s per generated image. Generated pictures carry
`SourceTitle = "generated:<term>"` (keeps the no-duplicates rule working) and are badged
"drawn" in the review UI.

## List batches from the UI (2026-09-28) — Matthew: "so that i would not need to interact with you"
Generate page, list channel: **"Make several videos"** card (`ListBatchCard.tsx`) -> `POST
/api/generate/list/batch {count 1-30}`; `GET` polls progress; `/stop` cancels. Runs server-side in
`ListBatchRunner` (in-memory: an API restart ends the batch, finished videos stay in Review), one
video at a time via `ListAutoMaker` (the old inline `/list/auto` body, now shared). Pictures are
staged in `out/_drafts/auto_*` BEFORE the job exists, so when `CodexQuota` (parsed from the CLI's
"You've hit your usage limit ... try again at X") ticks during a video, it is dropped and the
batch stops with state "quota" instead of rendering holes + photo-search junk. While exhausted,
`FallbackImageService` no longer swaps failed object drawings for Commons photos either.
So: don't hand-roll batch scripts any more - the button does it, and prefer it for tests too.

### Exact-name photo lookups must NAME the thing (2026-09-28, `CommonsRelevance`)
Commons ranks loosely, so the top hit for a specific name often shares one word: "Windows Recall"
-> "Precision-Recall tradeoff" chart (opening picture, twice), "The True Size" -> planet chart,
"Game Boy Color" -> a Game Boy ADVANCE. Now every exact lookup (real-kind pictures, drafts, the
object fallback) keeps a file only if its title+ObjectName+description+categories contain each
capitalised run of the term as a phrase (a leading brand word may be missing if >=2 words remain:
"AI Overviews" ok for "Google AI Overviews", "Recall" not ok for "Windows Recall"); lowercase terms
need every word. Rejected before download; the caller draws instead. Description matters: the
right Dyson sink photo is FILE-named after the station it was taken in. Cannot fix "Greenland" ->
a photo taken in Greenland (a map was wanted). Prototype: scratchpad `relevance_proto.py`.

## Google Drive moves (2026-09-29) — copied from YtPipeline's RcloneArchiver
Matthew asked to copy `D:\Users\M4tth3w\Downloads\Youtube Pipeline Plan`'s "Move to Google Drive".
`DriveArchiver` + `DriveAutoMover`: rclone copy -> rclone check --one-way -> record `job.Drive`
(persisted) -> delete local files except thumb.jpg and .json/.txt < 1 MB (list.json must stay: the
script writer reads recent list.json to avoid repeats). Remote `gdrive:MemeShorts/<channel>/<id>`,
same signed-in `gdrive` remote as YtPipeline, rclone at WinGet Links path (appsettings `Drive`).
Auto (Settings-tab switch, setting `driveAutoMove`): every 5 min, rejected OR
`JobManager.IsPublishedEverywhere` (every CONFIGURED destination Uploaded; Instagram "Scheduled"
still needs the file). Failed moves wait 1 h. Approve/revoice/upload refuse while in Drive
("Bring it back first"). Bring back leaves the Drive copy. NOT the G:\My Drive folder: Drive for
desktop caches a local copy, freeing nothing. Verified round trip on 6c8e855d: 48 MB -> 145 KB ->
back, identical file list/sizes. rclone still on the shared client_id (retiring 2026) - fix doc:
YtPipeline `docs/google-drive-own-client-id.md`.

### Picture sources (2026-09-30) - real first, Codex draws only when nothing accurate exists
Order (FallbackImageService.ArchiveAsync): Wikipedia lead image (Commons-hosted only; en-wiki fair-use
files skipped) -> Commons exact term -> Pexels (PEXELS_API_KEY in .env -> Pictures:PexelsApiKey) ->
Openverse (commercial+modification licences, photographs) -> Codex drawing. Scenes: Pexels only,
then draw (Commons/Openverse scenes brought real people). Pexels/Openverse skip photos described
with people or as mockups/posters. Matching: proper names need their phrase; plain Commons/
Openverse terms need the words TOGETHER (categories/joined tags created false matches: "foldable
phone box" -> train table, "juice box" -> meat-juice ad); Pexels alt text allows words anywhere
(one sentence about one photo). Google Images ruled out: Custom Search API closed to new users,
ends 2027-01-01. Reality check: real photos cover common objects well (~half); story-specific beats
("Wii Remote accessory port", "novelty soda bottles") still need drawing - expect ~1/3 real overall.
