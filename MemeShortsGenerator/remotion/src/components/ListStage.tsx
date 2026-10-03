import { type Caption } from "@remotion/captions";
import { Easing, Img, Loop, OffthreadVideo, Sequence, interpolate, spring, staticFile, useCurrentFrame, useVideoConfig } from "remotion";
import { RenderBeat } from "../types";

/**
 * The CobbleCat list-short look, re-measured 2026-09-26 across his 20 latest Shorts plus his
 * four biggest hits (downloaded at 1080p60 and read frame by frame; numbers below are measured,
 * not eyeballed, unless they say otherwise).
 *
 * What changed from the 2026-09-01 version, and why - that one was fitted to a media-filled
 * card with big 2-4 word captions, and his current videos are not that:
 *
 *   - the card is a SMALL STICKER, ~700-950px wide (median ~800), pinned at the frame centre.
 *     Ours ran 950-1080 and cut to full frame / full bleed 18% of the time; he almost never does.
 *   - it casts a hard OFFSET shadow down-right only, and its edge is hand-cut, not a clean rect.
 *   - captions are Comic Sans MS Bold, 1-2 words a page, sized WITH the card (~0.155 x width).
 *   - the mascot hangs off the card's LEFT EDGE in front of it, not off the frame edge.
 *   - a new picture every ~1.2s comes from the SCRIPT now (one short line per picture); the
 *     renderer only re-frames a picture when a line runs long.
 */

const CARD_CENTRE_X = 540;

// Card centre y: median 959 across 14 tracked Shorts; per-video sd 25-65px (the larger sds were
// the tracker catching a caption or the mascot). The old +/-190px spread was invented.
const CARD_CENTRE_Y = 960;
const CARD_Y_JITTER = 40;

// Card width, measured by hand on ~60 frames across 9 Shorts after the automatic tracker kept
// merging the mascot into the card: p10 ~700, median ~800, p90 ~940. Two clusters - a ~730-760
// "small" state and an ~860-950 "big" one. Full frame is rare (one frame in nine videos).
const CARD_W_MIN = 700;
const CARD_W_MAX = 950;
const CARD_BIG_CHANCE = 0.06;
const CARD_BIG_W = 1010;
const CARD_MAX_H = 1120;

// Shape. White cards (a cut-out subject on white) come out near square - 733x722 on the gelatin
// short - because a caption band sits above the subject. Media cards take the media's shape.
const MEDIA_ASPECT_MIN = 0.72;
const MEDIA_ASPECT_MAX = 1.35;
const WHITE_ASPECT_MIN = 0.92;
const WHITE_ASPECT_MAX = 1.3;

// Corner: the edge reaches full width ~80px below the top on a 733px card, i.e. r ~= 0.11 W.
const CORNER_FRAC = 0.11;
// Hand-cut edge: straight runs vary +/-1.5px and the long edges bow by a few px.
const WOBBLE_FRAC = 0.005;

// SHADOW, profiled across the gelatin card's edges: NOTHING on the top or left edge; on the
// right and bottom the background darkens ~50% right at the edge and recovers over ~70px, with
// the half-way point at +38px right / +36px down on a 733px card (sigma ~15px).
const SHADOW_DX = 38 / 733;
const SHADOW_DY = 36 / 733;
const SHADOW_SIGMA = 15 / 733;
const SHADOW_ALPHA = 0.5;

// CAPTION. Two clean samples fitted Comic Sans MS Bold to within 1-2px: "can" = 116px on a 733px
// card, "controlled" = 136px on an 888px card - the face scales with the card (0.158 / 0.153).
// Outer stroke 11-12px = ~0.095em. CSS strokes are centred on the outline and paint-order puts
// the fill over the inner half, so the stroke width asked for is twice what shows.
const CAPTION_EM = 0.155;
const CAPTION_STROKE_EM = 0.19;
// Top of the caption box sits ~0.11 of the card's height below its top edge ("can": 81px on a
// 722px card). Matthew wants captions in the UPPER part of the card, so they always go here.
const CAPTION_TOP_FRAC = 0.1;

// White-card picture box: the gummy bear occupies 0.25-0.88 of the card's height, centred.
const WHITE_PIC_TOP = 0.25;
const WHITE_PIC_BOTTOM = 0.07;
const WHITE_PIC_SIDE = 0.09;

// SHOTS. With a picture per short line, most lines are one shot. A line that runs long is
// re-framed once - his haribo bag holds through "is ACTUALLY a WEAPON" and punches in from ~68%
// to ~85% of the frame on the same picture.
const SHOT_MS = 1100;

// Arrival. Caption pages and most picture changes are HARD CUTS (one frame). Some new cards grow
// in from ~0.88 over ~6 frames (the bone at 4.90-5.00s). No card pops up from nothing.
const GROW_CHANCE = 0.4;
const GROW_FROM = 0.88;
const GROW_FRAMES = 6;

// MASCOT. Straddles the card's left edge, in FRONT of it, and rides the card - moves when it moves.
//
// HOW IT GETS THERE, re-measured 2026-09-27 by tracking his cat's eyes through every frame of 18
// Shorts (35 appearances) and reading 60fps strips around every entrance and exit. The earlier
// study only measured where it sits, and "entrance: spring in from the left" was a guess that
// rendered as a visible ~90px glide-and-settle after every cut. His cat never does that:
//   - it ARRIVES ON A PICTURE CUT, already in place in the same frame as the new picture. Of 21
//     mid-video entrances, 20 land within one frame of a cut. No motion afterwards.
//   - now and then (3 of ~12 clean entrances) it SNAPS IN from off-screen left in 3-4 frames,
//     right after a caption change - no overshoot.
//   - it LEAVES ON A PICTURE CUT too (25 of 35), sometimes sliding back out left.
//   - it is on screen from the FIRST FRAME in 10 of 18 videos.
//   - an appearance lasts ~1.3s median (0.4-4s) - one or two pictures - about two per video.
//
// And it IS ANIMATED on the way in and out - Matthew spotted it, the first pass missed it because
// the contact sheets were too small to show a tilt. Measured from the tilt of the eye line in every
// frame (a second tracker pass):
//   - ENTRANCE SWING: on about 2 of 3 entrances it arrives TILTED, median ~11 deg (5-26), about two
//     in three clockwise, and rocks back upright like a toy on its base - the pivot is its FEET
//     (16 deg of tilt moved the eyes 66px sideways). One overshoot of 30-60% to the other side,
//     settled in ~6-10 frames, with a small size pulse and motion blur on the fast frames.
//   - A POSE CHANGE on a cut while it stays up replays the swing (new drawing, same rock).
//   - EXIT TIP: when it slides out it tips TOWARDS the way it is leaving (20-30 deg counter-
//     clockwise), shrinks to ~85-90% and blurs, over ~5-12 frames.
const MASCOT_START_CHANCE = 0.55;
const MASCOT_SLIDE_IN_CHANCE = 0.25;
const MASCOT_SLIDE_OUT_CHANCE = 0.2;
const MASCOT_SLIDE_IN_FRAMES = 4;
const MASCOT_SLIDE_OUT_FRAMES = 7;
const SWING_CHANCE = 0.7;
const SWING_MIN_DEG = 6;
const SWING_MAX_DEG = 24;
const SWING_CW_CHANCE = 0.65;
const SWING_OMEGA = 47;       // rad/s - half a rock every ~4 frames, as measured
const SWING_DECAY = 10.5;     // 1/s - amplitude falls to ~15% within 10 frames
const SWING_POP = 0.1;        // arrives at ~90% size and pulses just past 100%
const FADE_IN_CHANCE = 0.3;   // some arrivals also fade up over ~3 frames
const POSE_SWAP_CHANCE = 0.35;
const EXIT_TIP_DEG = -26;
const EXIT_SCALE = 0.87;
// The sprite canvas puts the character's feet at 94% down and its centre 53% across - the pivot.
const MASCOT_PIVOT = "53% 94%";
// Sprite BOX width. His character is nearly all head; ours is a full-body sprite with wide ears
// and wings in a padded box, so at 0.5 of the card it read visibly smaller than his on the first
// render. 0.6 puts the drawn character at roughly his size.
const MASCOT_W_FRAC = 0.6;

// Pages pair a short function word with the word after it ("by a", "that comes", "of animals")
// and leave content words alone. Replayed over the gelatin short's 46 words this reproduces 35 of
// his 36 pages; the one miss is "store shelf".
const FUNCTION_WORDS = new Set(
  (
    "a an the of to in on at by for from with and but or so is are was were be been am it its it's " +
    "this that these those as if than then can could will would just not no my your our their his " +
    "her we you i they he she me us do does did has have had into onto out up off over what who how " +
    "why when all any some very too also there here i'm you're they're we're that's what's"
  ).split(" "),
);
const PAGE_MAX_CHARS = 12;

const rand = (seed: number) => {
  // Integer hash - the sin(seed)*43758 trick is badly biased for small sequential seeds.
  let x = Math.imul(seed ^ 0x9e3779b9, 0x85ebca6b);
  x = Math.imul(x ^ (x >>> 13), 0xc2b2ae35);
  x ^= x >>> 16;
  return (x >>> 0) / 4294967296;
};

type Page = { text: string; startMs: number };

const cleanWord = (raw: string) => {
  let w = raw.trim().replace(/^["'(\[“‘]+/, "").replace(/["')\]”’]+$/, "");
  // He keeps ? and ! ("neck?", "HOW?!") and trailing "...", and drops commas and full stops.
  if (!/\.\.\.$|…$/.test(w)) w = w.replace(/[,.;:]+$/, "");
  return w;
};
const isLoud = (w: string) => {
  const letters = w.replace(/[^A-Za-z]/g, "");
  return letters.length >= 2 && letters === letters.toUpperCase();
};
const endsClause = (raw: string) => /[,.;:!?…]["')\]”’]*$/.test(raw.trim());
const isFunction = (w: string) => FUNCTION_WORDS.has(w.toLowerCase().replace(/[^a-z']/g, ""));

export const buildPages = (captions: Caption[]): Page[] => {
  const toks = captions
    .map((c) => ({ raw: c.text.trim(), word: cleanWord(c.text), startMs: c.startMs }))
    .filter((t) => t.word.length > 0);
  const pages: Page[] = [];
  let i = 0;
  while (i < toks.length) {
    const first = toks[i];
    const words = [first.word];
    let j = i + 1;
    const fits = (k: number) =>
      k < toks.length && !isLoud(toks[k].word) && [...words, toks[k].word].join(" ").length <= PAGE_MAX_CHARS;
    if (isFunction(first.word) && !isLoud(first.word) && !endsClause(first.raw) && fits(j)) {
      words.push(toks[j].word);
      j++;
      // "as a quick", "out of a": two tiny function words can carry a third.
      const second = toks[j - 1];
      if (
        isFunction(second.word) && second.word.length <= 3 && first.word.length <= 3 &&
        !endsClause(second.raw) && fits(j)
      ) {
        words.push(toks[j].word);
        j++;
      }
    }
    pages.push({ text: words.join(" "), startMs: first.startMs });
    i = j;
  }
  return pages;
};

/** A rounded rectangle with a gently wavering, hand-cut edge, as a clip-path polygon. */
const stickerPolygon = (w: number, h: number, seed: number) => {
  const r = Math.min(w, h) * CORNER_FRAC;
  const amp = Math.min(w, h) * WOBBLE_FRAC;
  const p1 = rand(seed * 3) * Math.PI * 2;
  const p2 = rand(seed * 5) * Math.PI * 2;
  const perimeter = 2 * (w + h - 4 * r) + 2 * Math.PI * r;
  const pts: string[] = [];
  let s = 0;
  const push = (x: number, y: number, nx: number, ny: number) => {
    const t = (s / perimeter) * Math.PI * 2;
    const d = amp * (0.65 * Math.sin(t * 6 + p1) + 0.35 * Math.sin(t * 13 + p2));
    pts.push(`${(x + nx * d).toFixed(1)}px ${(y + ny * d).toFixed(1)}px`);
  };
  const edge = (x0: number, y0: number, x1: number, y1: number, nx: number, ny: number) => {
    const len = Math.hypot(x1 - x0, y1 - y0);
    const n = Math.max(2, Math.round(len / 18));
    for (let k = 0; k < n; k++) {
      push(x0 + ((x1 - x0) * k) / n, y0 + ((y1 - y0) * k) / n, nx, ny);
      s += len / n;
    }
  };
  const arc = (cx: number, cy: number, a0: number) => {
    const n = 10;
    for (let k = 0; k < n; k++) {
      const a = a0 + (Math.PI / 2) * (k / n);
      push(cx + Math.cos(a) * r, cy + Math.sin(a) * r, Math.cos(a), Math.sin(a));
      s += (Math.PI / 2) * r / n;
    }
  };
  edge(r, 0, w - r, 0, 0, -1);
  arc(w - r, r, -Math.PI / 2);
  edge(w, r, w, h - r, 1, 0);
  arc(w - r, h - r, 0);
  edge(w - r, h, r, h, 0, 1);
  arc(r, h - r, Math.PI / 2);
  edge(0, h - r, 0, r, -1, 0);
  arc(r, r, Math.PI);
  return `polygon(${pts.join(", ")})`;
};

type Shot = {
  beat: RenderBeat;
  startMs: number;
  cardW: number;
  cardH: number;
  cardCx: number;
  cardCy: number;
  grow: boolean;
  seed: number;
};

const shotFor = (beat: RenderBeat, nowMs: number, visualSeed: number): Shot => {
  const beatLen = Math.max(1, beat.endMs - beat.startMs);
  const shotCount = Math.max(1, Math.floor(beatLen / SHOT_MS));
  const shotLen = beatLen / shotCount;
  const shotIndex = Math.min(shotCount - 1, Math.max(0, Math.floor((nowMs - beat.startMs) / shotLen)));
  const seed = visualSeed * 8191 + beat.index * 977 + shotIndex * 131 + 5;

  const big = rand(seed * 17) < CARD_BIG_CHANCE;
  let cardW = big ? CARD_BIG_W : CARD_W_MIN + rand(seed) * (CARD_W_MAX - CARD_W_MIN);
  // A re-frame of the same picture punches IN, the way his does - never a smaller copy.
  if (shotIndex > 0) cardW = Math.min(CARD_BIG_W, Math.max(cardW, CARD_W_MIN + 0.75 * (CARD_W_MAX - CARD_W_MIN)));

  const picAspect = beat.panelAspect ?? 1;
  const white = !!beat.isCutout;
  // The white card's picture box is (1 - 2*side) wide by (1 - top - bottom) tall, so the card
  // that fits a picture of aspect p has aspect p * (1 - top - bottom) / (1 - 2*side).
  const aspect = white
    ? Math.min(WHITE_ASPECT_MAX, Math.max(WHITE_ASPECT_MIN,
        (picAspect * (1 - WHITE_PIC_TOP - WHITE_PIC_BOTTOM)) / (1 - 2 * WHITE_PIC_SIDE)))
    : Math.min(MEDIA_ASPECT_MAX, Math.max(MEDIA_ASPECT_MIN, picAspect));
  let cardH = cardW / aspect;
  if (cardH > CARD_MAX_H) {
    cardH = CARD_MAX_H;
    cardW = Math.min(cardW, cardH * aspect);
  }

  return {
    beat,
    startMs: beat.startMs + shotIndex * shotLen,
    cardW,
    cardH,
    cardCx: CARD_CENTRE_X + (rand(seed * 29) - 0.5) * 12,
    cardCy: CARD_CENTRE_Y + (rand(seed * 19) - 0.5) * 2 * CARD_Y_JITTER,
    grow: rand(seed * 13) < GROW_CHANCE,
    seed,
  };
};

type MascotRun = {
  from: number;
  to: number;
  topFrac: number;
  slideIn: boolean;
  slideOut: boolean;
  swing: boolean;
  fadeIn: boolean;
};

/**
 * Mascot appearances as runs of consecutive beats (pictures). A run starts and ends on a beat
 * boundary, which is a picture cut - that is where his cat arrives and leaves.
 */
const mascotRuns = (beats: RenderBeat[], visualSeed: number) => {
  const runs: MascotRun[] = [];
  let k = 0;
  let on = rand(visualSeed * 31 + 3) < MASCOT_START_CHANCE;
  while (k < beats.length) {
    const r = rand(visualSeed * 131 + k * 17 + (on ? 1 : 2));
    // On for 1 picture half the time, 2 most of the rest, occasionally 3 (~1.3s median at our
    // ~1.2s pictures). Off for 3-6, which gives him his ~2 appearances in a 13-picture video.
    const len = on ? (r < 0.5 ? 1 : r < 0.9 ? 2 : 3) : 3 + Math.floor(r * 4);
    if (on) {
      runs.push({
        from: k,
        to: Math.min(beats.length - 1, k + len - 1),
        topFrac: 0.36 + r * 0.16,
        // Never on the opening frame: when he opens with the cat it is simply there.
        slideIn: k > 0 && rand(visualSeed * 211 + k * 29) < MASCOT_SLIDE_IN_CHANCE,
        slideOut: rand(visualSeed * 223 + k * 31) < MASCOT_SLIDE_OUT_CHANCE,
        swing: rand(visualSeed * 227 + k * 37) < SWING_CHANCE,
        fadeIn: rand(visualSeed * 229 + k * 41) < FADE_IN_CHANCE,
      });
    }
    k += len;
    on = !on;
  }
  return runs;
};

export const ListStage: React.FC<{
  beats: RenderBeat[];
  captions: Caption[];
  /** Optional sticker that hangs off the card's left edge. */
  mascot?: string | null;
  /** Every pose of that sticker, main pose first; it switches pose on some cuts. */
  mascotPoses?: string[] | null;
  /** Stable per-video seed so separate renders do not repeat one edit sequence. */
  visualSeed?: number;
}> = ({ beats, captions, mascot, mascotPoses, visualSeed = 0 }) => {
  const frame = useCurrentFrame();
  const { fps } = useVideoConfig();
  const nowMs = (frame / fps) * 1000;

  // The beat on screen: the latest one that has started. Beats are contiguous now (one voice
  // take, cut at word times), and holding the latest also covers the tail after the last word.
  let beatIndex = 0;
  for (let k = beats.length - 1; k >= 0; k--) {
    if (nowMs >= beats[k].startMs) {
      beatIndex = k;
      break;
    }
  }
  const beat = beats[beatIndex];
  if (!beat) return null;

  const shot = shotFor(beat, nowMs, visualSeed);
  const { cardW, cardH } = shot;
  const white = !!beat.isCutout;

  const shotFrame = ((nowMs - shot.startMs) / 1000) * fps;
  const scale = shot.grow
    ? interpolate(shotFrame, [0, GROW_FRAMES], [GROW_FROM, 1], {
        extrapolateLeft: "clamp",
        extrapolateRight: "clamp",
        easing: Easing.out(Easing.cubic),
      })
    : 1;

  // Stills on a media card get the faintest push so a held photo is not frozen; cut-outs on
  // white sit still, as his do.
  const beatT = Math.min(1, Math.max(0, (nowMs - beat.startMs) / Math.max(1, beat.endMs - beat.startMs)));
  const push = white ? 1 : 1 + 0.03 * beatT;

  // Captions: global pages, each held until the next one starts.
  const pages = buildPages(captions);
  let page: Page | undefined;
  for (let k = pages.length - 1; k >= 0; k--) {
    if (nowMs >= pages[k].startMs - 1) {
      page = pages[k];
      break;
    }
  }
  const pageText = page?.text ?? "";
  let fontSize = CAPTION_EM * cardW;
  // Width estimate from Comic Sans Bold's own advances (lowercase ~0.50em, caps ~0.66em). His
  // longest pages ("theoretically", "convenience") run edge to edge; only wider ones shrink.
  const estW = (px: number) =>
    [...pageText].reduce((a, c) => a + (c >= "A" && c <= "Z" ? 0.66 : c === " " ? 0.3 : 0.5), 0) * px +
    px * CAPTION_STROKE_EM;
  if (estW(fontSize) > cardW * 1.04) fontSize = fontSize * ((cardW * 1.04) / estW(fontSize));

  // Mascot.
  const run = mascot ? mascotRuns(beats, visualSeed).find((r) => beatIndex >= r.from && beatIndex <= r.to) : undefined;
  const cardLeft = shot.cardCx - (cardW * scale) / 2;
  const cardTop = shot.cardCy - (cardH * scale) / 2;
  const mascotW = Math.min(560, Math.max(380, cardW * MASCOT_W_FRAC));
  const mascotHome = cardLeft + 0.03 * cardW * scale - 0.52 * mascotW;
  // Fully off the left edge of the frame - where a slide starts or ends.
  const mascotOffscreen = -mascotW - 20;
  const poses = mascotPoses && mascotPoses.length > 0 ? mascotPoses : mascot ? [mascot] : [];

  // Which pose is up, and when the current swing started: the appearance's first frame, or the
  // last cut where it changed pose while staying up.
  let poseIndex = 0;
  let swingFromMs = 0;
  let swinging = false;
  if (run && poses.length > 0) {
    poseIndex = Math.floor(rand(visualSeed * 307 + run.from * 41) * poses.length) % poses.length;
    swingFromMs = beats[run.from].startMs;
    swinging = run.swing;
    for (let b = run.from + 1; b <= beatIndex; b++) {
      if (poses.length > 1 && rand(visualSeed * 311 + b * 43) < POSE_SWAP_CHANCE) {
        poseIndex = (poseIndex + 1 + Math.floor(rand(visualSeed * 313 + b * 47) * (poses.length - 1))) % poses.length;
        swingFromMs = beats[b].startMs;
        swinging = true;
      }
    }
  }
  const swingSeed = visualSeed * 331 + Math.round(swingFromMs);
  const swingDeg0 =
    (rand(swingSeed) < SWING_CW_CHANCE ? 1 : -1) * (SWING_MIN_DEG + rand(swingSeed * 3) * (SWING_MAX_DEG - SWING_MIN_DEG));

  /** Where the mascot is, how far it is tipped and how big, at a moment in time (ms). */
  const mascotAt = (tMs: number) => {
    let left = mascotHome;
    let rot = 0;
    let size = 1;
    let opacity = 1;
    if (!run) return { left, rot, size, opacity };
    const sinceStart = ((tMs - beats[run.from].startMs) / 1000) * fps;
    const untilEnd = ((beats[run.to].endMs - tMs) / 1000) * fps;
    // The rock back to upright: a damped swing about the feet, with a matching size pulse.
    const swingT = (tMs - swingFromMs) / 1000;
    if (swinging && swingT >= 0 && swingT < 0.6) {
      const env = Math.exp(-SWING_DECAY * swingT) * Math.cos(SWING_OMEGA * swingT);
      rot = swingDeg0 * env;
      size = 1 - SWING_POP * env;
    }
    if (run.fadeIn && sinceStart < 3) opacity = Math.max(0.35, (sinceStart + 1) / 3);
    if (run.slideIn && sinceStart < MASCOT_SLIDE_IN_FRAMES) {
      // Snaps in from off-screen, decelerating, no overshoot.
      left = interpolate(sinceStart, [0, MASCOT_SLIDE_IN_FRAMES], [mascotOffscreen, mascotHome], {
        extrapolateLeft: "clamp",
        extrapolateRight: "clamp",
        easing: Easing.out(Easing.cubic),
      });
    } else if (run.slideOut && untilEnd < MASCOT_SLIDE_OUT_FRAMES) {
      // Leaves accelerating off to the left, tipping the way it is going and shrinking.
      left = interpolate(untilEnd, [0, MASCOT_SLIDE_OUT_FRAMES], [mascotOffscreen, mascotHome], {
        extrapolateLeft: "clamp",
        extrapolateRight: "clamp",
        easing: Easing.out(Easing.cubic),
      });
      const p = 1 - Math.max(0, untilEnd) / MASCOT_SLIDE_OUT_FRAMES;
      rot += EXIT_TIP_DEG * p * p;
      size *= 1 - (1 - EXIT_SCALE) * p;
      if (untilEnd < 2) opacity = Math.min(opacity, 0.5 + 0.25 * untilEnd);
    }
    return { left, rot, size, opacity };
  };
  const mascotNow = mascotAt(nowMs);
  // Motion blur, the cheap way: on frames where it is moving fast, two faint copies trail at
  // one-third and two-thirds of a frame back. His fast frames smear; a crisp sprite snapping
  // 15 degrees between frames reads as a flicker.
  const frameMs = 1000 / fps;
  const ghosts = [1 / 3, 2 / 3]
    .map((k, i) => ({ ...mascotAt(nowMs - k * frameMs), alpha: i === 0 ? 0.35 : 0.18 }))
    .filter((g) => Math.abs(g.rot - mascotNow.rot) > 1.5 || Math.abs(g.left - mascotNow.left) > 10);

  return (
    <>
      <div
        style={{
          position: "absolute",
          left: shot.cardCx,
          top: shot.cardCy,
          width: cardW,
          height: cardH,
          transform: `translate(-50%, -50%) scale(${scale})`,
          transformOrigin: "50% 50%",
        }}
      >
        {/* The shadow wrapper: drop-shadow follows the CLIPPED card below, so the offset shadow
            takes the hand-cut outline instead of a plain rectangle's. */}
        <div
          style={{
            position: "absolute",
            inset: 0,
            filter: `drop-shadow(${(SHADOW_DX * cardW).toFixed(1)}px ${(SHADOW_DY * cardW).toFixed(1)}px ${(
              2 * SHADOW_SIGMA * cardW
            ).toFixed(1)}px rgba(0,0,0,${SHADOW_ALPHA}))`,
          }}
        >
          <div
            style={{
              position: "absolute",
              inset: 0,
              overflow: "hidden",
              background: white ? "#fff" : "#111",
              clipPath: stickerPolygon(cardW, cardH, shot.seed),
            }}
          >
            {beat.clip ? (
              <Sequence from={Math.round((beat.startMs / 1000) * fps)} layout="none">
                <Loop durationInFrames={Math.max(1, beat.clipLoopFrames ?? Math.round(fps * 3))}>
                  <OffthreadVideo
                    src={staticFile(beat.clip)}
                    muted
                    style={{ width: "100%", height: "100%", objectFit: "cover" }}
                  />
                </Loop>
              </Sequence>
            ) : white ? (
              <Img
                src={staticFile(beat.panel)}
                style={{
                  position: "absolute",
                  left: `${WHITE_PIC_SIDE * 100}%`,
                  right: `${WHITE_PIC_SIDE * 100}%`,
                  top: `${WHITE_PIC_TOP * 100}%`,
                  bottom: `${WHITE_PIC_BOTTOM * 100}%`,
                  width: `${(1 - 2 * WHITE_PIC_SIDE) * 100}%`,
                  height: `${(1 - WHITE_PIC_TOP - WHITE_PIC_BOTTOM) * 100}%`,
                  objectFit: "contain",
                }}
              />
            ) : (
              <Img
                src={staticFile(beat.panel)}
                style={{
                  display: "block",
                  width: "100%",
                  height: "100%",
                  objectFit: "cover",
                  transform: `scale(${push})`,
                }}
              />
            )}
          </div>
        </div>

        {/* Outside the clip, so a long word can reach the card's edges the way his do. */}
        {pageText ? (
          <div
            style={{
              position: "absolute",
              top: `${CAPTION_TOP_FRAC * 100}%`,
              left: "50%",
              transform: "translateX(-50%)",
              whiteSpace: "nowrap",
              zIndex: 2,
              lineHeight: 1,
              fontFamily: `"Comic Sans MS", "Comic Sans", "Comic Neue", cursive`,
              fontWeight: 700,
              fontSize,
              color: "#fff",
              WebkitTextStroke: `${(fontSize * CAPTION_STROKE_EM).toFixed(1)}px #000`,
              paintOrder: "stroke fill",
              filter: `drop-shadow(${(fontSize * 0.03).toFixed(1)}px ${(fontSize * 0.05).toFixed(1)}px ${(
                fontSize * 0.06
              ).toFixed(1)}px rgba(0,0,0,0.45))`,
            }}
          >
            {pageText}
          </div>
        ) : null}
      </div>

      {/* In FRONT of the card, straddling its left edge, and moving with it when it resizes. It
          appears and disappears on picture cuts, rocking about its feet as it lands. */}
      {run && poses.length > 0
        ? [...ghosts, { ...mascotNow, alpha: 1 }].map((m, i) => (
            <Img
              key={`mascot-${i}`}
              src={staticFile(poses[poseIndex])}
              style={{
                position: "absolute",
                left: m.left,
                top: cardTop + run.topFrac * cardH * scale - 0.12 * mascotW,
                width: mascotW,
                zIndex: 3,
                opacity: m.opacity * m.alpha,
                transformOrigin: MASCOT_PIVOT,
                transform: `rotate(${m.rot.toFixed(2)}deg) scale(${m.size.toFixed(4)})`,
                // Only the solid copy casts a shadow; on the trail it would double up.
                filter: m.alpha === 1 ? "drop-shadow(6px 8px 10px rgba(0,0,0,0.35))" : undefined,
              }}
            />
          ))
        : null}
    </>
  );
};
