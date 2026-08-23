import { createTikTokStyleCaptions, type Caption } from "@remotion/captions";
import { useCurrentFrame, useVideoConfig } from "remotion";
import { CARD, RenderBeat, offsetForIndex, tiltForIndex } from "../types";

/**
 * The CURRENT beat's full caption, drawn in the card's top strip with per-word
 * highlighting (karaoke) from @remotion/captions. The whole caption box carries the
 * SAME tilt + offset as the active card so the text always sits on top of it.
 *
 * Grouping is per beat (we run createTikTokStyleCaptions on just the active beat's
 * words) — the library groups by time gaps and would otherwise bleed across beats.
 */
export const CaptionTrack: React.FC<{ captions: Caption[]; beats: RenderBeat[]; flat?: boolean }> = ({
  captions,
  beats,
  flat = false,
}) => {
  const frame = useCurrentFrame();
  const { fps } = useVideoConfig();
  const nowMs = (frame / fps) * 1000;

  // After the last beat's audio ends the panel would otherwise sit with an empty title strip
  // (the comment is still on screen and being read). The reference channel keeps its title up
  // for the entire video, so hold the final beat's caption rather than clearing it.
  const beat =
    beats.find((b) => nowMs >= b.startMs && nowMs < b.endMs) ??
    (beats.length > 0 && nowMs >= beats[beats.length - 1].endMs ? beats[beats.length - 1] : undefined);
  if (!beat) return null;

  const beatCaps = captions.filter((c) => c.startMs >= beat.startMs - 1 && c.endMs <= beat.endMs + 1);
  if (beatCaps.length === 0) return null;

  const { pages } = createTikTokStyleCaptions({
    captions: beatCaps,
    combineTokensWithinMilliseconds: 10_000_000, // whole beat = one page
  });
  const page = pages[0];
  if (!page) return null;

  // flat = the fixed PanelBoard, which never tilts or shifts; the caption must sit still on it.
  const tilt = flat ? 0 : tiltForIndex(beat.index);
  const { dx, dy } = flat ? { dx: 0, dy: 0 } : offsetForIndex(beat.index);

  return (
    // Card-sized box matching the active card's transform, so the caption rides it.
    <div
      style={{
        position: "absolute",
        left: CARD.left,
        top: CARD.top,
        width: CARD.width,
        height: CARD.height,
        transformOrigin: "center center",
        transform: `translate(${dx}px, ${dy}px) rotate(${tilt}deg)`,
        zIndex: 1000,
        pointerEvents: "none",
      }}
    >
      <div
        style={{
          position: "absolute",
          left: 28,
          top: CARD.captionPadTop,
          width: CARD.width - 56,
          height: CARD.captionHeight,
          display: "flex",
          flexWrap: "wrap",
          alignContent: "flex-start",
          justifyContent: "center",
          gap: "4px 14px",
          fontFamily: '"Arial Black", Impact, system-ui, sans-serif',
          fontWeight: 900,
          fontSize: 50,
          lineHeight: 1.05,
          textAlign: "center",
        }}
      >
        {page.tokens.map((tok, i) => {
          const active = nowMs >= tok.fromMs && nowMs < tok.toMs;
          return (
            <span
              key={i}
              style={{
                color: active ? "#e11d48" : "#111",
                transform: active ? "scale(1.07)" : "none",
                transition: "transform 0.05s",
                display: "inline-block",
              }}
            >
              {tok.text.trim()}
            </span>
          );
        })}
      </div>
    </div>
  );
};
