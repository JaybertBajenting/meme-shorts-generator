# Handover

Everything needed to pick this project up on a fresh machine or in a fresh chat.
Start here, then read `docs/notes/INDEX.md` (the working notes, copied out of the assistant's
private memory so they live in git).

## What this is
A .NET 8 minimal API (`src/MemeShorts.Api`, port 5180) + Remotion renderer (`remotion/`) + Vite/React
frontend (`frontend/`, built into `src/MemeShorts.Api/wwwroot`) that makes vertical Shorts.
The `cobblecat` channel makes list shorts in CobbleCat's format. Videos land in the **Review** tab;
nothing is published until it is approved there.

## Run it
```powershell
cd src\MemeShorts.Api
$env:ASPNETCORE_ENVIRONMENT = 'Development'
dotnet run                      # or launch.ps1 from the repo folder
```
Frontend rebuild: `cd frontend; npm install; npm run build` (= `tsc && vite build`).
Remotion deps: `cd remotion; npm install`.

## What git does NOT hold (back these up or recreate them)
| What | Where | Notes |
|---|---|---|
| API keys and tokens | `src/MemeShorts.Api/.env` | Copy `.env.example`. ElevenLabs, Facebook/Instagram, `PEXELS_API_KEY`. |
| YouTube OAuth | `src/MemeShorts.Api/secrets/` | `youtube_client_secret.json` and `youtube_token/`. |
| Job database + settings | `src/MemeShorts.Api/data/state.db` | Jobs are JSON blobs; settings are key/value. The narration voice chosen in the Settings page is stored here and **overrides `.env`**. |
| Rendered videos | `src/MemeShorts.Api/out/` | Finished ones are archived in Google Drive at `gdrive:MemeShorts/<channel>/<jobId>`. |
| Backgrounds and music | `assets/backgrounds/`, `assets/music/` | About 10 GB, git-ignored on purpose. |
| Google Drive remote | rclone config | A remote named `gdrive`. Its path and the rclone exe are in `appsettings.json` -> `Drive`. |
| Codex login | `CODEX_HOME`, set in `appsettings.json` -> `Codex:CodexHome` | Machine-specific path. |

`appsettings.json` contains paths specific to the original PC (rclone, Codex home). Change them on a new machine.

## Settings worth knowing
- Narration voice in use: `fMVyuPXyuSzDe8TPvizz` (the original voice, restored on request after a new one was disliked). It is set in the app's Settings, not in `.env`.
- Auto-approve must stay **off**. The owner approves every video.
- Codex model `gpt-5.5`, reasoning effort medium. Image drawings cost about 30k tokens each; they are the last resort after photo search.
- Picture order: Wikipedia lead image -> Wikimedia Commons -> Pexels -> Openverse -> Codex drawing.

## Rules the owner set (enforce in code, not only in prompts)
1. If CobbleCat does not do it, our videos do not do it. Check his transcripts first (`docs/notes/feedback-cobblecat-is-the-rule.md`).
2. Never re-render an APPROVED job: it re-uploads to YouTube. `tools/rerender.py` refuses approved jobs.
3. Use our own bat mascot, never CobbleCat's. Do not paste his lines or titles into prompts; describe patterns instead.
4. No photos of real, identifiable people.
5. Restart the API only when it is idle (no render or batch running).
6. Do not permanently delete files; move them and let the owner delete.

## Open items
- Four videos were rejected and auto-moved to Drive (PlayStation `d4c7ae30`, diamonds `3bf0e8fa`, animal tongues `3cfe8472`, groceries `843ef543`). Unanswered: were they rejected on purpose, or should they be brought back (Review -> History -> Bring back) and re-rendered in the original voice?
- Remotion background-clip bug: "No frame found at position 5632" happens when `bg.mp4`'s start is too close to its end.
- Single place names ("Greenland", "Neptune") can still match a loosely related photo.
- A logo saved as a PNG on a Wikipedia article is not recognised as a logo.
- Idea not built: draw a brand-new product once and reuse its picture.
- Optional: give rclone its own Google client ID (see the YtPipeline `docs/google-drive-own-client-id.md`).

## Notes folder
`docs/notes/` holds the measured format studies (CobbleCat, YosherYaps, BlobbyClips), Codex CLI
gotchas, Remotion memory/render lessons, the repost slicing safety net, and the project history.
Some notes mention scratch scripts (`check6.py`, `drafts_v3.py`, ...) that lived in a temporary
folder and were not kept; the measurements they produced are recorded in the notes.
`cobblecat-layout-measurements.md` is the older layout study; `cobblecat-format-study.md` is the newer
re-measure (2026-09-26) and wins where they disagree.
