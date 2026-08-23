import type { Caption } from "@remotion/captions";

export type ChartPoint = { label: string; value: number };
export type BeatVisual = {
  type: "stat" | "chart" | "comparison" | "tip";
  stat?: { from?: string | null; to: string; label: string; delta?: string | null; breakdown?: ChartPoint[] | null } | null;
  chart?: { kind: "bar" | "line" | "pie" | "donut" | "progress" | "ranking"; unit?: string | null; points: ChartPoint[] } | null;
  comparison?: { leftTitle: string; left: string[]; rightTitle: string; right: string[]; result?: string | null } | null;
  tip?: { text: string; icon?: string | null; points?: string[] | null } | null;
};

export type RenderBeat = {
  index: number;
  caption: string;
  panel: string; // filename in public dir, e.g. "panel_0.png" (meme only)
  audio: string; // filename in public dir, e.g. "beat_0.wav"
  startMs: number;
  endMs: number;
  options?: string[] | null;
  visual?: BeatVisual | null; // finance only
};

export type RenderFakeComment = {
  username: string;
  text: string;
  startMs: number;
};

/** One PRE-WRAPPED display line of a reposted social post, with the moment it appears. */
export type PostLine = {
  text: string;
  startMs: number;
  /** First line of a paragraph — draws the wider inter-paragraph gap above itself. */
  newParagraph?: boolean;
};

/**
 * A real social post reproduced as the video's only visual (contentType "repost").
 * Layout/motion defaults live in PostCard.tsx (POST_CARD); these fields only override them.
 */
export type RenderPost = {
  /** All chrome fields are optional — omit them all for a bare image on the card. */
  authorName?: string | null;
  authorDegree?: string | null;
  headline?: string | null;
  linkText?: string | null;
  /** The small line under the link, e.g. "4d · 🌐". */
  meta?: string | null;
  /** Filenames in the public dir. */
  avatar?: string | null;
  image?: string | null;
  lines: PostLine[];

  /**
   * Slice mode. When imageWidth is set, the card IS the screenshot: the header plus each
   * revealed caption line is one growing crop of it, and the media below is a second crop.
   * Nothing is re-typeset, so a screenshot that already contains its text shows it once.
   */
  imageWidth?: number | null;
  imageHeight?: number | null;
  headerBottom?: number | null;
  /** Stacked crops of the screenshot, each appearing at its own moment. */
  blocks?: {
    top: number;
    bottom: number;
    startMs: number;
    text?: string | null;
    /** Horizontal crop in image pixels; absent means full width. */
    xLeft?: number | null;
    xRight?: number | null;
    /** Which column, on a comparison. */
    side?: "left" | "right" | null;
    /** Blocks sharing a group are laid out side by side, not stacked. */
    group?: number | null;
  }[];
  /** Flow diagram: panels sit where they are in the picture and light up in arrow order. */
  diagram?: boolean;
  /** Two-column memes: where the gutter is, and which half the voice is on. */
  gutterFrac?: number | null;
  spotlights?: { startMs: number; side: "left" | "right" | "full" }[];
  /** Sampled from the screenshot so the growing edge never flashes white on a dark capture. */
  cardBackground?: string | null;

  left?: number;
  width?: number;
  topStart?: number;
  topEnd?: number;
  driftStartMs?: number;
  driftPxPerSec?: number;
};

export type RenderProps = {
  width: number;
  height: number;
  fps: number;
  durationInFrames: number;
  background?: string | null;
  backgroundLoopFrames?: number | null;
  contentType?: "meme" | "finance" | "repost" | "list";
  /** List shorts: sticker that perches on the card. Absent = no sticker. */
  mascot?: string | null;
  /** List shorts blur the gameplay behind the card so the card carries the eye. */
  backgroundBlur?: number | null;
  /** Set on repost videos; the post card replaces the panel entirely. */
  post?: RenderPost | null;
  music?: string | null;
  musicVolume?: number;
  musicLoopFrames?: number | null;
  title: string;
  format: string;
  beats: RenderBeat[];
  captions: Caption[];
  fakeComment?: RenderFakeComment | null;
};

/**
 * Shared card geometry (px, in the 1080x1920 frame). The white panel card and
 * the caption overlay are positioned against these so captions land in the
 * card's top strip — above the rage face, like the reference videos.
 */
export const CARD = {
  width: 770,
  height: 920,
  top: 420, // top edge of the card (kept centered around y=880, same as before)
  get left() {
    return (1080 - this.width) / 2;
  },
  captionPadTop: 32, // caption strip offset from card top
  captionHeight: 200,
  imagePadTop: 215, // panel image starts this far below the card top
};

/**
 * Deterministic per-card tilt + offset so stacked cards look like a hand-dropped
 * pile (matching the reference videos). The caption uses the SAME transform for the
 * active card so the text always rides on top of it.
 */
export const tiltForIndex = (i: number): number => {
  const seq = [-3, 2.5, -2, 3.5, -1.5, 2, -3.5, 1.5];
  return seq[((i % seq.length) + seq.length) % seq.length];
};

export const offsetForIndex = (i: number): { dx: number; dy: number } => {
  const xs = [-14, 10, -8, 16, -12, 6, -16, 12];
  const ys = [-10, 8, 12, -6, 4, -12, 6, -8];
  const k = ((i % xs.length) + xs.length) % xs.length;
  return { dx: xs[k], dy: ys[k] };
};

export const defaultProps: RenderProps = {
  width: 1080,
  height: 1920,
  fps: 30,
  durationInFrames: 300,
  background: null,
  title: "Preview",
  format: "relatable_chaos",
  beats: [],
  captions: [],
  fakeComment: null,
};
