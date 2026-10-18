---
name: repost-slicing-safety-net
description: The repost slicer's preflight validator, the 100+ screenshot regression corpus, and the LockBits crash it uncovered
metadata:
  type: project
---

The repost slicer infers a layout from each pasted screenshot rather than matching known
formats, so a screenshot in a shape it has not met yields a wrong plan and there is no case to
add in advance. Matthew diagnosed this himself (2026-08-19): "there's no fix format ... if
there's new format of images its not been saved yet and you dont know how to handle it".

The answer is not to enumerate formats, it is to check the PLAN is self-consistent:

- `PostPlanValidator` — layout-agnostic invariants (overlapping slices in a lane, inverted or
  out-of-bounds coordinates, no captions, nothing spoken, a text slice 4x the median height,
  large uncovered stretches, one-sided comparison groups, header over 40% of the image).
- `POST /api/repost/preflight` — dry-runs the real slicer on an image and returns the plan plus
  issues, in ~1s, with no render.
- `RunRepostAsync` now validates before rendering: severity Broken throws with the reason, so
  the job fails at 8% with readable text instead of producing a broken video for Review.
- `tools/slice_sweep.py` — replays EVERY saved `out/*/post_image.png` through preflight. Those
  images are a free regression corpus of real layouts. Run it after any slicer change.
  Baseline: 103 screenshots -> 102 Info, 1 Broken (an image with genuinely no readable text).

## The crash this uncovered

`PostSliceService.IsBlank` locked a SUB-RECTANGLE and then copied `Stride * Height` bytes. For
a sub-rect lock, GDI+ returns Scan0 pointing into the middle of the bitmap with the FULL
bitmap's stride, so only `Width*4` bytes of the final row belong to you. Certain screenshots
therefore read past the allocation and killed the whole API with
`Fatal error. Internal CLR error. (0x80131506)` — no exception, no failed job, process gone.
Now copied row by row into a packed buffer. The other three LockBits sites lock the full
bitmap, where `Stride * Height` is correct; only sub-rectangle locks have this trap.

Also fixed: OCR line boxes overlap where descenders reach into the next row, and those boxes
became slice bounds directly, so consecutive slices claimed the same ~7px and both drew it.
Hit 11 of 103 screenshots. `SeparateOverlaps` moves the boundary to the midpoint.

## Failed column splits (the black-block family)

Three shapes of the same defect, all of which rendered a black rectangle for the whole video
and all of which the ORIGINAL row-and-column checks passed as clean:

1. **Sliver column** — gutter found between columns 1 and 2 of a FOUR column meme, so the
   "right column" clamped to 4px of a 719px post.
2. **One-sided comparison** — a left column found, nothing at all on the right, so the entire
   right half (the expanding-brain pictures) was in no slice.
3. Both leave a hole that exists only as an AREA: every row is covered by something and every
   column is covered by something, so one-dimensional coverage checks cannot see it.

`AbandonSliverColumns` now abandons either bad split and MERGES the group's blocks (text and
media together, in one pass) into full-width slices by row-run. A plain full-width reveal loses
the side-by-side effect but is correct, and it degrades gracefully on layouts nobody has taught
it. Merging text and media separately, or flattening columns without merging, both recreate the
overlap defect in a new place — the corpus caught both attempts.

Hole detection needs an INK MAP (`PostSliceService.InkGrid`); without it the blank margin beside
a short left-aligned line counts as a hole and 30 of 108 ordinary posts trip the check.

## Audio/reveal sync

`RepostComposer.Align` returns the indices of card lines it could not hear in the audio. A line
on the card that is never spoken still takes a reveal slot, so every line after it appears while
the PREVIOUS line is being read — the whole post runs a beat behind. The pipeline drops those
lines and re-times. Verified by transcribing vo.wav and diffing word timings against props.json
`startMs`: lag +0.00 on every line.

## The gutter scorer — tried, reverted (2026-08-20)

`FindColumnGutter` takes the FIRST gap in the middle 40% of the post with text on both sides.
No check that the resulting split makes sense. That one line is the root of the whole
black-block family above.

A scoring rewrite was attempted: rank every candidate gap on (a) text on both sides, (b)
neither side narrower than 15% of the post, (c) the two sides' text at similar HEIGHTS, then
take the best and reject everything if none qualifies. It correctly rejected the four-column
meme, the expanding brain, the split picture and the $1M thread.

**Reverted, for two measured reasons:**
1. It cannot separate a CHAT SCREENSHOT from a real comparison. Alternating message bubbles are
   geometrically identical to two columns — text left, text right, similar heights, gap down the
   middle. Both scored align=0.83. No threshold separates them; that needs a signal geometry
   does not carry (bubble fills, reading order).
2. Corpus Warn went 8 -> 17 (fat-line 3 -> 12): fewer splits meant more content merged into
   tall single slices.

The downstream guards do the rejecting instead, and the corpus sits at ~10 Warn / 2 Broken
(both Broken are one genuinely textless image). Known remaining gap: the woman-yelling-at-cat
layout has ONE OCR box per caption, so `inBand.Count < 3` refuses to even look, and both panels
appear at once. Relaxing that guard to 2 fixes the meme but makes a chat thread split badly —
the same chat-vs-comparison ambiguity. Fix the ambiguity first, then the guard.

## Profanity blur (added 2026-08-28)

Matthew: *"in the images, if you see any profanity pls blurr it out."* `ProfanityFilter` +
`ImageCensorService` blur swear words out of the pasted screenshot before it is filmed, off the
SAME OCR pass the slicing uses (`OcrLineBox.Words` now carries per-word boxes), so the censored
copy is the same size and no band moves. Only the picture - the voice still reads the post
verbatim, which he has NOT been asked about yet.

`tools/profanity_sweep.py` is the corpus check, same shape as `slice_sweep.py`.
**Baseline: 119 screenshots -> 8 with profanity, 14 words covered, 0 false positives.**

Two defects the corpus and the synthetic tests caught, both worth remembering:
- the adjacent-word JOIN (for OCR splitting "fu ck") fired on "the"+"fuck" and blurred the
  innocent word too, because a join trivially CONTAINS the stem. It must only be tried when the
  second word is innocent on its own.
- `sh1t` never arrives as "sh1t" - the OCR engine resolves the digit to a letter and returns
  "shlt", so folding leet "1" to "i" is too late and "l" has to be folded as a second spelling.


## Checking the PLAN is not checking the VIDEO (2026-09-01)

Matthew sent nine broken repost frames: *"why are you making these mistakes it keeps repeating"*.
Every one of those videos was rendered on **29 August**; the slicer fixes landed on **31 August**.
They were never re-rendered, so the Review tab still held the old output while the plans were all
correct.

**The verification was the failure, not the slicer.** `/api/repost/preflight` was run over the
whole corpus and the plans checked - and not one repost video was re-rendered to confirm the plan
reached the screen. Preflight is a dry run; it proves the slicing decision, not the render.
Re-render and look at frames before reporting a repost fix.

`POST /api/jobs/{id}/revoice` re-queues a job; for a repost that is a full re-slice plus
re-render off the stored `repost.json` and image, so it is the way to refresh old videos.

Two things the re-render exposed that the plan could not:

- **"two wolves" (79b88fd7)** now reveals left column then right, but the card draws with large
  empty white areas - the plan says gutter 456, 8 slices, severity Info, and the video still
  looks wrong. A correct plan can still render badly.
- **the crop-chain hole**: `BuildBlocks` caps a caption's crop at 10px above its own top
  (`Math.Max(Math.Min(prevBottom, b.Top), b.Top - 10)`), so any gap between 10px and gapLimit is
  shown by nothing at all. Harmless when line spacing is tight, visible when it is not.

Also fixed this round: chrome rows are cut out of the PICTURE blocks too, not merely dropped from
the narration (an iMessage screenshot still displayed "iMessage" and "Today 1:01 PM" while
correctly not saying them); a whole-line URL rule drops watermarks like "gflip.com"; and OCR
retries suspect lines - garbled ones and unusually large ones - on a 3x upscaled contrast-boosted
crop, keeping the new reading only when it scores better as English.

**Still unfixed: the Tarantino meme (a9a36fcc).** Its captions are diagonal - left at y 124-252,
right at y 416-532, 7% overlap - and its two halves are separate BANDS, so the per-band gutter
search never sees text on both sides at once. It needs a GLOBAL two-panel mode (a vertical seam
running the full height with content either side), a different mechanism from the per-band
gutter, deliberately not half-built.

Corpus after this round: Broken 2 -> 2 (the same two textless images), Warn 21 -> 8, screenshots
splitting into columns 27 -> 74.


## The autofilled text overrode everything, and a post-render gate (2026-09-01)

Matthew: *"these are still broken what the hell ... did you not add any validation at all on the
vids after rendering?? put it on a loop until its fixed"*.

**Root cause of nearly every complaint: `Settings:ocrAutofill` saves that moment's OCR into the
job, and typed text WINS over the slicer, applied to slices POSITIONALLY.** The stored text is
frozen at upload; improving the slicer changes the slice count and the frozen text then desyncs.
Measured: boycotts had 5 typed lines against 12 slices (the huge blank gaps), Tinder's line 0 was
literally "Ethan" so the voice said it however well the header rule worked. Every slicer
improvement made these WORSE. Typed text now only wins when its line count matches the slicer's.

**`RenderValidator`** gates every repost: a failing video is marked Failed and never reaches
Review (Matthew's choice - "i dont wanna see any broken or mistakes"). What it checks and why:

- **GAP** - rows of the picture no slice shows, computed from the crop ranges. EXACT.
- **ORDERING** - a comparison's right column must not start before the left finishes, from the
  reveal times in props. EXACT.
- **FROZEN** - the mp4 actually moves. The only thing left that needs pixels.

**Three pixel-based checks were built and thrown away**, each after it gave a wrong verdict:
"card lost content" followed the blurred gameplay cutting between scenes; "large flat featureless
area" could not tell a void from the Pooh meme's white caption column; and pixel ORDERING
measured when each half of the FRAME changed, but a comparison card's picture spans both halves,
so both changed the instant the card appeared. **Where props can answer a question exactly, do
not ask the pixels.**

**The crop reach (`BuildBlocks` cropTops) has now been wrong in both directions.** At a flat
`b.Top - 10` any spacing wider than 10px was drawn by nothing (two wolves came out half blank).
Removing the cap let a caption reach back over the artwork above it and the boycotts meme
rendered its title and wojaks TWICE. It is now bounded by the post's own median line height
(x1.5), which closes line spacing and cannot swallow a picture. **The validator must mirror this
rule exactly** - while it still simulated the old cap it reported phantom gaps and pushed every
comparison into the full-width fallback, which shows both halves at once.

**RESOLVED (2026-09-03):** the columns were not reaching the render at all. `ValidateBands`
measured each lane's coverage from the top of the post, so a right column that legitimately
starts halfway down looked like a 487px hole, and the pipeline dutifully abandoned the split and
re-sliced full width - both halves at once. The check now measures UNION coverage: a row is
covered when ANY slice shows it.

**BEWARE re-rendering an APPROVED job: it uploads to YouTube on completion.** Re-rendering the
nine published two videos (HoLGSx8oJEI, WM_9XXgvsgM) without anyone approving those new renders.
Check `stage`/approval before calling revoice in bulk.


## THE CARD STACKS ITS SLICES (2026-09-03)

The single fact that explains most of the layout defects, and it is not obvious from the props:
`PostCard` lays its slices out in ARRAY ORDER, one after another - it does not position them at
their source rows. Three consequences, all of which shipped as bugs:

1. **Two slices covering the same rows draw that content twice**, and the card grows by the
   repeat. The crop chain reaching up into the picture above it put the boycotts wojaks' chins on
   screen twice and a second strip of car under the two wolves; the same picture emitted both
   full width and as a column made the wolves card so tall its ending fell off the frame.
   `PostSliceService.CropTops` is now the ONE definition of a caption's crop - used by the
   builder, the hole patch and the validator - and bounds the reach by the lowest edge of
   anything above that overlaps it horizontally, in any lane.
2. **The array order IS the layout**, so it must be the post's own order, sorted on the ORIGINAL
   rows (a slice's `Top` is its crop, which reaches upward). Every slice of a comparison carries
   the group's first row so the columns stay in one contiguous run - split into two runs, the
   card draws the group's box twice.
3. **A comparison is drawn as one positioned box**, so a slice whose rows fall INSIDE that box
   must join the group (`AbsorbIntoOverlappingGroup`) or it is stacked after the box and redraws
   those rows - 312 of them on the AI art meme.

Also: a slice appears when the FIRST of its lines is spoken, so a slice holding several written
lines shows them all at once (the Tarantino meme, three at a time). `OneLinePerSlice` clusters
the OCR boxes into written lines - boxes overlapping >50% vertically are BESIDE each other, not
stacked - and cuts the slice into one per line.

**Comic grids** (`TryComicGrid`) get the panel reveal instead of text slicing: near-black
dividers >=92% dark, 3px..3% thick, on a light page, 1-3 per axis, cells >=25% wide / >=20% tall.
Measured over all 151 saved screenshots it claims exactly 7, all genuine grids. An earlier,
looser version claimed 24 - almost all of them dark-mode screenshots, where rows of white text
look like dividers. Requiring a light page is what separates them.

**Two rules the 151-screenshot corpus caught, both in changes that looked obviously right:**

- Assigning OCR rows to a slice by OVERLAP gives the same row to two neighbours - the boxes lap
  over each other wherever a descender dips into the line below - and the second copy comes out
  with no height. 19 screenshots went Info -> Broken ("a slice ends above where it starts").
  Assign by the row's CENTRE: exactly one slice owns it.
- Dropping a duplicate picture on 90% OVERLAP threw away the ten rows by which it stuck out
  above its container, and that column rendered a blank strip. Require real CONTAINMENT
  (within ~4px on every edge).

Corpus after this round: **Info 141 / Warn 8 / Broken 2, zero screenshots changed severity,
zero slice pairs drawing the same rows, and the comic-grid rule claiming exactly its 7.**


## What actually guards the output now (2026-09-03)

Matthew: *"did you make changes so that in the future i won't get any issues like these any more?"*

- **Every format is checked after rendering**, not just reposts. `RenderValidator.ValidateFileAsync`
  (exists / runs the length it was built to run / actually moves) runs on the meme and list paths
  too. The LIST format - most of what the channel publishes - previously had NO check on its
  output at all. Proved by forcing the check to fail and watching a list job stop at "checking the
  video" instead of reaching Review.
- **`tools/corpus_check.py`** replays all 151 saved screenshots and EXITS NON-ZERO against
  `tools/corpus_baseline.json` (Info 141 / Warn 8 / Broken 2, rows drawn twice 0, gate rejects 0,
  comic grids 7). Proved by planting the old crop bug: 35 slice pairs drawing the same rows,
  exit 1.
- **`tools/rerender.py`** reads `review` per job immediately before queuing and refuses anything
  approved. See [[revoice-uploads-approved-jobs]].

**A check can only see what it measures.** The first version of `corpus_check` did not catch the
planted regression at all: preflight returned the SLICER's rows, while the defect lives in the
crop `BuildBlocks` draws from. Preflight now reports `cropTop` per slice, computed by the shared
`PostSliceService.CropTops`, and the check reads that. Always verify a new check by breaking the
thing it is supposed to catch.

**Bug this turned up: no list job could be re-rendered.** `CutoutService` replaces a picture on
disk (`item_4.jpg` -> `item_4.cutout.png`) but `list.json` still named the original, so every
re-render 404'd - all 26 rejected list jobs and every future one. `RunListAsync` now resolves a
missing picture to its `.cutout.png` and writes the repaired names back to `list.json`.


## A comparison is not always two columns (2026-09-03)

Matthew: *"on the 2010 its not showing"*. "Applying for a job in... 1998 / 2010 / 2026" is THREE
columns; the slicer only ever looked for one gutter, so the middle column was swept in with the
right one - its lines merged into the right column's rows ("Create an account Apply online") and
2010 was never revealed or read on its own. Measured over the corpus, **17 saved screenshots are
three or four columns**, every one sliced as two, so this was in all of those videos. The
Claude/OpenAI/Meta/Google meme is four.

`PostBlock.Column` (0-based, null = full width) is now the truth; `Side` is only the shorthand the
renderer and the spotlight still speak. `FindColumnGutters` returns every gutter, the band walk
and the picture walk iterate `ColumnEdges`, and narration reads down column 0, then 1, then 2.

Four things the corpus caught in that refactor, all invisible from the one meme being fixed:

- a near-full-width picture in the first column pushed every later column's slices past their own
  RIGHT edge (`x=713..319`), so the shift now refuses to invert a slice;
- flattening an abandoned split clipped pictures against that group's text only - with three
  columns the text a picture spans is routinely a neighbour's, so it is clipped against all text;
- `ContentExtent` may overflow a column (portraits run past the gutter) but on a FOUR-panel meme
  the photographs are separated by a thin dark seam, so the measure ran across all four and the
  first column claimed 0..713 of a 719px post. Overflow is now allowed only for two columns;
- a heading row is ONE row, and the gutter search refused any band of fewer than two rows - so
  "1998 / 2010 / 2026" stayed a single full-width slice reading "1998 2026 2010". Counted in
  words now, not rows.

**A column's tail is not a new section.** `EndComparisonAtLastSharedRow` demoted everything below
the SHORTEST column, which tore the 2026 column's nine remaining items out of it. It now measures
each row against its OWN column's typical line spacing.

**`tools/corpus_check.py` compares only the screenshots the baseline knows.** The corpus grows
every time a repost is made, so comparing raw totals reported a regression the moment a new
screenshot warned about anything.

## Renders die when C: is full (2026-09-03)

The API exited on its own mid-render and the job came back "resuming after restart"; the render it
produced disagreed with its own props.json and its file timestamps were 20 minutes apart. **C: had
0 bytes free.** Freeing ~320MB (this session's cached plan dumps, and 12 leaked
`%TEMP%/remotion-*` folders from crashed renders) let it render correctly. Do not debug a strange
render before checking free space - see also [[remotion-render-memory]].

## The safety net had a hole exactly where the new code failed (2026-09-07)

Two videos shipped visibly broken - the punchline panel on screen from 0ms - while
`corpus_check.py` reported **"0 slice pairs drawing the same rows"**. `drawn_twice` skipped any
pair sharing a group id. That exemption was written for a CAPTION lying over a picture, which is
where the post put it; it also hid PICTURE-on-picture overlaps, and both offending pictures were
in group 0. A metric with an exemption is only as good as the exemption. It now exempts only
caption-over-picture.

**Two panels side by side measure full width.** `ContentExtent` returns the outer extent, so with
no blank corridor between two touching panels it runs from the left edge of one to the right edge
of the other; anything >=85% wide was claimed as a "shared" picture belonging to no column, which
swallowed the neighbour's panel and, taking the earliest column's turn, revealed it before a word
was read. There is nothing in the pixels to find - measured both memes, the columns either side
of the gutter are as busy as the picture itself (std 64), no seam.

The tell is **what the other columns claimed**: a genuine shared picture is bounded by the same
text in every column so every claim covers the same rows, while independent panels reach past it.
Resolved after all columns have walked, not during.

**A sliver is not a panel.** The first version of that rule read a 5px remnant at x=970..975 of a
975px post as a second panel, demoted a genuinely shared picture and tore a 27% hole in the card
(`e80b56e1`). A rival must be >=15% of the post's width. Six corpus screenshots carry a sliver
column that arises on its own, so this is not rare.

**A/B against the corpus rather than reasoning about it.** An env-var gate around the new pass,
one run with it off, one on, settled in two minutes what the plans alone could not: the pass
fixed two memes, halved a third, and caused one regression.

Baseline now 177 screenshots: Info 165 / Broken 2 / Warn 10, **2 known overlapping pairs**
(`7f5a8cad`, `92414dda`) - pre-existing, unfixed, and newly visible rather than newly broken.

## Geometry is not the video (2026-09-08)

Two rounds of "fixed" were reported off `preflight` alone, and both times Matthew came back with
the same video still broken. Two distinct reasons, both worth keeping:

**1. A fix is not in a video until the job is re-run.** The first "fixed" was verified on the
slicer while `out/<job>/props.json` still held the pre-fix plan. He was watching a stale mp4.
`python tools/rerender.py <id>` re-slices AND re-renders (and refuses approved jobs, which would
publish to YouTube). Verify there, then read frames out of the mp4 - never report a video fixed
off a plan.

**2. `corpus_check.py` cannot see time.** It replays the slicer, so it proves the CROPS are
right and is blind to WHEN each appears. Both shipped defects were timing. New
`tools/reveal_order.py` reads the rendered `props.json` and fails any job where a slice appears
before content above it in the same column (columns are independent by design, so it never
compares across them). 12 of 42 jobs on disk fail it today.

**Where the time came from:** a picture in a column took the time of that column's FIRST caption.
Right for a picture ABOVE its lines (the wolves photo under a heading), wrong for one BELOW them
- the punchline landed at 0ms under four unread captions. It now anchors to the first line below
it, falling back to the last line above when it ends the column. `PipelineService`, in the
`m.Group is not null` branch.

See also [[remotion-render-memory]] - renders need real free space on C:, and a full disk
produces a video that disagrees with its own props.

## Listen to the video (2026-09-08)

"it did not say algorithm correctly". Three rounds were spent reading props.json and arguing the
text was correct - it WAS correct: `THE ALGORITHM: "I SEE THAT!...`. Transcribing the finished
mp4 with faster-whisper took one minute and settled it: **the video said "The Algoramp."**

The voice reads an ALL-CAPS word as an initialism, and meme text is caps from end to end.
`RepostComposer.Spoken()` now lower-cases any line that is >=80% capitals, keeping the first
letter of each word and leaving tokens of <=3 letters alone so "AI", "CEO" and the "WI"/"FI" of
WI-FI still get spelled out. Audio only - the card still draws the post's own lettering. ".01"
also started reading as "point zero one" instead of being mangled.

**Transcribe the mp4 when a complaint is about what the video SAYS.** Reading the text that was
passed to TTS cannot show a pronunciation fault, and that is where three rounds went:

    from faster_whisper import WhisperModel
    WhisperModel("small.en", device="cpu", compute_type="int8").transcribe(mp4)

## A caption OCR ran together is not a username (2026-09-08)

"the manager not being shown wtf" - a blank white strip where a caption belonged. `IsNoise` was
innocent; `UsernameSlugLine` (`^[a-z0-9]+([-_][a-z0-9]+)+$`, there to drop handles like
`the-lad-bible`) ate it. "INSTALL THE AI DIRECTLY INTO THE WI-FI" came back from OCR as
`INSTAUTBEAIDIRECTLYIHTOTHEWI-R` - one token, one hyphen, from WI-FI. Segments are now capped at
15 chars each and 4 of them.

It cost TWO defects at once, which is why it was hard to see: the dropped line left rows no slice
covered (the blank strip), AND it cost the typed text a slice to align to, so `RewordFromTyped`
smeared two panels into one spoken chunk ("EXACTLY ZERO BACKUP PLANS MY MANAGER ASKING IF WE CAN
INSTALL"). A dropped OCR line damages the narration of its NEIGHBOURS, not just itself.

**Trace the stages rather than reason about them.** An env-gated `TraceBlocks` dump after each
slicer stage found this in two runs, after three wrong static guesses about where the block
vanished.

## Bands must AGREE on a gutter, not be forced to share one (2026-09-09)

"the bottom line showed at the same time" - a two-panel comic whose punchline ("There are 4
rules") was revealed with the setup it answers. The slicer found the right gutter and threw it
away: `columnNote` said `'1 found, 0 kept - discarded by one-sided:1'`.

The top panel has a character on the LEFT and speech text on the RIGHT, which is the exact shape
of a column split, so it claimed a gutter at x=340. Consecutive splitting bands are forced to
share one gutter (so the centre seam cannot zig-zag), the fake gutter and the real one at x=460
have no overlap, the intersection came out EMPTY, the split collapsed to one column, `oneSided`
discarded it - and with no columns `MergeIntoRows` merged the two side-by-side captions into one
full-width row, `'There are 4 rules Every guy on earth'`, revealed at once.

Three changes, each of which the corpus had an opinion about:

- bands now share a gutter only while they still AGREE on one; the band->group map is decided in
  that loop instead of being re-derived from `prevSplit` in the walk below, which is only the
  same thing while every splitting band agrees;
- a gutter justified only by a PICTURE on one side may JOIN a comparison but not OPEN one -
  **conditional on the post splitting by words somewhere else**. Demanding words outright was
  over-correction: the pear meme has no text-justified gutter at all, lost its columns and its
  picture entirely (`Warn`, 169px in no slice);
- a full-width picture is CUT AROUND a neighbouring column's text, emitting the parts above and
  below. Trimming it to the first caption instead - the obvious first attempt - discarded
  everything below that caption and left 660px of one post in no slice.

**A 2x2 grid is read ACROSS then down.** `NarrationOrder` read each column out whole, which is
right for a table (all of 1998, then all of 2010) and wrong for panels: the voice said "my
manager asking if we can install the AI directly into the Wi-Fi" at 4.9s while that caption
stayed hidden until 14.0s, because the typed post - and so the audio - is in panel order. The
tell is each column's OWN picture sitting between two of its captions. `RenderValidator` had to
learn the same rule or it fails the very videos the walk now produces - it checks one panel ROW
at a time. **This path is still unverified by a render**: the only meme that exercises it
(`14096ecc`) is approved, and `rerender.py` correctly refuses it - see
[[revoice-uploads-approved-jobs]].

Baseline now 182 screenshots: Info 169 / Broken 2 / Warn 11, 2 known overlapping pairs.

## A failed video is kept, and shown (2026-09-09)

The render gate's rule stands - a video that fails its own check is never publishable, which is
what "i dont wanna see any broken or mistakes" asked for. What was wrong was throwing the FILE
away: it set `job.VideoPath = null`, so a failure left a one-line reason and nothing to look at.
"what picture was this" / "can you still produce the video even if it fails so that i can know
what is wrong with it".

Now: the mp4 and thumbnail survive (the thumbnail is grabbed BEFORE the check so the card has a
poster), `HasVideo` is true for Failed as well as Done, and Review has a **Failed** section above
History - video inline, full reason, full-screen button, and a Re-render button. It still cannot
reach YouTube: `JobManager.Approve` refuses anything that is not `Done`, and a failed job never
is. Old failures still show "No video was produced" - their files were already discarded; a
re-render brings them back.

## A chat bubble is not a byline (2026-09-09)

`67983133` (the "go eat my child" chat screenshot) failed with "Rows 178..254 are drawn by two
different slices". Nothing in the LAYOUT separates the first bubble of a chat screenshot from a
username: one line, a gap beneath it, more text below. That gap was absorbed as an avatar, so
"Aunt, on my way back home" became the header - left on screen from frame 0, NEVER SPOKEN, and
its slice stretched down over the absorbed gap drew the same rows as the picture there.

**What the layout cannot tell, the typed post can**: a row whose words appear in the typed text
is the post, not chrome. Matthew types what the picture says; the byline is the one thing he
leaves out. Both defects went with it.

**`corpus_check.py` cannot see this rule at all** - it posts the image to `/api/repost/preflight`
with no typed text, so `typedLines` is null there and the branch never runs. The check can only
say nothing ELSE regressed. Anything that keys off typed text needs a real job to verify.

## The check does not get the last word (2026-09-10)

"this pass for me, put it on review" - the finished-video check flagged 78px of picture drawn by
no slice, Matthew watched it and judged it fine. The check errs towards failing, and his eyes are
the authority on his own videos.

`POST /api/jobs/{id}/accept-check` -> `JobManager.AcceptFailedCheck` moves a FAILED job with a
video to `Done` + review `pending`. It goes no further than the review queue; Approve is still a
separate press, and that is the step that uploads. What the check objected to is KEPT on the job
as `CheckOverride` and shown on the review card ("You passed this despite its check: ..."), so
nothing silently forgets that it was overruled. Button is "Looks fine" on the Failed card.

## Half a row drawn by nobody (2026-09-10)

"also these are broken" - black bars lying across Steve Jobs' eyes and a second presenter's face
in the Apple keynote meme. They looked exactly like censor bars; the profanity scan reported ZERO
hits and the source image was clean. They were HOLES: rows nothing drew, showing the card's own
dark ground, which happened to land on two faces.

Three causes, all in comparisons:

- **The shift opens a gap.** A slice moves right when an earlier column's picture overlaps ANY of
  its rows, but the whole slice moves while the picture reaches only part way up it. `"a stylus?"`
  was pushed to x=492 by a photo starting 23 rows lower, leaving rows 142..166 at x=168..492 drawn
  by nothing - straight across his eyes. Slices now reach to whatever the NEXT column actually
  starts at over their own rows, not to the nominal gutter.
- **Some regions have no slice at all** - column 0 had nothing at rows 588..626 - so no amount of
  widening helps. New `PatchColumnHoles` fills them with the post's own pixels.
- **The finished-video check counts ROWS.** A row counts as covered when any slice draws part of
  it, so a row half-drawn passes. That is why this shipped looking broken and reported clean, and
  it is the same shape of blind spot as the group exemption in `drawn_twice`.

Two bugs of mine on the way, both caught by the corpus rather than by reasoning: the patch first
measured coverage only inside its own group (so it copied the next group's caption over the
original), then used band boundaries taken only from its own group (same result, subtler - a
caption ending 2px short of the band was missed as cover). **Coverage and band cuts must both come
from EVERY slice in the post**, not the group's.

Baseline 185 screenshots: Info 176 / Broken 2 / Warn 7 - the patch also fixed four other posts'
hole warnings (`3b81d209`, `6b3089e4`, `7504e28c`, `801f4a09`), Warn 11 -> 7.

**A whole-corpus run of ERROR means the API died, not that the slicer broke.** Every preflight
after the crash returns connection-refused and the checker reports `Info -> ERROR` for each.
Check the process is alive before believing it.

## Two comparisons must not share rows (2026-09-10)

Fixing the holes on the Apple keynote meme uncovered the next fault, and that one uncovered a
third. Worth remembering as a chain, because each looked like "still broken" to Matthew:

**1. The comparisons overlapped in rows.** Group 0 ran to row 628 while group 1 began at 588: the
picture walk measures a column's gap from one of its captions to the next, and the next caption
belonged to the comparison BELOW. The card lays each comparison out as its own box and STACKS the
boxes, so those 40 rows - the 2026 presenter's head - were drawn once in each box, one above the
other. `SeparateGroups` now clips a comparison's pictures at the next comparison's first row.
Pictures only: a caption never spans a boundary and cutting one would halve its words.

**2. `SeparateOverlaps` only looked at ROWS.** It pushes apart slices in one lane that share rows -
but two slices sitting SIDE BY SIDE share rows by design. The patch filling the hole to the left of
"iPhone Duo" made it trim that caption from 588..626 to 588..607, cutting the text off at the
waist. It now requires an overlap in both axes, which is what "the same pixels twice" means.

**Assigning a patch to a column is unreliable once slices have been widened.** The gap at x=0..492
went to column 1 because the gutter sat at 168 and "2026" had been widened to full width, so every
column's spread contained the gap's middle. It only affects WHEN the patch appears, and fixing
SeparateOverlaps made the placement harmless - but do not trust column spread as a proxy for
column identity after the widening pass.

Corpus held at 185 screenshots: Info 176 / Broken 2 / Warn 7, no regressions.

