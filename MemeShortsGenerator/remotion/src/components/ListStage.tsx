import { type Caption } from "@remotion/captions";
import { loadFont } from "@remotion/google-fonts/Baloo2";
import { Img, staticFile, useCurrentFrame, useVideoConfig } from "remotion";
import { RenderBeat } from "../types";

// The caption face has to be LOADED, not merely named: naming "Baloo 2" in fontFamily without
// this silently fell through to Comic Sans MS, which is what every list short rendered in.
const { fontFamily: CAPTION_FONT } = loadFont("normal", { weights: ["800"] });

/**
 * The "list short" look, measured off a reference Short frame by frame: a heavily blurred
 * gameplay background, one wide picture card that drifts and tilts, and karaoke captions in a
 * heavy outlined face sitting ON the card.
 *
 * Each beat is one picture plus one spoken line, so the card swaps as the narration moves on.
 * The captions come from the same word timings the rest of the pipeline already produces.
 */

// Card geometry, measured off reference frames at 1080x1920 rather than guessed:
// the card spans x 75..1010 (935 wide, ~87% of frame) and sits centred near y 900-960,
// LOWER than the middle of the frame. It breathes visibly — across the reference the card
// swings between roughly 935 and 1130 wide, which a 0.6% scale wobble does not read as.
const CARD_W = 950;
const CARD_CENTRE_Y = 900;
const CARD_RADIUS = 55;

// Caption cap height measures ~95-100px in the reference, which is a ~140px face — the 112
// used before sat noticeably small against it.
const CAPTION_SIZE = 140;

// Captions ride in the upper third of the card rather than across its middle. The card top is
// at CARD_CENTRE_Y - 295, so this is about a fifth of the way down it — clear of whatever the
// photograph is actually of.
const CAPTION_CENTRE_Y = CARD_CENTRE_Y - 165;

export const ListStage: React.FC<{
  beats: RenderBeat[];
  captions: Caption[];
  /** Optional sticker that rides beside the caption text. */
  mascot?: string | null;
}> = ({ beats, captions, mascot }) => {
  const frame = useCurrentFrame();
  const { fps } = useVideoConfig();
  const nowMs = (frame / fps) * 1000;

  // Hold the last picture once the narration ends rather than blanking the card.
  const beatIndex = beats.findIndex((b) => nowMs >= b.startMs && nowMs < b.endMs);
  const beat =
    beats[beatIndex] ??
    (beats.length > 0 && nowMs >= beats[beats.length - 1].endMs
      ? beats[beats.length - 1]
      : beats[0]);
  if (!beat) return null;

  const t = frame / fps;

  // The card does NOT rotate and does NOT sway. Tracked across all 21s of the reference with
  // minAreaRect, its angle stays inside ±0.9° for the entire body of the video (sd 0.80°,
  // and the only larger value is the outro). What it does is ZOOM: within a beat its width
  // creeps by ~5-6% (e.g. 1000px -> 1058px over 1.6s), which also nudges the centre because
  // the zoom is anchored low. Earlier versions rocked the card side to side, which is
  // invented — nothing in the reference does that.
  const beatT = Math.max(0, (nowMs - beat.startMs) / 1000);
  const scale = 1 + Math.min(beatT, 4) * 0.016;

  // Captions grouped into short bursts. The reference changes text every 0.5–0.9s, which is
  // two to four words at its ~200 wpm delivery.
  const beatCaps = captions.filter(
    (c) => c.startMs >= beat.startMs - 1 && c.endMs <= beat.endMs + 1,
  );
  // Paged by WORD COUNT, not by pauses. createTikTokStyleCaptions groups on the gaps between
  // words, so a line delivered without a breath became one page of the whole sentence and ran
  // clean off both edges of the frame. The reference always shows two to four words, whatever
  // the delivery does, so that is what gets built here.
  const pages: { text: string; startMs: number; endMs: number }[] = [];
  for (const cap of beatCaps) {
    const last = pages[pages.length - 1];
    const words = last ? last.text.trim().split(/\s+/).length : 0;
    // 16 chars, not 22: the reference's pages are short ("kicked out", "which state",
    // "California"), and that shortness is exactly what lets the face stay huge.
    if (!last || words >= 3 || last.text.trim().length + cap.text.length > 13) {
      pages.push({ text: cap.text, startMs: cap.startMs, endMs: cap.endMs });
    } else {
      last.text += cap.text;
      last.endMs = cap.endMs;
    }
  }
  let pageIndex = -1;
  for (let k = pages.length - 1; k >= 0; k--) {
    if (nowMs >= pages[k].startMs - 1) {
      pageIndex = k;
      break;
    }
  }
  const page = pages[pageIndex];

  // An integer hash, not the usual sin(seed)*43758 trick — that one is badly biased for small
  // sequential seeds (mean 0.557 over the seeds this actually uses).
  const rand = (seed: number) => {
    let x = Math.imul(seed ^ 0x9e3779b9, 0x85ebca6b);
    x = Math.imul(x ^ (x >>> 13), 0xc2b2ae35);
    x ^= x >>> 16;
    return (x >>> 0) / 4294967296;
  };

  // MASCOT — measured by tracking the sprite's eyes through every frame of the reference.
  //
  // It is NOT parked at a fixed spot. Five separate appearances across 21s, on screen only 40%
  // of the video, and every one of them differs:
  //   #  secs   x drift        y drift        scale
  //   0  1.0    133 -> 76      ~997           +12%
  //   1  1.5    135 -> 76      ~888           +14%
  //   2  2.6    856 -> 872     755 -> 804     +31%
  //   3  1.2    162 -> 234     574 -> 650      -4%
  //   4  1.7    143 -> 191     693 -> 778      -4%
  // So: it appears on SOME beats, at its own height and size each time, and it slides
  // (40-90px) and rescales while it is up.
  const mSeed = beat.index * 101 + 7;
  const mascotShown = mascot != null && rand(mSeed) < 0.62;
  const mascotOnRight = rand(mSeed * 5) < 0.4;
  const MASCOT_W = 250 + rand(mSeed * 11) * 90;                 // measured eye-sep 50..91
  // Eye level lands between ~380px above the card centre and just below it.
  const mascotEyeY = CARD_CENTRE_Y - 360 + rand(mSeed * 13) * 400;
  const mascotDriftX = (rand(mSeed * 17) - 0.35) * 130;         // measured -59 .. +72
  const mascotDriftY = (rand(mSeed * 19) - 0.4) * 110;          // measured -11 .. +85
  const mascotGrow = 1 + rand(mSeed * 23) * 0.3;                // measured -4% .. +31%
  const beatFrac = Math.min(1, Math.max(0, (nowMs - beat.startMs) / Math.max(1, beat.endMs - beat.startMs)));

  // Size the line to the space it actually has. Pages vary from "Texas" to "demolished the
  // Eiffel Tower", and at a fixed 140px the long ones wrapped into a five-line block that
  // buried the picture. Shrinking the rare long page keeps every caption on one line.
  const pageText = page ? page.text.trim() : "";
  // The caption is centred in the space BESIDE the character, not across the whole frame. In
  // the reference the two never overlap — there is a 30-40px gap between the sprite and the
  // nearest letter, on both sides.
  //
  // The keepout differs per side because the anchors do: the sprite's transparent margin is
  // 15% on its left and 9% on its right, and the right anchor is inset further from the frame
  // edge than the left one. A single symmetric value left the character sitting on the text.
  const padNear = mascotShown ? (mascotOnRight ? 376 : 275) : 40;
  const available = 1080 - 40 - padNear;
  // No floor: a floor is what let an over-long line keep its size and run off the frame. Pages
  // are short by construction now, so this only ever trims the occasional very wide one.
  // 0.48em per character is measured off a rendered Baloo 2 800-weight line, not guessed.
  const fontSize = Math.min(CAPTION_SIZE, available / Math.max(1, pageText.length * 0.48));

  return (
    <>
      <div
        style={{
          position: "absolute",
          left: (1080 - CARD_W) / 2,
          top: CARD_CENTRE_Y - CARD_W * 0.31,
          width: CARD_W,
          // Zoom only — no rotation, no lateral drift. Anchored low, matching the measured
          // behaviour where the card's bottom edge stays put while it grows.
          transform: `scale(${scale})`,
          transformOrigin: "50% 80%",
          borderRadius: CARD_RADIUS,
          overflow: "hidden",
          boxShadow: "0 24px 60px rgba(0,0,0,0.45)",
          background: "#d9d9d9",
        }}
      >
        <Img src={staticFile(beat.panel)} style={{ width: "100%", display: "block" }} />
      </div>

      {/* Slides and rescales across the beat it appears on, from its own starting point.
          Behind the words, so any overlap never costs a letter. */}
      {mascotShown ? (
        <Img
          src={staticFile(mascot!)}
          style={{
            position: "absolute",
            // The sprite carries transparent margin (~15% left, ~9% right), so these offsets
            // put the DRAWN character where the tracked eye positions say it should be.
            ...(mascotOnRight
              ? { right: 80 - mascotDriftX * beatFrac }
              : { left: -40 + mascotDriftX * beatFrac }),
            // Eyes sit ~45% down the sprite, so back off from the measured eye level.
            top: mascotEyeY - MASCOT_W * 0.45 + mascotDriftY * beatFrac,
            width: MASCOT_W,
            transform: `scale(${1 + (mascotGrow - 1) * beatFrac})`,
            transformOrigin: mascotOnRight ? "100% 50%" : "0% 50%",
            zIndex: 0,
            filter: "drop-shadow(0 6px 14px rgba(0,0,0,0.35))",
          }}
        />
      ) : null}

      {page ? (
        <div
          style={{
            position: "absolute",
            left: 0,
            right: 0,
            // Sits in the UPPER part of the card, not across the middle of the picture —
            // dead-centre buried the subject of every photo behind the words.
            top: CAPTION_CENTRE_Y - fontSize * 0.62,
            display: "flex",
            alignItems: "center",
            justifyContent: "center",
            paddingLeft: mascotOnRight ? 40 : padNear,
            paddingRight: mascotOnRight ? padNear : 40,
            zIndex: 1,
          }}
        >
          <span
            style={{
              fontFamily: `${CAPTION_FONT}, "Comic Sans MS", "Arial Rounded MT Bold", system-ui, sans-serif`,
              fontSize,
              fontWeight: 800,
              lineHeight: 1.05,
              color: "#fff",
              textAlign: "center",
              whiteSpace: "nowrap",
              position: "relative",
              zIndex: 1,
              // Stroke UNDER the fill, or a heavy outline eats into the letterforms and the
              // words stop being readable at this size. Scaled with the face so a shrunk line
              // doesn't turn into a black blob.
              WebkitTextStroke: `${Math.round(fontSize * 0.115)}px #000`,
              paintOrder: "stroke fill",
              letterSpacing: -1,
            }}
          >
            {pageText}
          </span>
        </div>
      ) : null}
    </>
  );
};
