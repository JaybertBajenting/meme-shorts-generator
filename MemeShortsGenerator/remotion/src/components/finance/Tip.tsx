import { interpolate, spring, useCurrentFrame, useVideoConfig } from "remotion";
import { FIN_FONT } from "./font";

/** Big icon + headline + supporting bullets — fills the card. */
export const Tip: React.FC<{ tip: { text: string; icon?: string | null; points?: string[] | null }; startFrame: number }> = ({ tip, startFrame }) => {
  const frame = useCurrentFrame();
  const { fps } = useVideoConfig();
  const local = Math.max(0, frame - startFrame);
  const s = spring({ frame: local, fps, config: { damping: 12, mass: 0.6, stiffness: 140 } });
  const scale = interpolate(s, [0, 1], [0.5, 1]);
  const points = (tip.points ?? []).slice(0, 4);

  return (
    <div style={{ width: "100%", height: "100%", display: "flex", flexDirection: "column", alignItems: "center",
      justifyContent: "center", gap: 22, padding: "10px 56px 36px", boxSizing: "border-box", textAlign: "center",
      fontFamily: FIN_FONT }}>
      {tip.icon ? <div style={{ fontSize: 130, lineHeight: 1, transform: `scale(${scale})` }}>{tip.icon}</div> : null}
      <div style={{ fontSize: 46, fontWeight: 800, color: "#0f172a", lineHeight: 1.18, fontFamily: FIN_FONT }}>{tip.text}</div>

      {points.length > 0 ? (
        <div style={{ width: "100%", display: "flex", flexDirection: "column", gap: 14, marginTop: 8 }}>
          {points.map((pt, i) => {
            const op = interpolate(local, [fps * (0.3 + i * 0.18), fps * (0.5 + i * 0.18)], [0, 1], { extrapolateLeft: "clamp", extrapolateRight: "clamp" });
            const dx = interpolate(op, [0, 1], [-30, 0]);
            return (
              <div key={i} style={{ display: "flex", alignItems: "center", gap: 14, background: "#ecfdf5", border: "2px solid #bbf7d0",
                borderRadius: 14, padding: "16px 20px", opacity: op, transform: `translateX(${dx}px)` }}>
                <div style={{ fontSize: 30, color: "#16a34a", fontWeight: 900 }}>✓</div>
                <div style={{ fontSize: 30, fontWeight: 700, color: "#0f172a", textAlign: "left", lineHeight: 1.15 }}>{pt}</div>
              </div>
            );
          })}
        </div>
      ) : null}
    </div>
  );
};
