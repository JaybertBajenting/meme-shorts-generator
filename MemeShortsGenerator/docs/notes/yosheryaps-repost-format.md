---
name: yosheryaps-repost-format
description: "Measured breakdown of @YosherYaps's VERBATIM-REPOST format (real social post read word-for-word, revealed one wrapped line at a time) — a second, simpler format than the storyboard one"
metadata: 
  node_type: memory
  type: reference
  originSessionId: d67c83dc-2bfe-406a-80da-eef6a94772eb
  modified: 2026-08-17T02:34:14.835Z
---

Measured 2026-08-15 from `youtube.com/shorts/73TI7ghyWMY` — "Genius Anti-Theft Method 💀",
2.1M views, uploaded 2026-08-12. Matthew supplied the source LinkedIn post (Stephen Cotter,
contactless-terminal-behind-the-door joke) and asked whether it could be recreated.

**This is a DIFFERENT format from the one in [[yosheryaps-format-study]].** There is no written
script and no generated artwork. He takes a real, already-funny social post and reads it VERBATIM.
Whisper transcript == the post text word for word, including "I'm up -£45".

MEASURED (1080x1920, 60fps, 23.21s):
- Card spans x **58..1018 = exactly 960 px** (88.9% of frame width). x0/x1 stdev ~0 — **horizontally
  locked**, never scales, never tilts.
- The whole card **drifts UPWARD ~6.7 px/s** for the entire video (subtle continuous pan). It does
  NOT re-centre — it grows downward while translating up.
- Body text appears **one WRAPPED LINE at a time** (not one paragraph, not one beat), as a **hard
  cut**, synced to narration. Height jumps measured at t≈2.8 (+56), 5.6 (+42), 6.9, 7.0 …
- The post's embedded image sits **below** the text at full card width (960x626, AR 1.53) and is
  **pushed down** as lines are added. Header (avatar / name / headline / "4d · 🌐" / +Follow) is
  present from frame 0.
- Narration: **80 words, first word 0.00s, last ends 22.82s, 210 WPM** — matches the ~190-210 band.
- Background: Minecraft parkour with hard cuts. **No fake comment** — the post's own last line is
  the punchline.

**THE MECHANIC — corrected 2026-08-15 after getting it wrong twice.** "Changed horizontally on
the captions" means the screenshot is **cut into HORIZONTAL BANDS, one per caption line, and the
bands are uncovered one at a time.** The pixels on screen are always the original screenshot's.
He does NOT re-typeset the text.

Proof: zoom 3x into a text line of his frames — heavy JPEG ringing and colour fringing, i.e.
upscaled screenshot pixels, not crisply rendered type. The header differences vs Matthew's
screenshot (2nd vs 3rd+, "Head of Agency @ Synthesia" vs "Account Executive", 4d vs 1yr) are
just a capture taken 4 days after posting from a different account, and the different line wrap
is simply HIS capture's own wrap at his window width — not a re-layout.

**Why this matters:** rebuilding the text in a matching font puts the words on screen TWICE,
because a pasted screenshot already contains them — which is always, since the screenshot IS
the post. Matthew hit exactly this and was (rightly) annoyed. Never redraw the post text.

**BUILT 2026-08-15** as a third `ContentType` ("repost") alongside meme/finance — a mode picker
on the Generate tab with a paste-a-screenshot form. How it works:
- **Line positions come from the OCR ENGINE, not from pixel-gap analysis.**
  `IOcrService.ReadLinesAsync` returns each line's bounding box (union of its words'
  `BoundingRect`), and `PostSliceService.Analyze` turns any text-free stretch taller than
  ~1.6x the typical line into a PICTURE block. One OCR pass gives words + geometry together,
  so slicing and narration can never disagree.
  *I rewrote pixel-gap detection THREE times and it broke on every new layout — an avatar
  bridging the rows, page chrome down both margins leaving no blank row anywhere, tight line
  spacing, a photo in the middle. Do not go back to it.*
  Validated on three real captures: Instagram 6 lines, Instagram 5 lines, 4chan greentext 9
  lines + a 653px photo, all correct.
- Small refinements that matter: a round avatar hangs below the byline, so a SHORT picture
  block right under the header is absorbed into the header (a tall one is a real photo);
  picture blocks are moved to the END of the stack, because a photo sitting mid-post meant the
  text below it was swallowed as "media" and never read out; and attachment metadata lines
  ("swaos.jpg", "1.02 MB JPG" on image boards) are dropped entirely — Matthew: *"skip lines
  that look like filenames, it does not make any sense"*. Both regexes must match the WHOLE
  line so a real caption mentioning a file survives.
- **WHERE PICTURES GO — settled 2026-08-15 after four rewrites. NOTHING is ever moved; every
  picture keeps its own place in the post.** Only two cases remain:
  - **several pictures** (multi-panel meme, a photo beside each quote): each is revealed with
    the row it heads, or the quote is torn away from its photo;
  - **one picture**: on screen from the VERY FIRST FRAME, wherever it sits — above the text on
    an image board, below it on Instagram, between on a news article — and the words build
    around it. Matthew: *"on these types of video, please show the picture as well."*
  The four dead ends, so they are not retried: "always move pictures to the end" (broke every
  layout except Instagram/greentext); "keep it in place only when captions sit above AND
  below"; "keep them in place only when there are several"; and holding a leading photo back
  as a payoff — Matthew first asked for that on the greentext, then reversed it ("on second
  thought I changed my mind, show the picture on the greentext as well").
- **PICTURES ARE NEVER SPLIT AT THE GUTTER.** Matthew's own tip, after several broken videos:
  *"don't include the pictures that break line by line, only the text."* Cutting a picture into
  a left and right half that arrive at different moments put half a face on screen beside a
  blank rectangle. But each picture's EXTENT is measured **within its own column** — measuring
  text-free gaps across the full width ended a picture at the row where the OTHER column's
  caption began, so it came out sliced off with blank space under it. Measuring per column is
  not splitting: nothing is halved, each picture is just found whole where it actually lives.
- **A stray single LETTER on a short line is OCR noise** — "i sleep" written across a photo came
  back as "Q rsleep" and the voice read out the Q. Only "a"/"i" stand alone; digits are fine
  ("Step 1:"); capped at 3 words so a real caption is never at risk.
  *Two attempts to instead detect "is this text on a photograph?" from pixels both silently ate
  real punchlines — greentext's "i don't tip", the comic's "INTO MY HOUSE!" — and one silenced
  a whole meme. Do not try that again; the narrow rule about the actual defect is the one that
  worked.*
- **TWO-COLUMN COMPARISON MEMES** (cars-in-action-movies vs horror-movies): slices are cropped
  per COLUMN, not full width, and the card lays a comparison out as two side-by-side stacks.
  The left column builds line by line on its own, THEN the right appears beside it — Matthew
  picked this over row-by-row. One comparison finishes before the other starts, and a run of
  split bands separated by photos counts as ONE comparison sharing one gutter. NO blurring of
  the finished column: he asked for it, saw it, and had it removed ("show the two of them").
- **FLOW DIAGRAMS** (arrows between panels) are a separate path — `DiagramSliceService`. It
  only claims images with 2+ arrows that are small, elongated and floating in whitespace
  (looser tests read a red car as arrows). Panels light up in place on a fixed canvas via ONE
  copy of the image with un-reached panels covered over — drawing a cropped copy per panel
  OOM-crashed the render. **The reading order comes from the TYPED TEXT**, matched to panels by
  longest-common-subsequence: arrow-following detects the arrows correctly but panel extents
  overlap them, so the deduced order is wrong. Diagram captions are 1–2 words, so each label is
  voiced as its OWN clip and stitched with a fixed 1.1s silence — leaving the gaps to the voice
  gave 1.0s after one label and 2.3s after the next, which read as a stall.
- The BACKEND decides each block's crop range, in document order; the renderer just draws what
  it is given. When PostCard tried to infer "the gap above this block" to preserve line
  spacing, it broke as soon as pictures moved to the end — the gap above the first caption was
  the entire photo, so the photo appeared TWICE in the video. Only visible in rendered frames,
  not in props.json: always check frames, not just the JSON.
- `PostCard.tsx` renders TWO crops of the same screenshot: a growing top crop (header + lines
  said so far) and the media crop pinned below. `cardBackground` is sampled from the image so
  the growing edge never flashes white on a dark-mode capture.
- The typed-text box is now only the CORRECTION path for OCR mistakes; text is optional when
  an image is supplied. `RepostComposer.Wrap` + `WrapWidthPx` survive only for the rare
  no-image fallback — they are NOT the main path.
- Paragraphs ending on an emoji strip to no punctuation, so the spoken script ran sentences
  together. `SpokenScript` inserts a stop at paragraph boundaries.
- **`RepostComposer.Align` must use LONGEST COMMON SUBSEQUENCE, not a greedy forward scan.**
  The prototype used Python's `difflib.SequenceMatcher`; the first C# port was a greedy walk
  with a 4-word lookahead that only advanced its cursor on a match. One word the voice said
  differently from the text ("swaos.jpg" read as "swaos dot jay peg") desynced it forever and
  every later line inherited ONE timestamp — the whole post appeared at once. LCS skips
  insertions on either side and recovers.
- Titles: blank title -> `ICodexClient.GenerateRepostTitleAsync` with up to 40 real scraped
  @YosherYaps titles. Pass the OCR'd band text, NOT `input.Text` — that is empty in the normal
  slice case, and passing it made the titler silently fall back every time.

Testing note: generation is usually PAUSED with a big Codex backlog, and `ResumeAll()` starts
ALL paused jobs. To run one job for real without touching that queue, launch a second instance
with its own state: `dotnet run --no-build -- --urls=http://localhost:5199
--Pipeline:StateDbPath=<tmp>\test.db --Pipeline:OutputRoot=<tmp>\testout`. A fresh db means
generationPaused is unset, so jobs run immediately. (Env vars did NOT work — appsettings' `Urls`
won and it crashed on the busy port; command-line args do.)

**Original build sketch** (nothing here needs Codex or image_gen):
input = post text + image + author chrome → TTS reads it verbatim → faster-whisper word timings →
each pre-wrapped line reveals at its first word's timestamp (`CaptionBuilder.AlignToSpeech`
already does this mapping). New Remotion component alongside `PanelBoard.tsx`: fixed 960 width,
lines as an explicit array (pre-wrap in the backend — do NOT rely on CSS auto-wrap, or the reveal
boundaries won't match what's drawn), image row last, slow upward drift, zero fade.

Flag raised with him: these are real identifiable people's names/photos/posts, so sourcing and
whether to anonymise the author chrome is his call.

Related: [[meme-shorts-generator-project]].
