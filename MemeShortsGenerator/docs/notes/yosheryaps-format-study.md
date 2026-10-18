---
name: yosheryaps-format-study
description: "Measured breakdown of @YosherYaps's Shorts format (panel behaviour, asset types, pacing) — the reference Matthew wants copied"
metadata: 
  node_type: memory
  type: reference
  originSessionId: 7b326c63-1838-4d6d-94fc-1069e6444980
  modified: 2026-08-09T10:59:18.349Z
---

Study of @YosherYaps's **top 10 shorts** (4.8M / 4.6M / 4.4M / 1.9M / 1.5M … views), done
2026-07-20 by downloading them with yt-dlp, tiling ffmpeg frames into montage sheets, and
transcribing with faster-whisper. Matthew asked to copy this format — ours was "vastly different".

## ⚠ NATIVE CHARTS ARE DISABLED (2026-08-08, at his request)
Repeated attempts never matched the reference look and he called it: *"geez its so bad, disable
the charts and graphs for now."* Killed in three places: the format is marked DISABLED in
`Prompts.Storyboard`, `PipelineService.NativeChartsEnabled = false` (so `IsChartGag` always
returns false and chart_gag falls back to the ordinary image path), and `PanelBoard.isChart` is
hard-coded false. The "INCLUDE CHART-SHAPED TOPICS" block was removed from the topic brainstorm.
`MemeChart.tsx` and the chart schema are INTACT for a future retry — flip those three switches.

**What was still wrong when it was switched off** (measured against his "Lines of Life"):
his chart card is PORTRAIT (~630x830, chart fills it, thin ~3px curve, modest title); ours was a
wide landscape card with a big bold title, which flattened every curve. Last change set the card
to 620 wide with a 620-tall portrait plot and a 4px stroke, but **that was never rendered** — the
verification run died when C: hit 0 GB free. Also unmatched: he sometimes annotates the curve
with a label + arrow ("Taking naps"), and uses two-tone titles.

**DO NOT re-enable without a rendered side-by-side comparison.** Three rounds of "fixed it" here
were based on reasoning rather than a rendered frame, and each was wrong.

## LAYOUTS — ALL FOUR BUILT AND VERIFIED (2026-08-03)
`format` (was reaching Remotion via `props.format` and being ignored) now drives layout in BOTH
`PipelineService` (how many images to generate) and `PanelBoard.tsx` (how to draw them). The two
lists MUST stay in sync or beats point at panel files that don't exist.
- `single_visual_reveal` / `escalating_corrections` / `absurd_debate` -> image PER BEAT, artwork
  changes, single caption. Verified: keyboard-graveyard, 5 images, art changed each beat.
- `chart_gag` -> NO generated images; renders `beat.visual.chart` natively via the finance
  `FinanceVisual`, so the frame is genuinely pixel-fixed. Validation enforces IDENTICAL axis
  labels across beats (that's the joke). Verified: school-energy chart, same 7-point axis, curve
  redrawn per beat.
- `character_dialogue` (NEW format added to the enum) -> one image of BOTH characters, alternating
  speech bubbles accumulating beneath (grey left / red right, speaker derived from beat index —
  his sketches alternate strictly A/B so no schema change was needed). Captions are exempt from
  the >=3-word rule since real speech has short exclamations. `stripSpeaker()` removes a
  script-style "Marc:" prefix. Verified: bubble-wrap origin story rendered correctly.
- everything else -> one hero image, captions stack beneath.

**PROMPT SIZE IS A HARD CONSTRAINT — the prompt is passed to the Codex CLI as a COMMAND-LINE
ARGUMENT.** Exceeding ~32k chars kills the process with `Win32Exception 206` ("filename or
extension too long") and surfaces confusingly as "Codex returned no storyboard". Caused by my own
additions; fixed by capping style titles 400->40 and the avoid-list 60->30. **Budget for this
before adding anything to `Prompts.Storyboard`.** A permanent fix would be passing the prompt via
a file/stdin instead of argv.

## (historical) LAYOUT WAS OVER-FITTED TO ONE FORMAT — raised by Matthew, now resolved

He spotted that "not all his videos are just text like that" and he is RIGHT. The accumulating
stacked-text panel I built is only ONE of his layouts (the giraffe/karma pattern, maybe a third
of his output) and I applied it to EVERY video.

**Layout taxonomy, from the frame-by-frame study of 12 videos:**
| Video | Layout |
|---|---|
| Japanese English Shirts (4.8M) | photo SEQUENCE — different shirt photos replace each other |
| What Power (4.6M) | option reveals — 17 change events, new diagram per power |
| Chip Bags (4.4M) | one product, then a SECOND product appears alongside |
| Roomba (1.9M) | one screenshot, then a PHOTO punchline at the end |
| Things That Move (1.4M) | flowchart drawing itself branch by branch |
| Camels (1.2M) | two characters + speech bubbles |
| Lines of Life (1.1M) | same chart frame, curve + title change each beat |
| Giraffe Centaur (1.5M) | one artifact + comment — **the only one I actually built** |

**REGRESSION I INTRODUCED:** `PipelineService` now generates ONE hero image per video
(`panel_0.png`) and every RenderBeat points at it. That deliberately kills per-beat artwork, which
**breaks every format needing the picture to change** (shirts, powers, the Pringles can arriving).
Those cannot work at all in the current build.

**PLAN — branch layout on the storyboard's existing `format` field** (7 values, currently IGNORED
by the renderer; it reaches Remotion as `props.format` already):
- `relatable_list`, `internal_monologue` -> stacked text under one artifact (exists today)
- `single_visual_reveal`, `escalating_corrections` -> IMAGE SEQUENCE, new artwork per beat
- `chart_gag` -> fixed frame, changing data/label
- `absurd_debate` -> two labelled options side by side
- `relatable_chaos` -> stacked text (closest to existing)
Work needed: (1) restore per-beat image generation for the sequence formats only, keeping the
single-hero path for the text-stack ones — see the "ONE hero image" block in
`PipelineService.RunAsync`; (2) `PanelBoard.tsx` switch on `format`; (3) panel prompt must know
whether it is drawing the ONE artwork or one of N sequence frames.

**PANEL ACCUMULATION — BUILT AND VERIFIED 2026-08-03.** `PanelBoard.tsx` is now a vertical STACK
that grows, not an image swapper: row 0 = beat 1's caption as the permanent title, then the ONE
hero artwork, then each later beat's caption appears as a persistent text row at its narration
time (newest in #e11d48, settled ones black), then the fake comment as a final row with avatar.
Fixed width, `top: CARD.top + CARD.height/2` with `translateY(-50%)` so it grows symmetrically.
`CaptionTrack` and the floating `FakeComment` are NOT rendered for meme any more (they would
double-draw); `FakeComment` is now finance-only.
**PipelineService generates ONE panel image** (`panel_0.png`) instead of one per beat — every
RenderBeat points at it. Per-beat regeneration WAS the reason output looked like a slideshow:
Codex redrew the subject in a different style/perspective each time so nothing ever looked like
one evolving post. Also saves 4-5 image_gen calls per video.
Prompt updated to match: only beat 1's `drawing` is rendered and must carry the whole premise;
captions must read standalone since they stack and stay on screen.
Verified end-to-end ("why shopping cart wheels pick a direction"): title fixed, labelled caster
diagram pixel-identical throughout, four lines accumulating one at a time, comment row last.
This now genuinely matches the reference mechanic.

**FRAME-BY-FRAME STUDY (2026-07-31) — supersedes the 6-stills-per-video guesswork below.**
He asked "did you study his videos frame by frame?" The honest answer was NO — the original study
sampled 6 evenly-spaced stills per video (~0.8% of frames) and I INFERRED mechanics from them.
Redone properly: every frame of **12 videos** extracted and measured (panel bbox per frame, ink
inside the panel, in-panel and out-of-panel frame diffs). Script: `scratchpad/fbf.py` pattern.

MEASURED, HIGH CONFIDENCE:
- **Content changes are HARD CUTS, one frame (~17ms at 60fps).** Overwhelmingly the most common
  event length across all 12. Verified visually on consecutive frames: giraffe-centaur f213 has
  no comment, f214 has it fully drawn, nothing between. **My PanelBoard had an invented 5-frame
  cross-fade — now FADE_FRAMES = 0.**
- **The panel is horizontally FIXED and grows VERTICALLY.** Where the detector locked on cleanly:
  giraffe x-stdev 0.0 / width-stdev 0.0 but y 21.5 / h 43.2; Many Lines x 0.5 / w 0.8, y 6.8 /
  h 13.0; Cold Water x 0.4 / w 0.4, h 6.4. The panel is a document that grows downward as
  elements are added and re-centres. **Ours is a fixed 770x920 box — still wrong, not yet fixed.**
- **Elements are genuinely ADDED, not redrawn**: title + both giraffes stay pixel-identical while
  a comment appears beneath. Confirms ours (regenerating the whole artifact per beat) is wrong.
- **Background has 0-6 hard cuts per video** — it is not one continuous clip. Ours uses one.
- Change events per video: 1-17, commonly 4-7.

DETECTOR LIMITS — do not over-read these: the white-panel finder returned 0% for "Under The Ice
Antarctica" and 1% for "Things That Move" although both plainly have panels (they are dark
map/flowchart images, not white). So panel-visibility percentages and most "position MOVES"
verdicts are detector noise, NOT evidence the panel moves. Only the videos with near-zero x/width
stdev are trustworthy for geometry.

**PANEL BEHAVIOUR (the big structural difference).** He uses **ONE white panel, fixed in place,
that PROGRESSIVELY ACCUMULATES elements**. It does not tilt, does not stack, does not get replaced
per beat. Each beat ADDS something to the same artifact — a label, a bracket, a second image, a
speech bubble, another flowchart branch. Ours renders a NEW doodle per beat and tilts/stacks them
into a pile, which is the single most visible mismatch. (Note: `Prompts.Storyboard` ALREADY asks
for "the NEXT REVEAL of that same artifact" — so the storyboard intent is right and the mismatch
is in the Remotion composition + per-beat image generation, not the script prompt.)

**ASSET TYPES — real/found imagery, NOT MS-Paint doodles.** Observed across the 10:
real product photography (Lay's bag, Pringles can, WD-40, duct tape), clean stock clipart/line-art
(camels, giraffes, anatomical diagrams, hands), real screenshots (a Roomba app coverage map,
t-shirt photos, storefront photos), a real photo punchline (dog), clean matplotlib-style charts
with labelled axes, and self-drawing flowcharts. Plain black sans-serif on white, thin border.
Panel is centred, ~55-75% of screen width.

**"COMMENT" OVERLAYS ARE CORE.** Nearly every video ends with a YouTube/Reddit-style comment
pinned under the panel — circular avatar, bold username, comment text, sometimes "11mo Reply 1028".
Crucially **the comment is READ ALOUD as the punchline** (e.g. "This changes everything!",
"I'ma choose invisible arms and become a boxing champion!"). Our storyboard already has
`fake_comment`, so this concept exists — it just needs to be visually styled like a real comment
and voiced as the final line.

**MEASURED PACING (faster-whisper, 5 videos):**
- **~190 WPM** (range 143-222). Conversational is ~140, podcast ~160. His speed is a big part of
  why it reads as "his" style.
- **~37 words TOTAL per video**, mean length **12.1s** (range 8.0-19.0s).
- **First word at 0.0s**, last word within ~0.5s of the end — narration is wall-to-wall, no intro
  silence, no lingering outro card.
- Narration **reads the on-screen text verbatim** — the voice recites the artifact rather than
  commenting on it from outside.
- Background is Minecraft parkour (Matthew already matches this).

**Applied 2026-07-20:** `Tts:ElevenLabsSpeed` (new option, clamped 0.7-1.2, set 1.1),
`Pipeline:BeatGapMs` 120→30, `Pipeline:OutroMs` 2500→700, storyboard targets retuned to
~35-40 words / ~12s with a "narration reads the on-screen text" rule.

**Panel rework DONE (phase 1).** New `remotion/src/components/PanelBoard.tsx` = one fixed,
untilted white panel (radius 6, restrained shadow) whose beat images CROSS-FADE in place over
each other; earlier beats stay mounted underneath so the incoming image never dissolves to bare
white. Deleted the now-dead `PanelStack.tsx` + `Panel.tsx`. `CaptionTrack` gained `flat` (zeroes
tilt/offset) and MemeShort passes it. `FakeComment` re-anchored flush UNDER the panel
(`CARD.top + CARD.height + 14`, panel width) instead of floating at `bottom: 360`. Verified by
rendering real stills from job `out/00e0c114` — panel flat/fixed/centred, comment correctly
anchored and YouTube-styled. Finance path untouched (`FinanceStage`).

**Asset pipeline DONE (phase 2), 2026-07-27.** Root cause of the doodle look was NOT a missing
feature — `visual_style` (photo|graphic|doodle) already existed. `Prompts.PanelImage` built a base
spec that HARDCODED "draw the subject as a doodle" + "Crude MS-Paint / rage comic art", then
injected the style block as an "override" *into the middle of that list* — so photo/graphic panels
got self-contradicting instructions and the doodle language won every time. Fixed by making the
style block the SOLE authority and stripping all doodle language from the base spec. Also:
`photo` → studio-lit product photography on white; `graphic` → clean flat vector / labelled
diagram / flowchart ("textbook figure or stock clipart, NOT hand-drawn"); storyboard now DEFAULTS
to photo/graphic with `doodle` capped at ~1 beat; `rage_face` capped at 1 per video and only on
doodle beats (**his top 10 contain ZERO rage faces** — the older "rage-comic cards" description of
this reference channel was wrong). `PANEL ART` rule no longer mandates MS-Paint — `drawing` now
describes WHAT is shown, never HOW.
Verified by generating both styles through real `codex exec` + image_gen: the photo prompt produced
a genuine studio product shot of a soap dispenser mid-dribble; the graphic prompt produced a clean
two-colour decision flowchart closely matching his "DOES IT MOVE?" chart. Test images were deleted
from `$CODEX_HOME\generated_images` afterwards.
Also fixed: **title now persists** — `CaptionTrack` holds the final beat's caption after the last
beat ends (previously the top strip went empty while the comment was still being read).

**FULL END-TO-END RUN, MEASURED (job `4003535d`, "why escalators suddenly become stairs",
2026-07-27).** He pushed back on earlier claims; this is the real verification.

WORKS: storyboard picked `graphic` for all 6 beats, 0 rage faces, 28 words. Assets are genuinely
reference-grade — labelled escalator diagrams with a green MOVING arrow, a STOP sign, a
"suspicious stairs" callout, a technical FROZEN ESCALATOR / 80 LB FOOT / DANGER cutaway, a crown
"HANDRAIL: STILL ROYAL". Panel fixed/flat/centred, title with karaoke highlight, no gaps >0.35s
(BeatGapMs 30 works), lead-in silence 0.00s.

**DOES NOT WORK — the artifact is REDRAWN each beat, it does not accumulate.** Every beat is a
different escalator illustration (different perspective, style, composition). The cross-fade
therefore reads as a slideshow, NOT as his one-artifact-gaining-elements format. This is the
predicted failure of simulating accumulation via per-beat regeneration. **A real fix needs
composited layers** — one base image plus separately-generated overlay elements positioned by the
renderer — not a full redraw per beat.

**AUDIO MEASURED (this is where I was most wrong):**
- **121 WPM in the real video vs the reference's 190.** An isolated ElevenLabs synth of one
  flowing sentence gave 191 WPM, which misled me — short per-beat fragments with their own
  padding drag the real in-video rate down to 121. Trust the rendered video, never an isolated
  synth.
- **Trailing tail 1.50s** vs reference <0.5s, despite OutroMs 700 — per-beat audio padding adds
  the rest.
- **Pitch 127 Hz median vs reference 240 Hz**, spread 94 Hz vs 171 Hz. Nearly an octave low and
  much flatter. **This is the dominant reason it doesn't sound like him** — no setting fixes it,
  it needs a different `ElevenLabsVoiceId`. His key lacks `voices_read` (401), so voices can't be
  enumerated programmatically — he must pick one in the ElevenLabs web UI. I can then verify any
  candidate by synthesizing and measuring F0 against the 240 Hz / 171 Hz targets.
- `ElevenLabsSpeed` is a **no-op on eleven_v3** (proved: unset 8.8s vs 1.1 → 9.0s). Set to null.
  Raising WPM needs either a model swap (turbo/multilingual honour speed but lose v3 emotion tags)
  or ffmpeg `atempo` post-processing — an open decision, not yet made.

**SCRIPT / STORYTELLING STUDY (12 transcripts, 2026-07-27).** He asked for this explicitly after
noticing I'd studied pacing and visuals but never the writing. Whisper transcripts of 12 videos.

His archetypes: (1) shared-knowledge setup then reversal — states something the viewer already
knows, then flips it; (2) two named characters in banter, where a physical action becomes the
punchline; (3) first-person live reaction with emotional escalation; (4) rapid label-list over one
artifact; (5) demonstration/explainer with a "wait, actually" turn; (6) choice/poll where the
narrator picks at the end. Common to all: **complete conversational sentences with connectives**
(so/but/because/well/and then), **a narrator with a stake** (first person) or characters speaking,
a real **arc** (setup → complication → reveal), and a closing line **spoken aloud**.
Measured: 27-74 spoken words, mean ~40.

**ROOT CAUSE of our telegraphic scripts:** `Prompts.Storyboard` said *"one short caption (max ~8
words), and one TTS line that EQUALS the caption."* That single line forced narration to be
label-fragments. The schema always had separate `caption` and `tts` fields — the prompt just
collapsed them. Our output read "escalator moving public trust restored / … / climb." — five
disconnected quips, no narrator, no arc.
**Architecture already supported the fix:** `PipelineService` displays `beat.Caption` while timing
comes from whisper on the spoken audio, and `CaptionBuilder.AlignToSpeech` falls back to
proportional mapping when token counts differ — so caption and tts may safely diverge.
Fixed: decoupled them (caption = short printed label, tts = full spoken sentence), added a
NARRATION VOICE block, per-beat cap 14→22 words, total 8-55→20-60, and an explicit
**"write tts in sentence case, never ALL CAPS"** rule (first run came back entirely in caps,
which the voice engine renders as constant shouting).
Verified on "why plastic wrap hates bowls": captions became short labels (NORMAL LEFTOVER BOWL /
FIRST CONTACT / THE SIDE QUEST), spoken lines became first-person sentences with connectives and
a genuine arc (wants to cover a bowl → wrap resists → pinching a corner → wrap sticks to itself →
verdict). Word count came in at 78, hence the 60 ceiling.

**I BROKE HIS GENERATION AND FIXED IT (2026-07-27).** Tightening the validator to 20-60 words
contradicted the prompt's own new instruction to write full spoken sentences — his job
"why microwaves leave cold spots" died with *"expected 20-60 total spoken words, received 85."*
Three fixes: ceiling 60→**75** (his measured max is 74); an explicit per-beat word budget in the
prompt ("~8-12 spoken words per beat, 4 beats x ~10 = ~40; 75 is a HARD LIMIT"); and — the
structural one — **storyboard validation now RETRIES (3 attempts) instead of throwing on the
first rejection**. It previously validated once and threw, so a single overshoot killed the job.
Re-ran his exact failing topic: passed first try, 55 words, 0 all-caps beats, full render to mp4.

**VERIFIED DELIVERY (job `d146f337`, the fixed run):** 200 WPM (reference ~190 — SpeechRate 1.05
+ trim is right, after 1.35 overshot to 242), lead-in 0.00s, trailing tail 0.95s (ref <0.5s),
one 0.52s gap. Script reads as real speech with an arc and connectives.
**Pitch still 112 Hz vs reference 240 Hz, spread 75 vs 171** — unchanged and unfixable by
settings. Note pitch varies by run (163 Hz on a more animated script), so judge it across runs.

**SCRIPT VOCABULARY RUT — he spotted it, I measured it (2026-07-28).** He asked "you always
mention martial law huh?" and was right. Across **141 stored storyboards**: legal/court vocabulary
in **27.7%** (courtroom, verdict, evidence, "files for", "legally"), government words in 11.3%,
and "martial law" recurred verbatim across unrelated topics. Of the 8 most recent, SIX escalated
through legal/bureaucratic language. Other measured tics: **"once" in 41% of scripts**, "mother"
in 14% (the long-banned "mother of-" still leaking).
Root cause: the prompt says *"Treat a MUNDANE thing as deadly serious… Tiny stakes are
life-or-death"* + *"Hyperbole"* with NO constraint on HOW to escalate — so Codex reached for
courtroom/government drama every time.
Fixes: (a) prompt now bans that escalation domain by name and requires escalating INSIDE the
topic's own world (parts, materials, mechanism, physics); (b) a MEASURED TICS block listing the
percentages; (c) an anti-AI-voice block (ban uniform line lengths, shared openers, triadic lists,
"not just X but Y", tidy concluding sentences; require contractions and deliberately lopsided
line lengths); (d) **code-level enforcement**, because prompt bans demonstrably leak —
`FindClicheEscalation` (narrow unambiguous list only) and `FindRoboticWriting` (tic words,
>=3 beats sharing an opening word, line-length stdev < 1.5) both reject and drive a retry;
(e) `Codex:StoryboardAttempts` (default 5, clamped 1-10) replaces the hardcoded 3.
**VERIFIED 2026-07-28** after he re-ran `codex login` (the auth token had been invalidated).
Fresh scripts came back CLEAN of legal/govt vocabulary and tic words, passing on the FIRST
attempt (zero rejections logged), e.g. velcro: *"I'm scraping it with a pencil tip like that'll
fix physics… Listen to this flap make one sad pfft and abandon the whole shoe. The final boss was
pocket fuzz."* — contractions, first person, escalation drawn from the object itself, and a
6-word closer against 10-13 word lines.

**TUNING LESSON — the first version of the robotic-writing check was far too strict.** Using
standard deviation < 1.5 on line lengths rejected 4 of 5 real attempts, including 12/11/14/15/12
which is natural variation; jobs would have failed constantly. Replaced with SPREAD
(max - min <= 2), which only catches near-identical lines. **A validator that rejects almost
everything is worse than the tic it targets** — check the rejection rate in the log before
trusting a new heuristic.

Two residual weaknesses seen in the verified run: word counts trend high (52 and 62 vs the ~40
target, still under the 75 limit), and one script came back 13/13/13/13/10 — uniform enough to
feel templated but above the spread threshold. Tighten only if it recurs, and re-check the
rejection rate if so.

**Testing note:** run these tests against a FRESH EMPTY state db, not a copy of his. A copy
restores ~150 jobs which re-queue and, with `MaxConcurrentJobs=1`, starve the test jobs — they
sat at "queued" for 10 minutes. Also clean up `out/<jobid>` folders afterwards.

**I CORRUPTED TWO FILES AND FIXED THEM:** prepending a `using` via PowerShell
`[IO.File]::WriteAllText` after `Get-Content -Raw` mangled every em-dash into `â€”` (16
sequences across PipelineService.cs and ElevenLabsTtsService.cs). Repaired by round-tripping
cp1252 -> utf-8. **Do not rewrite whole .cs files through PowerShell** — use the Edit tool.

**SEPARATE FINDING, NOT YET RAISED WITH HIM:** `PipelineService` line ~150 hardcodes
`var delivery = ch.IsFinance ? beat.Emotion : "shouts";` — every meme beat is delivered as
"shouts" and the storyboard's per-beat `emotion` is discarded. That was a deliberate earlier
"constant energy" choice, but it flattens expressiveness (our pitch spread 75-140 Hz vs the
reference's 171 Hz) and works against "sound like a human". Worth offering to revert to per-beat
emotion.

**THE REAL "SOUNDS LIKE AI" CAUSE — INCOHERENCE, not vocabulary (2026-07-30).** He pushed back:
*"the scripts don't make any sense at all… yosheryaps sounds like human."* He was right and his
diagnosis beat mine. Our scripts were five surreal images in a row with no proposition — endings
were literally **"Soup." / "Peel." / "Cricket." / "Pancaked."**, and lines like "my arm is in
2029", "the whole tray loses trust", "my body forgets knees" mean nothing read literally.
**Every one of his videos has a SPINE**: chip bags CLAIMS Pringles is better then REFUTES it with
a real fact; Roomba poses a mystery and ANSWERS it (a dog); giraffe centaur states a RULE and
applies it to a conclusion. Absurdity sits ON TOP of coherent logic — you end up knowing
something. **My own prompt was manufacturing the nonsense**: it instructed "the climax/punchline
is usually ONE word … an absurd one-word answer."
Fixed: a "THE SCRIPT MUST MAKE SENSE" block at the very top requiring beat 1 = question/claim,
middle = evidence that follows, last = the answer; banned surreal filler that "states nothing";
added a self-test ("say the premise in one plain sentence and what the last beat reveals");
replaced the one-word-climax rule with "the last beat ANSWERS the question beat 1 raised"; and a
code check rejecting any final beat of <= 2 words.
Verified on the SAME topic: before *"…the missing sock is folded inside the fitted sheet.
Pancaked."* / after *"Why does the dryer always eat exactly one sock? Because the wet sheet rolls
it into a tiny cotton burrito. Then heat sticks it to the drum wall for two whole loads. The
sheet did it; the dryer just provided hot spinning darkness."* — question, mechanism, answer, and
actually true.

**REMOVED the line-length validator entirely.** Tried twice (stdev < 1.5, then spread <= 2) and
BOTH rejected the majority of real attempts — 9/11/11/11/10, 12/11/11/12/11, 13/15/14/13 all got
bounced, burning multiple Codex calls per job and pushing one job past 5 attempts. Codex cannot
vary sentence length on demand reliably enough for a hard gate. **Lesson: fuzzy stylistic
preferences belong in the prompt, not in a validator.** The remaining code checks are all precise
(cliche escalation, tic words, repeated openers, <=2-word ending). Do not re-add it.

**REMAINING GAPS:** pitch/voice (needs a different `ElevenLabsVoiceId` — only he can pick one;
offer to measure any candidate's F0 against 240 Hz / 171 Hz); panel density; true layered
accumulation; trailing tail still ~0.95s.

**VOICE — HE IS NOT SCREAMING; MEASURED (2026-07-31).** Matthew assumed the reference voice is
"screaming" and ours is lower. Half right. Peak amplitude: **his 0.54-0.63, OURS 0.89-0.91**;
RMS his ~0.11 vs ours ~0.13 — **ours is the louder one**, because `PipelineService` hardcodes
`delivery = "shouts"` on every meme beat. The difference is PITCH, not volume: his F0 240 Hz,
ours 112-163 Hz. Adding more shouting moves us further away.
Constant "shouts" also FLATTENS variation: unforced our spread is 145 Hz, with forced shouts it
drops to 75-140 (his is 171). Removing the hardcode is a real, evidence-backed improvement —
offered, not yet done.
**Pitch-shifting is a dead end, tested:** ffmpeg asetrate+atempo at x1.25/x1.45/x1.85 gave
131/147/**186** Hz — still short of 240 even at nearly an octave, where it sounds badly
artificial. Do not pursue.
**Only fix = a different `ElevenLabsVoiceId` (naturally ~240 Hz).** His key cannot enumerate
voices: `/v1/voices`, `/v2/voices` AND `/v1/shared-voices` all return **401 missing voices_read**.
So either he adds `voices_read` to the key (then the whole account can be auto-measured against
the 240 Hz / 171 Hz target) or he pastes voice IDs from the website for measuring one by one.

**Voice cloning was declined** — cloning a real person's voice breaches ElevenLabs' consent terms
(account-termination risk, which would take out his whole TTS pipeline) and voice-likeness law in
several states. Offered instead: match delivery (speed/cadence/energy) on a licensed or
Voice-Design voice, which is what the measurements above are for. He accepted the study work;
revisit the voice-selection step with him.

Reference clips + montage sheets were downloaded to the session scratchpad (not the project) and
are not retained — re-download with yt-dlp if needed. Related: [[meme-shorts-generator-project]].
