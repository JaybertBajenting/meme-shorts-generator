import { interpolate, useCurrentFrame, useVideoConfig } from "remotion";
import { FIN_FONT } from "./font";

/** Two-column X-vs-Y with icon rows + a takeaway banner. Fills the card. */
export const Comparison: React.FC<{
  data: { leftTitle: string; left: string[]; rightTitle: string; right: string[]; result?: string | null };
  startFrame: number;
}> = ({ data, startFrame }) => {
  const frame = useCurrentFrame();
  const { fps } = useVideoConfig();
  const local = Math.max(0, frame - startFrame);
  const slide = interpolate(local, [0, fps * 0.4], [70, 0], { extrapolateLeft: "clamp", extrapolateRight: "clamp" });
  const op = interpolate(local, [0, 8], [0, 1], { extrapolateLeft: "clamp", extrapolateRight: "clamp" });
  const bannerOp = interpolate(local, [fps * 0.5, fps * 0.8], [0, 1], { extrapolateLeft: "clamp", extrapolateRight: "clamp" });

  const Col = (title: string, items: string[], accent: string, dx: number) => (
    <div style={{ flex: 1, display: "flex", flexDirection: "column", gap: 12, transform: `translateX(${dx}px)`, opacity: op }}>
      <div style={{ background: accent, color: "#fff", fontWeight: 900, fontSize: 34, padding: "14px 0", borderRadius: 14, textAlign: "center", fontFamily: FIN_FONT }}>
        {title}
      </div>
      {items.slice(0, 4).map((it, i) => (
        <div key={i} style={{ display: "flex", alignItems: "center", gap: 14, background: "#f6f8fb", borderRadius: 12, padding: "14px 16px" }}>
          <div style={{ width: 16, height: 16, borderRadius: "50%", background: accent, flex: "0 0 auto" }} />
          <div style={{ fontSize: 27, fontWeight: 700, color: "#0f172a", lineHeight: 1.15 }}>{it}</div>
        </div>
      ))}
    </div>
  );

  return (
    <div style={{ width: "100%", height: "100%", display: "flex", flexDirection: "column", padding: "16px 34px 24px",
      boxSizing: "border-box", fontFamily: FIN_FONT }}>
      <div style={{ flex: 1, display: "flex", alignItems: "flex-start", gap: 16, position: "relative" }}>
        {Col(data.leftTitle, data.left, "#dc2626", -slide)}
        <div style={{ alignSelf: "center", fontSize: 40, fontWeight: 900, color: "#0f172a", fontFamily: FIN_FONT }}>VS</div>
        {Col(data.rightTitle, data.right, "#16a34a", slide)}
      </div>
      {data.result ? (
        <div style={{ marginTop: 14, background: "linear-gradient(90deg,#0f172a,#1e293b)", color: "#fff", borderRadius: 14,
          padding: "18px 22px", textAlign: "center", fontSize: 32, fontWeight: 900, opacity: bannerOp }}>
          {data.result}
        </div>
      ) : null}
    </div>
  );
};
