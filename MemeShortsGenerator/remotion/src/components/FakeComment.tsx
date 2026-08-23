import { interpolate, spring, useCurrentFrame, useVideoConfig } from "remotion";
import { CARD, RenderFakeComment } from "../types";

/**
 * The fabricated viewer comment that delivers the punchline near the end.
 *
 * Anchored flush under the panel (not floating near the bottom of the frame): on the
 * reference channel the comment reads as part of the same post, and it is spoken aloud as
 * the closing line rather than being silent decoration.
 */
export const FakeComment: React.FC<{ comment: RenderFakeComment }> = ({ comment }) => {
  const frame = useCurrentFrame();
  const { fps } = useVideoConfig();

  const startFrame = Math.round((comment.startMs / 1000) * fps);
  const local = frame - startFrame;
  if (local < 0) return null;

  const s = spring({ frame: local, fps, config: { damping: 14, mass: 0.7, stiffness: 120 } });
  const translateY = interpolate(s, [0, 1], [40, 0]);
  const opacity = interpolate(local, [0, 6], [0, 1], { extrapolateRight: "clamp" });

  const initial = (comment.username[0] || "?").toUpperCase();

  return (
    <div
      style={{
        position: "absolute",
        left: CARD.left,
        width: CARD.width,
        top: CARD.top + CARD.height + 14, // flush under the panel
        transform: `translateY(${translateY}px)`,
        opacity,
        background: "#fff",
        borderRadius: 6,
        padding: "20px 22px",
        display: "flex",
        gap: 16,
        boxShadow: "0 14px 40px rgba(0,0,0,0.40)",
        fontFamily: "system-ui, -apple-system, Segoe UI, Roboto, sans-serif",
      }}
    >
      <div
        style={{
          flex: "0 0 auto",
          width: 64,
          height: 64,
          borderRadius: "50%",
          background: "linear-gradient(135deg,#7c5cff,#00d3a7)",
          color: "#fff",
          fontWeight: 800,
          fontSize: 30,
          display: "flex",
          alignItems: "center",
          justifyContent: "center",
        }}
      >
        {initial}
      </div>

      <div style={{ flex: 1, minWidth: 0 }}>
        <div style={{ fontWeight: 800, fontSize: 28, color: "#111" }}>{comment.username}</div>
        <div style={{ fontSize: 30, color: "#222", marginTop: 4, lineHeight: 1.25 }}>{comment.text}</div>
        <div style={{ display: "flex", gap: 26, marginTop: 14, color: "#888", fontSize: 24, fontWeight: 600 }}>
          <span>Reply</span>
          <span>♡ {1 + (comment.username.length * 137) % 4000}</span>
          <span>{1 + (comment.text.length % 9)}mo</span>
        </div>
      </div>
    </div>
  );
};
