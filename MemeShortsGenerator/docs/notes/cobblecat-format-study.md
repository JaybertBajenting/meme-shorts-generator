---
name: cobblecat-format-study
description: "Measured layout of the CobbleCat list-short format (card, captions, mascot anchors)"
metadata: 
  node_type: memory
  type: reference
  originSessionId: d67c83dc-2bfe-406a-80da-eef6a94772eb
  modified: 2026-09-27T10:23:15.710Z
---

## READ THIS FIRST — re-measured 2026-09-26 on 20 Shorts; supersedes the sections below

Matthew sent HddZKnsiHYY ("Gelatin is a WEAPON?") and then the whole channel: *"to not limit the
videos here is the youtube channel so that you can study more"*. Studied his 16 newest + his 4
biggest hits. Full table in `MemeShortsGenerator/docs/cobblecat-format-study.md`. His look had
MOVED since 09-01 (media-filled, big Baloo captions) - he is back on a small white sticker:

- card ~700-950px (median ~800), centre y 959 (sd 25-65); ours was 950-1080 + 18% full-frame
- offset shadow right/bottom only (+38/+36px on 733px, sigma 15, 50%); corner r ~0.11W; no outline
- captions **Comic Sans MS Bold**, fitted to 1-2px, **0.155 x card width**, 1-2 words/page, white caps
- mascot straddles the CARD's left edge in front of it, 40-50% of card width, ~40% of the time
- a new picture every ~1.2s on the word naming a new thing; pace **201 wpm**, 59 words, 16.5s
- **punchline shrink**: final reaction line -> whole frame hard-cuts to ~0.79 in black, holds to
  the end. 8/20 videos and ALL 4 top hits
- blur: his bg edge widths ~1.45x ours -> BackgroundBlur 14 -> 20

Implemented: one short line per picture (10-16 beats), **one voice take** cut at word times
(`NarrationAligner`) + atempo to 200 wpm (per-line clips were costing ~40-50 wpm), new
`ListStage.tsx`, shrink in `MemeShort.tsx`. Boundary kept: HIS format, OUR mascot/scripts/voice
and Commons-or-generated pictures - never his character, jokes or footage.

**Remaining gap is picture content**: his cards are clean isolated product shots and memes;
Commons gives mixed photos and diagrams, and only ~1 in 10 cuts out onto a white sticker.

### HOW THE MASCOT GETS THERE (2026-09-27) — supersedes "pops in / springs in" below
Matthew: *"u are trying to view it frame by frame but you don't know how it got there"*. Every
earlier pass measured where the cat SITS; none watched it ARRIVE. `cat_track.py` (eye tracker,
every frame, 18 Shorts, 35 appearances) + 60fps strips around every entrance/exit:
- **arrives ON a picture cut**, already in place the same frame (20 of 21 mid-video entrances
  within 1 frame of a cut). No motion after. Rarely (~3 of 12) snaps in from off-screen left in
  3-4 frames, no overshoot. Exits on a cut (25/35), rarely a 5-frame slide out left.
- on screen from frame 0 in 10 of 18 videos; appearance ~1.3s median (1-2 pictures), ~2/video
- rides the card, holds still (14px median wander), sometimes changes POSE on a cut; sometimes
  it IS the picture (cat alone on a white card).
Ours was: a 90px spring glide-and-settle after every cut (~12 frames), runs of 2-3 pictures,
34-55% screen time. Fixed in `ListStage.tsx` (`mascotRuns` + slide constants), verified on
rendered 60fps strips. The tracker misses some poses, so its "entrances" include pose changes -
check strips by eye.

**Then Matthew: "his mascot has like an animation when it enters and leaves" - also right; my
"no motion after the cut" was wrong.** The 0.16-scale contact sheets could not show a tilt.
Tracking the EYE-LINE ANGLE per frame (`cat_track.py` now stores `ang`; `cat_swing.py`):
- ~2 of 3 entrances arrive TILTED median ~11 deg (5-26), ~2/3 clockwise, and rock upright like a
  toy on its base: PIVOT AT THE FEET (16 deg tilt = 66px eye shift), one 30-60% overshoot,
  settled in ~6-10 frames, slight size pulse, motion blur on fast frames. Some fade in ~3 frames.
- a pose change on a cut replays the swing
- slide-outs tip TOWARD the exit (-20..-30 deg, ccw), shrink to ~0.87, blur, over 5-12 frames
- pose-drawn tilts (a pose drawn leaning 35 deg) read as constant tilt - not animation.
Implemented as a damped cosine (omega 47 rad/s, decay 10.5/s) about "53% 94%" of our sprite,
ghost copies at 1/3 and 2/3 frame back as motion blur. **Poses**: Codex drew 4 more of OUR bat
(point/shock/cheer/deadpan) from the sprite via `codex exec "<prompt>" -i ref.png` (prompt
BEFORE -i: `--image` takes several files and swallowed the prompt), on #00FF00, keyed and fitted
to the original canvas (`key_poses.py`); installed as `assets/mascots/cobblecat_*.png`, staged by
`TryStageMascotPoses` into `props.mascotPoses`.

### TITLES + TOPICS (2026-09-27) — Matthew: "follow how he makes the title", "topics and speech"
His latest 200 titles vs ours: Title Case 11% vs 100%; 68% have a SHOUTED word; 70% carry an
emoji, and it is 😭 (109) or 🤣 (34) - ours got 😂/💀/🤔 from `PackageMemeTitle` (built for the
meme channel) -> new `PackageListTitle`. ~Half start with the subject's name; ours were 5/11
"...Too X", then 6/6 "this/these" once told about "these". Title shape + caps level are now DRAWN
per video in `ListStyleCard`. Opinion words only: "ILLEGAL"/"LYING" produced false titles.
TOPICS: his are modern (tech/gadgets, AI & internet, games, bad designs, words/names, everyday
body/habits, animals/science; history ~3%; ~40% are roundups "these X"). Ours were Wikipedia
history because `ListSteers` was picked for what Commons has photos of (castles, coins, ships).
Now weighted modern steers + a 40% roundup format. Truth risk in roundups: Codex may invent a
plausible product ("Ragnok ErgoStrike7") - prompt now says drop anything not surely real.
Speech fix: half the sentences must be plain statements, max ONE figurative comparison, 0-2
devices per video (40% none), object-address only 25% - five of six had "Buddy/Sir/Border,".

### His WRITING, measured the same day — missed at first, Matthew caught it
*"the way you are scripting the narration, is it the same as cobblecat? i want it to be the same"*.
I had matched structure (length, pace, picture per phrase, reaction ending) and never the writing.
`voice_stats.py` (scratchpad) on his 20 transcripts vs our scripts:

| | his 20 | our first 5 | after fix (6) |
|---|---|---|---|
| questions / sentences | 20% (15 of 20 videos ask one) | 0% | 17% |
| exclamations | 20% | 0% | 15% |
| I/me/we per 100 words | 2.6 | 0.0 | 3.3 |
| this/these per 100 | 2.5 | 0.0 | 4.5 |
| spoken fillers per 100 | 5.6 | 1.9 | 4.9 |
| opens "X, because Y" | 2 of 20 | 5 of 5 | 0 |

He is one person REACTING OUT LOUD to the thing on screen: opinions, questions to the object or
whoever made it, role-play (the object talks, a viewer objection knocked down), casual speech,
no coined metaphors. Ours was an AI-ish narrator with a metaphor per sentence.

**Two failure modes on the way, both worth remembering:**
1. Describing his devices made Codex use ALL of them every time - "look at this thing" and
   dude/buddy/bro in 5 of 5, ALL-CAPS ending 5 of 5. A prompt cannot make one call differ from
   the next: every call reads the same text.
2. Fixed with `CodexCliClient.ListStyleCard`: code DRAWS each video's opener, two devices and
   ending type in his measured proportions (endings: 6 plain exclamation / 5 shout / 4 question /
   3 deadpan / 2 cut-off per 20). Plus a banned-tics list for phrases that recurred ("pay rent",
   "I hate", "who approved"...). Words from my own prompt ("approved") came straight back as tics.

Boundary: describe his devices abstractly; never paste his lines into the prompt.

### Second writing pass (2026-09-27) — "are you confident it's the same humour?" Honest answer: no
Format/pace/titles/topic categories matched; the humour did not. On 7 new-style scripts vs his 20:
we said "you" 0.0/100 (his 3.1), I/me 4.8 (his 2.6), and every line was a crafted quip ("gamble
with sink mist", "losing a fight to a bathroom") where he shouts, screams and argues. Batch-level
tics: "NO THANK YOU!" ended 2 of 7. And ~7 of his 20 react to NEWS (new model, new mod, product
announcement) - Codex can't know those without web search.
**Rates in the prompt give the average and no spread.** Fixed by drawing each script's
(you, "!" sentences, I/me, questions) together from ONE of his videos (`HisCounts`, 18 tuples).
Also: last 10 channel scripts go into the prompt (`JobManager.RecentListScripts`); 30% of
self-picked subjects are news via `codex --search` with a `source` URL saved to `source.txt`.
**Round-1 test drafts showed what prompt text can't fix:** 4 of 8 ended on a stock refusal
("no thanks!", "oh, I'm done!") - partly primed by the recent scripts - so `GenericEnding` regex +
60%-word-overlap check now reject and retry in code. The #1 remaining "written" tell is the
HUMAN-TRAIT QUIP ("your phone owns pajamas now", "lightning is leaving"). Forcing a "you" count
made "two hundred eyes on your dinner" - say only-where-natural. News drafts: two picked the same
month's biggest story (news now gets a steer area too), and one shortened "Muse Charm" to "Meta
Charm" - names must be copied exactly. Brand-new products are forced to drawn `object` pictures.

### Voice delivery cues (2026-09-26, `AudioTags`)
His '!' sentences lift pitch a median **+70 Hz** over his own baseline (12 videos). Ours were flat.
Sentence-level eleven_v3 cues now: `[shouting]` on mostly-capitals sentences (works: +180 Hz in a
real render; the old uncued "WHY?" was read at 137 Hz), `[excited]` on '!' sentences. Measured
with same-seed full-script A/B (`cue_fulltake.py`): inline `[excited]` beat untagged in 4 of 5
pairs by ~+22 Hz - real but modest. Take-to-take variance is +-100 Hz, so single-sentence tests
lie: in isolated clips `[excited]` looked like +115 Hz. Own-line placement, `[exclaims]` and
stability 0.0 were no better. **The remaining gap is the VOICE** (his baseline ~250-340 Hz,
ours ~180 Hz) - only a different voice ID closes it. Shouted sentences are lowered to lowercase
except real acronyms, or "WHY IS IT" gets read letter by letter.

Measured off @CobbleCatYT's "Which state has the HOTTEST residents?!" (21s, 643k views) at
1080x1920, frame by frame. Drives `ListStage.tsx` in [[meme-shorts-generator-project]].

- **Card**: ~935 wide (87% of frame), centred near y 900-960 — LOWER than frame middle.
  Corner radius ~55.
- **Card motion**: it does **NOT rotate and does NOT sway**. Tracked with `minAreaRect` over
  all 21s, its angle stays inside ±0.9° (sd 0.80°). What it does is ZOOM — width creeps ~5-6%
  within a beat, anchored low so the bottom edge stays put.
- **Captions**: Baloo-style rounded face, ~140px (cap height 95-100px), heavy black stroke
  under the fill. Always **2-4 words / ~10-13 chars** a page — that shortness is what lets the
  face stay huge. Page changes every 0.1-1.6s, typically 0.4-0.6s.
- **Caption tilt**: each page carries ONE fixed angle held for its whole life, then the next
  page jumps to a different one — measured drift within a page is under 0.2° (max 1.0°) across
  37 pages. Roughly **1 page in 6 is thrown hard over (±9 to ±16°)**; the rest sit within a
  couple of degrees of level. There is no smooth swing anywhere in the video.
- **Mascot** (tracked by its green eyes through every frame — `track_cat.py`): it is **not
  attached to the caption AND not parked at a fixed anchor**. Five appearances across 21s, on
  screen only **40% of the video**, each with its own position, size and motion:

  | # | secs | x drift | y | scale |
  |---|------|---------|---|-------|
  | 0 | 1.0 | 133→76 | ~997 | +12% |
  | 1 | 1.5 | 135→76 | ~888 | +14% |
  | 2 | 2.6 | 856→872 | 755→804 | +31% |
  | 3 | 1.2 | 162→234 | 574→650 | −4% |
  | 4 | 1.7 | 143→191 | 693→778 | −4% |

  So it appears on SOME beats only, at a different height (eye level 574-1000) and size
  (eye-sep 50-91, i.e. ~1.8x range) each time, and it SLIDES 40-90px and rescales while up.
  4 of 5 appearances are on the left. The caption sits clear of it, gap 30-40px.
- The card sometimes zooms in hard on a payoff beat (frame-filling); the rest of the time it
  holds the ~87% size.

## Scripts (4 transcribed with faster-whisper)

- **Length is the big lever.** His two 3.2M-view hits are **7.4s and 8.8s — 26 and 32 words**.
  His 21s explainers did far worse (2.5M and 654k). Target 30-70 words, 4-6 lines.
- **Pace 186-219 wpm, mean 204.**
- **No preamble** — the first sentence is the hook ("British Coins are cursed!").
- **A personal aside in every single script**: "I don't know what that's about", "I don't know
  if I can say that", "God, I love English", "End video!". He is reacting, not narrating.
- **The payoff lands on the PICTURE and the narration often refuses to finish the joke**
  ("They call it co— hahaha I don't know if I can say that").

## Titles (300 videos, median 830k views; top-30 vs bottom-30)

- Median 6 words / 30 chars. 83% of top titles have ONE or TWO words in ALL CAPS.
- **Question marks are a LOSER signal**: 3% of his top 30, 10% of his bottom 30. Avoid "??".
- 67% carry one emoji (😭 or 🤣) — same in top and bottom, so it is table stakes, not an edge.
- Winning titles **tease and withhold** rather than stating the fact: "Internet Explorer's
  being kinda quiet... 😭", "The Zombie Apocalypse ain't so bad...", "Firefox's DARK FUTURE...".
  Trailing "..." appears in 17% of the top set.

## Matthew's own preferences (override the reference)

- **No tilted captions.** Measured or not, he does not want them. Captions render level.
- **Captions go in the UPPER part of the card**, never centred over the middle of the picture —
  dead centre buries the subject of the photo behind the words.

**Lesson worth keeping**: three times now I described this format from eyeballing contact
sheets —
first claiming the mascot tracked the caption's end, then adding a smooth side-to-side sway to
both card and captions, then claiming the mascot sat at a fixed anchor. All three were wrong
and Matthew caught all three. The third one is the instructive failure: I HAD measured — but
only x, on one side, across a handful of frames — and stated a confident rule the measurement
could not support. Tracking y and scale THROUGH TIME showed it slides and grows on every
appearance.

Rule: before claiming any layout or motion behaviour, track the thing across the WHOLE video
in x, y and scale, and segment it into appearances. `track_card.py`, `track_caption.py` and
`track_cat.py` in the scratchpad do exactly this and each took minutes to write.

---

## Re-measured 2026-08-31 across FIVE shorts — three numbers above were WRONG

Matthew: *"the one that you made are somehow not yet the same quality as his ... study it frame
by frame."* Measured ZWvXhc5IFKY (5.1M), 3z_K3t5pXCo (4.4M), CwR0bJmG1SA (4.2M), llWmiL71Ypc
(3.6M) and Sm5eU75SFM0 at 10fps. **468 clean card reads, detector verified against an overlay
first** — the earlier one kept locking onto the artwork INSIDE the card, which is what produced
the bogus "angle swings 45°" and the "+5-6% zoom per beat" below.

| | old claim | measured now |
|---|---|---|
| card centre | ~y 900-960, "lower than middle" | **(539.5, 959.1) = dead frame centre** |
| card size | fixed 935-1130 wide | w p25 953 / med 1026 / p75 1085; h med 855; **aspect 1.0-1.5, it takes the artwork's shape** |
| card zoom | +5-6% within a beat | **~0%.** Frame-by-frame runs: 867→862 over 1s, 958→964 over 0.6s, 970→973 over 0.4s. Median across runs +0.3%. About **1 beat in 4** does push in for real (1016→1079 over 1.4s, +4.4%/s) |
| card angle | ±0.9° | median 0.00°, p75 0.19° — confirmed, it does not rotate |
| mascot | on screen 40% | **51 / 60 / 20 / 27 / 14 % of frames, median 27%** |
| mascot side | "4 of 5 on the left" | **97 / 98 / 100 / 89 / 88 % on the left** — putting it right 40% of the time was invented |
| caption size | ~140px face | glyph boxes 144-212px → an em of **~145-210**; 140 was measurably small |

**The structural thing that mattered most, and was missed entirely before: the card is a WHITE
STICKER and the caption sits in its white space, ABOVE the artwork.** Not text laid over a
photograph. "here's a" sits above the keyboard, "typewriters" above the typewriter. Reserving a
caption band at the top of the card also pushes its aspect back toward his 1.1-1.3 — a bare
landscape photo gives 1.5-1.6.

`ListStage.tsx` now: card centred at (540,960), sized to the picture inside a 1040x1010 box with
a 30px white margin and a caption band of 1.5x the face reserved above it; caption 185px in that
band; zoom 0.5%/s with 4.4%/s on one beat in four; mascot 35% of beats, 94% on the left.
Verified with `remotion still` against his frames — see `compare3.jpg`.

**Still not matched, and it is not layout:** his artwork is CUT OUT (subject isolated, transparent
background) and often a small collage; ours are rectangular stock photographs. That is the
biggest remaining gap and it lives in image sourcing, not in the component.

Tooling in the scratchpad: `track_card2.py` (white-card tracker), `measure.py` (card + caption),
plus green-eye mascot detection. Verify every detector with an overlay before quoting it — that
is twice now that an unverified tracker produced confident wrong numbers.

## The mascot POPS IN — it does not drift (measured 2026-08-31, second pass)

Matthew, watching the first sample render: *"the mascot is not appearing the same as cobblecat,
ours is just being drag away slowly while cobble cat is popping out."* He was right, and this
corrects the "it slides 40-90px and rescales while it is up" line above.

The travel is real. It happens in a QUARTER OF A SECOND at the start, not across the appearance.
The earlier reading came from sampling at 10fps, which cannot resolve a 5-frame entrance — the
entrance got smeared across the whole appearance and read as a drift.

Tracked at **native 60fps** (`cv2.VideoCapture` reads these AV1 files directly — no frame
extraction needed), 15 clean appearances across five shorts:

- **entrance 83-333 ms**, travelling 27-137px (clean median ~80), ease-out, sometimes
  overshooting — one runs 134 -> 211 -> back to 205, i.e. a spring
- **then dead still**: x moves 6-35px over the remaining ~1s
- **scale is FLAT** — eye separation is constant from the first visible frame, so it arrives at
  full size and slides. It does not grow
- appearances run **0.9-1.7s**, not the whole beat
- exit: ZWvXhc5IFKY slides it back out left over ~4 frames, accelerating; the others hard-cut

Clearest single entrance, ZWvXhc5IFKY at 7.57s:
`x = 62, 113, 135, 139, 141, 143, 142` then ~142 for the next 57 frames.

`ListStage.tsx` now drives the entrance with `spring()` (the config `FakeComment.tsx` already
uses) on a horizontal offset only, holds position, and slides out over 120ms. Our render
measures **317ms entrance, 86px travel, 2.5px of movement over the following 770ms, constant
width** — inside his band on every one of them.

**Trap worth remembering:** gating the mascot's VISIBILITY on a window inside the beat also
changed the caption's size and the card's top band, because both key off "is the mascot here".
Two flags are needed: one for the beat (layout) and one for the window (drawing). With one flag
the card visibly resizes the moment the character leaves.

## His EDITING (measured 2026-08-31, third pass) — the part two studies missed

Matthew: *"still not the same as cobblecat, is that the best you can do?? did you really study
his videos frame by frame????? also follow his editing style as well why did you not follow
it?"* He was right. Both earlier passes measured COMPOSITION — where the card sits, caption
size, mascot motion. Neither measured how he CUTS. Matching furniture while missing rhythm is
why it still did not look like him.

Measured at native 60fps across ZWvXhc5IFKY / llWmiL71Ypc / 3z_K3t5pXCo / CwR0bJmG1SA:

- **visual changes every 0.78-1.32s.** Ours held one card per spoken line, 2.4s.
- **card width p10 594 / p50 ~950 / p90 1080.** Ours measured 1080 at p10, p50 AND p90 — a
  spread of exactly zero. One fixed slab for the whole video.
- **full frame (1080, edge to edge) on 12-26% of frames**; **no white card at all on 13%** of
  ZWvXhc5IFKY (full-bleed).
- **THE KEY FINDING: most changes are the SAME PICTURE RE-FRAMED.** Colour-histogram comparison
  across every large size change scores >0.95 on the great majority. He zooms, punches to full
  frame, comes back. That is how his edit runs fast without a new photograph every second — and
  it means the pace can be matched with no pipeline change and no extra image sourcing.
- three arrival styles, all measured: **pop-in** (172 -> 640 -> 958 over 2-3 frames), **fast
  ramp** (863 -> 920 -> 979 -> 1080), **hard cut** (968 -> 1080 in one frame).
- background cuts are rare (every 1.6-3.5s) and mostly NOT aligned with picture changes.
- **NO SOUND EFFECTS on cuts.** High-frequency energy at cut moments is indistinguishable from
  baseline (0.598 vs 0.569, 0.414 vs 0.323, 0.291 vs 0.291). Do not add whooshes.

`ListStage.tsx` now cuts each beat into ~0.9s SHOTS, each re-framing the same picture with a
size mode drawn from his distribution (sticker / normal / full-frame / full-bleed) and one of
the three arrivals. Ours now measures **change every 0.76s** (his 0.78-1.32) and **white-area
sd 0.096** (his 0.082-0.119).

**Measurement traps hit this pass, all caught by looking at frames:**
- the white-card width detector MERGES our card with the caption and reads 1080 when the card is
  900. It reported "59% full frame" for a render whose contact sheet plainly showed varied
  sizes. A merge-proof proxy — the fraction of the frame that is white — is comparable across
  both and is what the numbers above use.
- a histogram "same picture" test threw false 3.5s matches; a suspected wrong-picture flash at
  6.4s was my own contact-sheet label being wrong, and the 60fps extraction showed correct
  content.
- forcing `minHeight: 620` on a narrow card makes a "small" shot a mostly-white slab and pushed
  our variation to sd 0.164, above his range. Scale the minimum with the card's width.

**Still not matched, still not layout:** his artwork is CUT OUT — subject isolated, transparent
background, often a small collage. Ours are rectangular stock photographs. That is now the
largest remaining difference and it lives in image sourcing.

## Cutout artwork + the final style verification (2026-08-31)

`CutoutService` turns a photo shot on a plain background into a subject on transparency, so the
card carries a cutout rather than a photo rectangle. Flood-fill from the border, no ML model.

**The first version shipped a genuinely bad cutout** — a museum stone on a grey wall came back
moth-eaten with half the wall attached, which looks far worse on a white card than the plain
photograph. The three checks that separate that failure from a clean cut, measured over the
sample set:

| | stone (bad) | drawing (good) |
|---|---|---|
| tolerance drift (flood at 30 vs 45) | 0.18 | 0.00 |
| ragged edge (share of frame on the boundary) | 0.096 | 0.015 |
| border coverage | 0.95 | 0.96 |

Thresholds: drift <= 0.06, ragged <= 0.04, border coverage >= 0.85, background 12-88% of the
frame, subject one connected piece. On the six sample photos this accepts exactly one — the
right one. **A low hit rate is the correct behaviour**; the fallback (an honest rectangle) is
good, and a bad cutout is not.

Also fixed: `ListStage` fell back to `beats[0]` whenever no beat matched, and beats are laid end
to end with a 30ms gap, so the FIRST picture of the video flashed for two frames at every line
boundary. It now falls back to the most recent beat.

**Verified against him** on the shipped render (`verify_style.py` in the scratchpad, reads the
mp4s directly with cv2 — no frame extraction):

| metric | his range | ours |
|---|---|---|
| visual change | 0.78-1.32s | 0.83s |
| size variation (sd of white area) | 0.036-0.119 | 0.093 |
| size spread p90-p10 | 0.080-0.338 | 0.253 |
| card centre x / y | 539-709 / 936-1693 | 540 / 960 |
| card rotation | 0.00-0.20 deg | 0.00 |
| SFX at cuts (1.0 = none) | 1.00-1.28 | 1.05 |
| mascot screen time | 14-60% (median 27) | ~16% (counted by eye) |

**The purple-blob mascot tracker is unreliable** when the Minecraft background is pink or red —
it reported 94% screen time and a 785px entrance on a render with two brief appearances. The
mascot's motion was verified properly on a render with a neutral background (217ms entrance,
77px travel, 6px of movement while held). Count it by eye on a contact sheet otherwise.

## HE HAS MORE THAN ONE LOOK (2026-09-01) — the white-sticker model was over-generalised

Matthew: *"its still not the same as his latest videos, go and pull out his 20 latest videos, you
really can't copy his videos?"*

Pulled the latest 20. **Correction to my own suspicion first: the videos studied were not
stale** — ZWvXhc5IFKY (22 Aug) and CwR0bJmG1SA (19 Aug) are both inside his latest 20. The
mistake was not age, it was generalising his whole format from ONE video.

**His current card is MEDIA-FILLED, not a white sticker with an inset.** Rounded corners, a thin
white outline, no white margin, no caption band — the words sit on the picture. Measured with the
merge-proof white-area metric:

| | his latest 12 | ours before | ours after |
|---|---|---|---|
| white area, median | 0.113 (0.033-0.261) | 0.181 | **0.040** |

**His media MOVES.** Comparing pixels inside the card only across frames where the card itself
did not move or resize: **96% of held frames are moving footage** (median inter-frame change
11.5; a still is under 1). His cards carry real clips — TikToks with the @thelashhause watermark
still on them, reaction video, owl footage. `ListStage` now supports a clip per beat
(`RenderBeat.clip` + `<Loop>`, matching the gameplay-background pattern since this Remotion has
no `loop` prop), and every still gets a slow Ken Burns push so a card is never frozen.

**The card takes the MEDIA's aspect** — his run 0.64-0.99 (portrait to square) because his source
material is vertical; ours come out landscape because Commons photographs are. So the pipeline
now measures each picture and passes `panelAspect`; imposing a fixed aspect only crops. Also
measured: **horizontally locked, vertically loose** — centre x sd 9px, centre y sd 112px.

All seven verification metrics inside his range afterwards (`verify_style.py`).

**Detector failures this round, both caught by looking at frames:** a sharpness-based card
tracker returned aspects of 4.84 and 0.10 and a 1427px "card" on a 1080px frame — it was locking
onto caption strips. The card aspect was read off contact sheets by eye instead. That is three
studies in a row where an unverified detector produced confident nonsense.

**THE REMAINING GAP IS SOURCE MATERIAL, and it is not technical.** He shows real internet clips,
reaction faces and memes; we show Wikimedia Commons photographs. Matthew chose to keep Commons
(the legally clean call), so the LOOK now matches and the CONTENT still reads as stock
photography. Nothing in the renderer closes that.

### Third pass (2026-09-30) — full comparison, 30 of ours vs his 200 titles / 20 transcripts
Matched: pace 202 vs 198 wpm, questions/"you"/sentence length. Gaps found and fixed in code:
titles 56% "<thing> is <JUDGEMENT>" (his 17%), 0% fully shouted (his 26%) -> shape weights +
`ListScriptChecks.StyleTitle` capitalises/adds "..." in code; opener tics (", and man," 7/22)
-> first-sentence n-gram check vs last 8; unrequested game similes -> regex check; spec sheets ->
max 3 numbers; subjects returning (rejected ones were invisible) -> `JobManager.ListHistory` gives
EVERY past subject + subject-repeat check; uniform length -> word target drawn from his 18 profiles
(`HisProfiles` now 7 fields incl. fillers/pointing/words). Soft checks only block attempts 1-2.
**Voice**: his 264 Hz (224-322) vs ours 176; the user's new voice ftVEC8Bynbd6AhYUW2pS measured
the SAME pitch as the old (193 vs 193 Hz on identical scripts). asetrate+atempo +3/+5 semitones
reaches 227/248 Hz (Remotion's ffmpeg has no rubberband). Matthew listened and chose the ORIGINAL
(no pitch shift, 2026-09-30) - accept the pitch gap; don't re-propose shifting unless he asks.
Then (same day, after hearing real videos) he DISLIKED the new voice: back to the OLD voice
fMVyuPXyuSzDe8TPvizz (set in-app via POST /api/settings/voice). Note .env holds a THIRD voice
(UgBBYS2sOqTuMpoF3BR0) - 'Reset to .env' in Settings would NOT restore the old one. Pictures now real-first (Wikipedia
lead image, Commons only, never en-wiki fair-use files) - ~25-33% of distinct pictures found real.
