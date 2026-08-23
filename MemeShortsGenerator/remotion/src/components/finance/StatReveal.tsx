import { interpolate, spring, useCurrentFrame, useVideoConfig } from "remotion";
import { ChartPoint } from "../../types";
import { FIN_FONT } from "./font";

const BAR_COLORS = ["#2563eb", "#16a34a", "#f59e0b", "#7c3aed", "#e11d48"];

/** Big number reveal + change badge + a supporting breakdown bar list — fills the card. */
export const StatReveal: React.FC<{
  stat: { from?: string | null; to: string; label: string; delta?: string | null; breakdown?: ChartPoint[] | null };
  startFrame: number;
}> = ({ stat, startFrame }) => {
  const frame = useCurrentFrame();
  const { fps } = useVideoConfig();
  const local = Math.max(0, frame - startFrame);

  const parsed = parseNumber(stat.to);
  const s = spring({ frame: local, fps, config: { damping: 18, mass: 0.7, stiffness: 90 } });
  const shown = parsed ? formatNumber(parsed.prefix, parsed.value * s, parsed.decimals, parsed.suffix) : stat.to;
  const op = interpolate(local, [0, 6], [0, 1], { extrapolateLeft: "clamp", extrapolateRight: "clamp" });
  const p = interpolate(local, [0, fps * 0.7], [0, 1], { extrapolateLeft: "clamp", extrapolateRight: "clamp" });

  // Auto-fit the big number to the card width (no overflow). Size from the FINAL string length.
  const finalStr = parsed ? formatNumber(parsed.prefix, parsed.value, parsed.decimals, parsed.suffix) : stat.to;
  const AVAIL = 654; // card width 770 - padding
  const numFont = Math.max(40, Math.min(108, Math.floor(AVAIL / (Math.max(1, finalStr.length) * 0.62))));

  const breakdown = (stat.breakdown ?? []).slice(0, 4);
  const max = breakdown.length ? Math.max(...breakdown.map((b) => Math.abs(b.value)), 1) : 1;

  return (
    <div style={{ width: "100%", height: "100%", display: "flex", flexDirection: "column", alignItems: "center",
      justifyContent: "center", gap: 14, padding: "10px 50px 30px", boxSizing: "border-box", textAlign: "center",
      fontFamily: FIN_FONT }}>
      {stat.from ? <div style={{ fontSize: 32, fontWeight: 800, color: "#94a3b8" }}>{stat.from}</div> : null}

      <div style={{ fontSize: numFont, fontWeight: 800, color: "#16a34a", lineHeight: 1, whiteSpace: "nowrap",
        fontFamily: FIN_FONT, letterSpacing: "-1px", opacity: op }}>{shown}</div>
      {stat.delta ? (
        <div style={{ background: "#dcfce7", color: "#15803d", fontWeight: 900, fontSize: 30, padding: "6px 16px", borderRadius: 999 }}>
          {stat.delta}
        </div>
      ) : null}

      {stat.label ? <div style={{ fontSize: 34, fontWeight: 800, color: "#0f172a" }}>{stat.label}</div> : null}

      {breakdown.length > 0 ? (
        <div style={{ width: "100%", display: "flex", flexDirection: "column", gap: 16, marginTop: 16 }}>
          {breakdown.map((b, i) => (
            <div key={i} style={{ display: "flex", alignItems: "center", gap: 16 }}>
              <div style={{ width: 240, textAlign: "right", fontSize: 28, fontWeight: 700, color: "#334155" }}>{b.label}</div>
              <div style={{ flex: 1, height: 38, background: "#eef2f7", borderRadius: 10, overflow: "hidden" }}>
                <div style={{ width: `${(Math.abs(b.value) / max) * 100 * p}%`, height: "100%", background: BAR_COLORS[i % BAR_COLORS.length], borderRadius: 10 }} />
              </div>
              <div style={{ width: 130, textAlign: "left", fontSize: 30, fontWeight: 900, color: "#0f172a" }}>{fmtCompact(b.value)}</div>
            </div>
          ))}
        </div>
      ) : null}
    </div>
  );
};

function parseNumber(s: string): { prefix: string; value: number; suffix: string; decimals: number } | null {
  const m = s.match(/^([^0-9.-]*)\s*(-?[0-9][0-9,]*\.?[0-9]*)\s*(.*)$/);
  if (!m) return null;
  const raw = m[2].replace(/,/g, "");
  const value = parseFloat(raw);
  if (!isFinite(value)) return null;
  const decimals = raw.includes(".") ? (raw.split(".")[1]?.length ?? 0) : 0;
  return { prefix: m[1] ?? "", value, suffix: m[3] ?? "", decimals };
}

function formatNumber(prefix: string, value: number, decimals: number, suffix: string): string {
  const fixed = value.toFixed(decimals);
  const [intPart, dec] = fixed.split(".");
  return `${prefix}${intPart.replace(/\B(?=(\d{3})+(?!\d))/g, ",")}${dec ? "." + dec : ""}${suffix}`;
}

function fmtCompact(v: number): string {
  if (Math.abs(v) >= 1000) return (v / 1000).toFixed(v % 1000 === 0 ? 0 : 1) + "k";
  return String(v);
}
