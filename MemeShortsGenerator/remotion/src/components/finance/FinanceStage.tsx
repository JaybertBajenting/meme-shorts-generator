import { createTikTokStyleCaptions, type Caption } from "@remotion/captions";
import { AbsoluteFill, interpolate, useCurrentFrame, useVideoConfig } from "remotion";
import { RenderBeat } from "../../types";
import { FinanceVisual } from "./FinanceVisual";
import { FIN_FONT } from "./font";

// Finance card geometry — same size as the meme card (770x920, centered ~y=880), but
// NO tilt/stack (clean & professional vs the meme pile).
const FCARD = { width: 770, height: 920, top: 420, get left() { return (1080 - this.width) / 2; }, capTop: 30, capHeight: 175, bodyTop: 205 };
const TRANS = 12; // crossfade length (frames) between beats

/**
 * Finance layout: ONE persistent white card. The card itself never disappears — only its
 * inner content (caption + visual) crossfades/slides when the beat changes, so transitions
 * feel natural instead of the panel popping out and back in.
 */
export const FinanceStage: React.FC<{ beats: RenderBeat[]; captions: Caption[] }> = ({ beats, captions }) => {
  const frame = useCurrentFrame();
  const { fps } = useVideoConfig();
  const nowMs = (frame / fps) * 1000;
  const msToF = (ms: number) => Math.round((ms / 1000) * fps);

  let ci = beats.findIndex((b) => nowMs >= b.startMs && nowMs < b.endMs);
  if (ci < 0) ci = beats.length - 1;
  if (ci < 0) return null;

  const beat = beats[ci];
  const prev = ci > 0 ? beats[ci - 1] : null;
  const local = frame - msToF(beat.startMs);
  const t = interpolate(local, [0, TRANS], [0, 1], { extrapolateLeft: "clamp", extrapolateRight: "clamp" });

  // Card fades in once at the very start of the video, then stays solid forever.
  const cardOpacity = interpolate(frame, [0, 10], [0, 1], { extrapolateLeft: "clamp", extrapolateRight: "clamp" });

  return (
    <AbsoluteFill>
      <AbsoluteFill style={{ backgroundColor: "rgba(8,12,24,0.55)" }} />
      <div
        style={{
          position: "absolute",
          left: FCARD.left, top: FCARD.top, width: FCARD.width, height: FCARD.height,
          background: "#ffffff", borderRadius: 28, boxShadow: "0 24px 70px rgba(0,0,0,0.45)",
          overflow: "hidden", opacity: cardOpacity,
        }}
      >
        <div style={{ height: 16, background: "linear-gradient(90deg,#16a34a,#2563eb)" }} />

        {/* Outgoing content slides up & fades out; incoming slides up from below & fades in. */}
        {prev && t < 1 ? (
          <BeatLayer beat={prev} captions={captions} nowMs={nowMs} visualStartFrame={msToF(prev.startMs)} opacity={1 - t} translateY={-22 * t} />
        ) : null}
        <BeatLayer beat={beat} captions={captions} nowMs={nowMs} visualStartFrame={msToF(beat.startMs)} opacity={t} translateY={22 * (1 - t)} />
      </div>
    </AbsoluteFill>
  );
};

const BeatLayer: React.FC<{
  beat: RenderBeat; captions: Caption[]; nowMs: number; visualStartFrame: number; opacity: number; translateY: number;
}> = ({ beat, captions, nowMs, visualStartFrame, opacity, translateY }) => (
  <div style={{ position: "absolute", inset: "16px 0 0 0", opacity, transform: `translateY(${translateY}px)` }}>
    <FinanceCaption captions={captions} beat={beat} nowMs={nowMs} />
    <div style={{ position: "absolute", top: FCARD.bodyTop, left: 0, width: "100%", height: FCARD.height - FCARD.bodyTop }}>
      {beat.visual ? <FinanceVisual visual={beat.visual} startFrame={visualStartFrame} /> : null}
    </div>
  </div>
);

const FinanceCaption: React.FC<{ captions: Caption[]; beat: RenderBeat; nowMs: number }> = ({ captions, beat, nowMs }) => {
  const beatCaps = captions.filter((c) => c.startMs >= beat.startMs - 1 && c.endMs <= beat.endMs + 1);
  if (beatCaps.length === 0) return null;
  const { pages } = createTikTokStyleCaptions({ captions: beatCaps, combineTokensWithinMilliseconds: 10_000_000 });
  const page = pages[0];
  if (!page) return null;

  return (
    <div
      style={{
        position: "absolute", top: FCARD.capTop, left: 40, width: FCARD.width - 80, height: FCARD.capHeight,
        display: "flex", flexWrap: "wrap", alignContent: "flex-start", justifyContent: "center", gap: "2px 12px",
        fontFamily: FIN_FONT, fontWeight: 700, fontSize: 44, lineHeight: 1.12, textAlign: "center",
      }}
    >
      {page.tokens.map((tok, i) => {
        const active = nowMs >= tok.fromMs && nowMs < tok.toMs;
        return <span key={i} style={{ color: active ? "#16a34a" : "#0f172a" }}>{tok.text.trim()}</span>;
      })}
    </div>
  );
};
